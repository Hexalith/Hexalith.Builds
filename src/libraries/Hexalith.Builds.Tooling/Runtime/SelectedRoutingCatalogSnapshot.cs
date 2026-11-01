// <copyright file="SelectedRoutingCatalogSnapshot.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Frozen;
using System.Text.Json;

/// <summary>Holds bounded public routing observations before a native child can change its workspace.</summary>
internal static class SelectedRoutingCatalogSnapshot
{
    /// <summary>The per-file limit permits a 4 MiB canonical body plus public proof wrapper.</summary>
    public const int MaximumFileBytes = 8 * 1024 * 1024;

    /// <summary>The bundle limit includes catalogs, reader assets and at most 64 host readbacks.</summary>
    public const int MaximumBundleBytes = 32 * 1024 * 1024;

    /// <summary>Captures only the catalog instance named by the validated descriptor and original run plan.</summary>
    /// <param name="plan">The original ready run.</param>
    /// <param name="descriptorSnapshot">The descriptor bytes validated before child launch.</param>
    /// <param name="cancellationToken">The existing execution cancellation.</param>
    /// <param name="required">Whether successful owner preparation requires these public proofs.</param>
    /// <param name="knownCredentials">Every already-collected private handoff value, excluded from decoded retained keys and strings.</param>
    /// <returns>Credential-free held bytes; empty when the owner does not require routing observations.</returns>
    /// <exception cref="InvalidDataException">The identity, ownership, bounds or retained content cannot be verified.</exception>
    internal static async Task<IReadOnlyDictionary<string, byte[]>> ReadAsync(CompositionRunPlan plan, byte[] descriptorSnapshot, CancellationToken cancellationToken, bool required = false, IReadOnlyList<string>? knownCredentials = null)
    {
        try
        {
            return await ReadCoreAsync(plan, descriptorSnapshot, required, knownCredentials ?? [], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new InvalidDataException("Routing observations have missing or incorrectly typed fields.", exception);
        }
    }

    /// <summary>Reads the bounded public proof fields; structural JSON failures are normalized by the caller.</summary>
    /// <param name="plan">The original ready run.</param>
    /// <param name="descriptorSnapshot">The validated descriptor bytes.</param>
    /// <param name="required">Whether owner preparation requires routing proofs.</param>
    /// <param name="knownCredentials">The complete private handoff values.</param>
    /// <param name="cancellationToken">The existing execution cancellation.</param>
    /// <returns>The held credential-free observations.</returns>
    /// <exception cref="InvalidDataException">The proof identity, bounds or contents are invalid.</exception>
    private static async Task<IReadOnlyDictionary<string, byte[]>> ReadCoreAsync(CompositionRunPlan plan, byte[] descriptorSnapshot, bool required, IReadOnlyList<string> knownCredentials, CancellationToken cancellationToken)
    {
        string instance = plan.EnvironmentId + "-catalog";
        string directory = Path.Combine(plan.Workspace, "platform-components", instance);
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        if (!required)
        {
            return result.ToFrozenDictionary(StringComparer.Ordinal);
        }

        if (!Directory.Exists(directory))
        {
            throw new InvalidDataException("A Platform native composition requires its routing observations.");
        }

        if (!SelectedNativeTestReport.IsOwnedRegularPath(Path.Combine(directory, "catalog.json"), plan.Workspace))
        {
            throw new InvalidDataException("The descriptor catalog must be a regular file inside its owned workspace.");
        }

        using JsonDocument descriptor = JsonDocument.Parse(descriptorSnapshot);
        JsonElement bindings = descriptor.RootElement.GetProperty("bindings");
        JsonElement catalog = bindings.GetProperty("environment").GetProperty("catalog");
        if (bindings.GetProperty("run").GetProperty("environmentId").GetString() != plan.EnvironmentId
            || catalog.GetProperty("instanceId").GetString() != instance || !CompositionRunPlanFactory.IsRunId(plan.EnvironmentId))
        {
            throw new InvalidDataException("Routing observations differ from the validated descriptor instance.");
        }

        List<(string Source, string Name)> inputs = [];
        foreach (string name in new[] { "catalog.json", "receipt.json", "activation.json", "metadata-http.json", "preparation.json", "selected-closure.json" })
        {
            inputs.Add((Path.Combine(directory, name), name));
        }

        string[] readbacks = [.. Directory.EnumerateFiles(directory, "*.committed.json", SearchOption.TopDirectoryOnly).Take(65)];
        if (readbacks.Length > 64)
        {
            throw new InvalidDataException("Routing observations exceed the bounded host inventory.");
        }

        foreach (string path in readbacks)
        {
            string name = Path.GetFileName(path);
            string host = name[..^".committed.json".Length];
            if (host.Length is < 1 or > 128 || !char.IsAsciiLetterOrDigit(host[0])
                || host.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
            {
                throw new InvalidDataException("Routing readback file identity is invalid.");
            }

            inputs.Add((path, name));
        }

        foreach ((string source, string name) in new[]
        {
            (Path.Combine(plan.Workspace, "source-mapping", "mapping.json"), "mapping.json"),
            (Path.Combine(plan.Workspace, "source-mapping", "catalog-versions.json"), "catalog-versions.json"),
            (Path.Combine(plan.Workspace, "platform-components", "catalog-reader", "obj", "project.assets.json"), "reader-assets.json"),
        })
        {
            inputs.Add((source, name));
        }

        int total = 0;
        foreach ((string source, string name) in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!SelectedNativeTestReport.IsOwnedRegularPath(source, plan.Workspace))
            {
                throw new InvalidDataException("Routing observations must be regular files inside the owned workspace.");
            }

            FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(source);
            await using (stream.ConfigureAwait(false))
            {
                long length = stream.CanSeek ? stream.Length : 0;
                if (length is <= 0 or > MaximumFileBytes || total + length > MaximumBundleBytes)
                {
                    throw new InvalidDataException("Routing observations exceed the bounded snapshot contract.");
                }

                byte[] bytes = new byte[(int)length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                using JsonDocument document = JsonDocument.Parse(bytes);
                if (name == "catalog-versions.json" && document.RootElement.GetProperty("CatalogCommit").GetString() != catalog.GetProperty("commit").GetString())
                {
                    throw new InvalidDataException("Routing source metadata differs from the validated Builds catalog commit.");
                }

                if (!SelectedMetadataSafety.IsSafe(document.RootElement) || ContainsCredentialField(document.RootElement)
                    || ContainsKnownCredential(document.RootElement, knownCredentials))
                {
                    throw new InvalidDataException("Routing observations contain prohibited retained material.");
                }

                total += bytes.Length;
                result.Add(name, bytes);
            }
        }

        using JsonDocument closure = JsonDocument.Parse(result["selected-closure.json"]);
        JsonElement[] hosts = [.. closure.RootElement.GetProperty("hosts").EnumerateArray()];
        string[] requiredHosts = [.. hosts.Select(host => host.GetProperty("ResourceId").GetString()!)];
        if (closure.RootElement.GetProperty("RunId").GetString() != plan.RunId || requiredHosts.Length is < 1 or > 64
            || requiredHosts.Distinct(StringComparer.Ordinal).Count() != requiredHosts.Length
            || requiredHosts.Any(host => !result.ContainsKey(host + ".committed.json")) || readbacks.Length != requiredHosts.Length)
        {
            throw new InvalidDataException("Routing readbacks differ from the selected run host inventory.");
        }

        using JsonDocument preparation = JsonDocument.Parse(result["preparation.json"]);
        using JsonDocument metadata = JsonDocument.Parse(result["metadata-http.json"]);
        bool incomplete = preparation.RootElement.GetProperty("RunId").GetString() != plan.RunId
            || !preparation.RootElement.GetProperty("routingCatalogMetadataObserved").GetBoolean()
            || metadata.RootElement.GetProperty("status").GetInt32() != 200
            || metadata.RootElement.GetProperty("method").GetString() != "GET"
            || metadata.RootElement.GetProperty("path").GetString() != "/api/v1/routing/catalog";
        return incomplete
            ? throw new InvalidDataException("Routing native preparation and public metadata observations must have succeeded.")
            : result.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static bool ContainsKnownCredential(JsonElement value, IReadOnlyList<string> knownCredentials) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property => ContainsKnownValue(property.Name, knownCredentials)
            || ContainsKnownCredential(property.Value, knownCredentials)),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsKnownCredential(item, knownCredentials)),
        JsonValueKind.String => ContainsKnownValue(value.GetString()!, knownCredentials),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => false,
    };

