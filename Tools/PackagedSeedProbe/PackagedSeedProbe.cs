// <copyright file="PackagedSeedProbe.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Qualification;

using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>Exercises the EventStore seed from one actual distributable module-tool archive.</summary>
internal static class PackagedSeedProbe
{
    private const string _seedEntry = "tools/net10.0/any/g4-host/bin/EventStoreHost/Hexalith.Builds.Module.EventStoreHost.dll";

    /// <summary>Loads only the archive's seed, tests both subscription modes and retains their observations.</summary>
    /// <param name="args">The archive, its expected version and a new evidence path.</param>
    /// <returns>A task representing completion; assertion failures terminate the probe unsuccessfully.</returns>
    /// <exception cref="ArgumentException">The required inputs or new output path are absent.</exception>
    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 3 || File.Exists(args[2]))
        {
            throw new ArgumentException("Provide an archive, expected version and new evidence path.");
        }

        string packagePath = Path.GetFullPath(args[0]);
        string packageHash = HashFile(packagePath);
        string evidencePath = Path.GetFullPath(args[2]);
        string scratch = Path.Combine(AppContext.BaseDirectory, "seed-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(scratch);
        try
        {
            (Assembly seed, string id, string version, string seedHash, string loadedHash, string seedPath) = PreparePackage(packagePath, args[1], scratch);
            MethodInfo probe = typeof(PackagedSeedProbe).GetMethod(nameof(CheckAsync))!
                .MakeGenericMethod(seed.EntryPoint!.DeclaringType!);
            List<object> checks = [];
            bool allPassed = true;
            foreach (bool declarative in new[] { false, true })
            {
                try
                {
                    (bool passed, object observation) = await ((Task<(bool Passed, object Observation)>)probe.Invoke(null, [declarative])!).ConfigureAwait(false);
                    allPassed &= passed;
                    checks.Add(observation);
                }
                catch (Exception exception) when (exception is InvalidOperationException or JsonException or KeyNotFoundException)
                {
                    allPassed = false;
                    checks.Add(new { declarative, errorType = exception.GetType().FullName });
                }
            }

            Require(HashFile(packagePath) == packageHash && HashFile(seedPath) == seedHash, "Package seed bytes changed during qualification.");
            string evidence = JsonSerializer.Serialize(new
            {
                schema = "hexalith.g4-packaged-seed-subscriptions.v1",
                status = allPassed ? "passed" : "failed",
                package = new { id, version, sha256 = packageHash },
                seed = new { entry = _seedEntry, sha256 = seedHash, loadedSha256 = loadedHash },
                checks,
            });
            FileStream output = new(evidencePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(false);
            await output.WriteAsync(Encoding.UTF8.GetBytes(evidence + "\n")).ConfigureAwait(false);
            return allPassed ? 0 : 1;
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>Preserves Platform's HTTP and receiver-metadata assertions for the loaded entry point.</summary>
    /// <typeparam name="TEntryPoint">The actual packaged seed's entry-point type.</typeparam>
    /// <param name="declarative">Whether Dapr owns the subscriptions declaratively.</param>
    /// <returns>The observed HTTP and endpoint metadata result.</returns>
    public static async Task<(bool Passed, object Observation)> CheckAsync<TEntryPoint>(bool declarative)
        where TEntryPoint : class
    {
        WebApplicationFactory<TEntryPoint> sourceFactory = new();
        await using ConfiguredAsyncDisposable sourceLifetime = sourceFactory.ConfigureAwait(false);
        WebApplicationFactory<TEntryPoint> factory = sourceFactory.WithWebHostBuilder(builder => builder.UseEnvironment("Development").ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Dapr:DeclarativeSubscriptions"] = declarative.ToString(),
            ["EventStore:ProjectionChanges:PubSubName"] = "configured-seed-events",
            ["EventStore:OpenApi:Enabled"] = "false",
            ["Authentication:JwtBearer:Issuer"] = "hexalith-test",
            ["Authentication:JwtBearer:Audience"] = "hexalith-eventstore",
            ["Authentication:JwtBearer:SigningKey"] = "test-only-seed-subscription-signing-key-at-least-32-bytes",
            ["Authentication:JwtBearer:AllowedAlgorithms:0"] = "HS256",
            ["Authentication:JwtBearer:RequireHttpsMetadata"] = "false",
            ["APP_API_TOKEN"] = "test-only-packaged-seed-channel-token",
        })).ConfigureServices(services =>
        {
            // Same narrow no-sidecar override as Platform's existing HTTP fixture.
            int index = services.ToList().FindIndex(service => service.ServiceType.FullName == "Hexalith.EventStore.Indexes.AdminOperationalIndexHostedService");
            Require(index >= 0 && services.Count > index + 2, "Missing startup index writer registration.");
            ServiceDescriptor hosted = services[index + 2];
            Require(hosted.ServiceType == typeof(IHostedService) && hosted.ImplementationFactory is not null, "Startup index writer hosted alias changed.");
            _ = services.Remove(hosted);
        }));
        await using ConfiguredAsyncDisposable factoryLifetime = factory.ConfigureAwait(false);
        using HttpClient client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        using HttpResponseMessage untokened = await client.GetAsync(new Uri("/dapr/subscribe", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        client.DefaultRequestHeaders.Add("dapr-api-token", "test-only-packaged-seed-channel-token");
        using HttpResponseMessage response = await client.GetAsync(new Uri("/dapr/subscribe", UriKind.Relative), timeout.Token).ConfigureAwait(false);
        RouteEndpoint receiver = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Single(endpoint => endpoint.RoutePattern.RawText == "projections/changed");
        object[] topics = [.. receiver.Metadata.Where(item => item.GetType().GetInterfaces().Any(type => type.FullName == "Dapr.ITopicMetadata"))];
        string? receiverPubsub = topics.Length == 1 ? (string?)topics[0].GetType().GetProperty("PubsubName")!.GetValue(topics[0]) : null;
        int subscriptionCount = 0;
        string? subscriptionPubsub = null;
        if (!declarative && (int)response.StatusCode == 200)
        {
            using JsonDocument subscriptions = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            JsonElement[] projections = [.. subscriptions.RootElement.EnumerateArray().Where(subscription => subscription.GetProperty("topic").GetString() == "*.*.projection-changed")];
            subscriptionCount = projections.Length;
            subscriptionPubsub = projections.Length == 1
                ? projections[0].EnumerateObject().Single(property => property.Name.Equals("pubsubname", StringComparison.OrdinalIgnoreCase)).Value.GetString()
                : null;
        }

        bool passed = (int)untokened.StatusCode == (declarative ? 404 : 401) && (declarative
            ? (int)response.StatusCode == 404 && topics.Length == 0
            : (int)response.StatusCode == 200 && subscriptionCount == 1 && subscriptionPubsub == "configured-seed-events" && topics.Length == 1 && receiverPubsub == "configured-seed-events");

        return (passed, new
        {
            declarative,
            discoveryStatusCode = (int)response.StatusCode,
            untokenedDiscoveryStatusCode = (int)untokened.StatusCode,
            projectionSubscriptionCount = subscriptionCount,
            subscriptionPubsubName = subscriptionPubsub,
            receiverPresent = true,
            receiverTopicMetadataCount = topics.Length,
            receiverPubsubName = receiverPubsub,
            startupIndexWriterAliasRemoved = true,
        });
    }

    /// <summary>Extracts and binds one packaged entry point without source-host resolution.</summary>
    /// <param name="packagePath">The distributable archive.</param>
    /// <param name="expectedVersion">The expected archive version.</param>
    /// <param name="scratch">The private extraction directory.</param>
    /// <returns>The loaded seed and its exact package identities.</returns>
    private static (Assembly Seed, string Id, string Version, string SeedHash, string LoadedHash, string SeedPath) PreparePackage(string packagePath, string expectedVersion, string scratch)
    {
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ZipArchiveEntry nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
        using Stream nuspecStream = nuspec.Open();
        XElement metadata = XDocument.Load(nuspecStream).Root!.Elements().Single(element => element.Name.LocalName == "metadata");
        string id = metadata.Elements().Single(element => element.Name.LocalName == "id").Value;
        string version = metadata.Elements().Single(element => element.Name.LocalName == "version").Value;
        Require(id == "Hexalith.Builds.Module.Cli" && version == expectedVersion, "Unexpected module-tool package identity.");
        ZipArchiveEntry seedEntry = archive.Entries.Single(entry => entry.FullName == _seedEntry);
        using Stream seedStream = seedEntry.Open();
        string seedHash = Convert.ToHexString(SHA256.HashData(seedStream));
        ZipFile.ExtractToDirectory(packagePath, scratch);
        string seedPath = Path.Combine(scratch, _seedEntry);
        AssemblyDependencyResolver resolver = new(seedPath);
        AssemblyLoadContext.Default.Resolving += (_, name) => resolver.ResolveAssemblyToPath(name) is string path
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path)
            : null;
        Assembly seed = AssemblyLoadContext.Default.LoadFromAssemblyPath(seedPath);
        Require(Path.GetFullPath(seed.Location) == seedPath, "The loaded seed is outside the extracted package.");
        string loadedHash = HashFile(seed.Location);
        Require(loadedHash == seedHash, "The loaded seed differs from its archive entry.");

        // MVC Testing requires the entry-point deps file beside the probe and an explicit
        // content-root manifest. Neither file changes the packaged seed or its dependencies.
        File.Copy(Path.ChangeExtension(seedPath, ".deps.json"), Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(seedPath) + ".deps.json"));
        File.WriteAllText("MvcTestingAppManifest.json", JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [seed.GetName().FullName] = Path.GetDirectoryName(seedPath)!,
        }));
        return (seed, id, version, seedHash, loadedHash, seedPath);
    }

    /// <summary>Computes an exact file identity.</summary>
    /// <param name="path">The file to hash.</param>
    /// <returns>The SHA-256 digest.</returns>
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    /// <summary>Rejects an unmet qualification assertion.</summary>
    /// <param name="condition">The required condition.</param>
    /// <param name="message">The failure explanation.</param>
    /// <exception cref="InvalidOperationException">A required assertion failed.</exception>
    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