    private static bool ContainsKnownValue(string value, IReadOnlyList<string> knownCredentials) =>
        knownCredentials.Any(secret => !string.IsNullOrEmpty(secret) && value.Contains(secret, StringComparison.Ordinal));

    private static bool ContainsCredentialField(JsonElement value, bool schema = false, bool properties = false) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().Any(property =>
            (CredentialName(property.Name) && HasMaterial(property.Value)
                && !(properties && property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.True or JsonValueKind.False))
            || ContainsCredentialField(
                property.Value,
                schema || property.Name is "requestSchema" or "resultSchema",
                (schema || property.Name is "requestSchema" or "resultSchema") && property.Name == "properties")),
        JsonValueKind.Array => value.EnumerateArray().Any(item => ContainsCredentialField(item, schema)),
        JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => false,
        _ => false,
    };

    private static bool CredentialName(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToUpperInvariant() is
        "TOKEN" or "ACCESSTOKEN" or "REFRESHTOKEN" or "PASSWORD" or "PASSWD" or "PWD" or "SECRET"
        or "CLIENTSECRET" or "APIKEY" or "PRIVATEKEY" or "SIGNINGKEY" or "DAPRAPITOKEN" or "APPAPITOKEN";

    private static bool HasMaterial(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !string.IsNullOrEmpty(value.GetString()),
        JsonValueKind.Object => value.EnumerateObject().Any(),
        JsonValueKind.Array => value.GetArrayLength() != 0,
        JsonValueKind.Null or JsonValueKind.Undefined => false,
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => true,
        _ => true,
    };
}
