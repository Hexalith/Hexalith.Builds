// <copyright file="NativeEndpointCredentialTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;

using Shouldly;

using Xunit;

/// <summary>Checks actual allocation admission, strict private-channel inspection and evidence exclusion.</summary>
public sealed class NativeEndpointCredentialTests
{
    private const string _secret = "fixture-distinct-private-sidecar-credential";
    private static readonly Uri _invocation = new("http://localhost:18081/v1.0/invoke/declared-owner/method/api/v1/");

    /// <summary>A prepared logical endpoint must match its actual healthy owner and sidecar allocation.</summary>
    /// <param name="fault">The changed allocation fact, or none for a valid binding.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("module")]
    [InlineData("unhealthy")]
    [InlineData("origin")]
    [InlineData("duplicate")]
    public void PreparedInvocationRequiresActualAllocation(string fault)
    {
        CompositionResourceReadiness[] resources = Resources();
        if (fault == "module")
        {
            resources[1] = resources[1] with { ModuleId = "other" };
        }
        else if (fault == "unhealthy")
        {
            resources[1] = resources[1] with { State = "Running" };
        }
        else if (fault == "origin")
        {
            resources[1] = resources[1] with { Endpoints = [new("http", "http", new Uri("http://localhost:18082"))] };
        }
        else if (fault == "duplicate")
        {
            resources[0] = resources[0] with { Endpoints = [new("dapr-invocation", "http", _invocation)] };
        }

        if (fault == "none")
        {
            CompositionNativeHttpEndpoints.Bind(resources, [Endpoint()])[0].Endpoints.Single().Uri.ShouldBe(_invocation);
        }
        else
        {
            _ = Should.Throw<InvalidDataException>(() => CompositionNativeHttpEndpoints.Bind(resources, [Endpoint()]));
        }
    }

    /// <summary>Only the exact private run, tuple, URI and constrained header can reach report inspection.</summary>
    /// <param name="fault">The private-channel mutation, or none for the accepted exact value.</param>
    /// <returns>The bounded channel inspection assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An unsupported fixture mutation is requested.</exception>
    [Theory]
    [InlineData("none")]
    [InlineData("run")]
    [InlineData("tuple")]
    [InlineData("uri")]
    [InlineData("header")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("path")]
    [InlineData("public-mode")]
    public async Task PrivateChannelRequiresExactBindingsAsync(string fault)
    {
        using NativeEndpointCredentialWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
        string path = Path.Combine(plan.Workspace, NativeTestHandoff.EndpointCredentialsFileName);
        JsonObject document = Document(plan);
        JsonObject entry = document["endpoints"]![0]!.AsObject();
        switch (fault)
        {
            case "none":
            case "missing":
            case "public-mode":
                break;
            case "run":
                document["runId"] = "other-run";
                break;
            case "tuple":
                entry["moduleId"] = "other";
                break;
            case "uri":
                entry["uri"] = "http://localhost:18082/v1.0/invoke/declared-owner/method/api/v1/";
                break;
            case "header":
                entry["headerName"] = "Authorization";
                break;
            case "unknown":
                document["extra"] = "refuse";
                break;
            case "duplicate":
                document["endpoints"]!.AsArray().Add(entry.DeepClone());
                break;
            case "path":
                path = Path.Combine(plan.Workspace, "other.json");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        await File.WriteAllTextAsync(path, document.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            if (fault == "public-mode")
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
            }
        }

        if (fault == "missing")
        {
            File.Delete(path);
        }

        CompositionReadiness readiness = Ready(plan, path);
        IReadOnlyList<string>? secrets = await CompositionEndpointCredentials.ReadSecretsAsync(plan, readiness, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (fault == "none" || (fault == "public-mode" && OperatingSystem.IsWindows()))
        {
            secrets.ShouldNotBeNull().ShouldBe([_secret]);
            IReadOnlyDictionary<string, string> handoff = NativeTestHandoff.Create(plan, readiness, "fixture-native-token");
            handoff[NativeTestHandoff.EndpointCredentialsPath].ShouldBe(path);
            JsonSerializer.Serialize(readiness).ShouldNotContain(_secret);
            JsonSerializer.Serialize(handoff).ShouldNotContain(_secret);
        }
        else
        {
            secrets.ShouldBeNull();
        }
    }

    /// <summary>Exact handed-off endpoint credentials cannot survive native reports or selected metadata retention.</summary>
    /// <returns>The report refusal and fixed archive allowlist assertions.</returns>
    [Fact]
    public async Task EndpointCredentialCannotEnterNativeEvidenceAsync()
    {
        using NativeEndpointCredentialWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
        string path = Path.Combine(plan.Workspace, NativeTestHandoff.EndpointCredentialsFileName);
        await File.WriteAllTextAsync(path, Document(plan).ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        IReadOnlyList<string> secrets = (await CompositionEndpointCredentials.ReadSecretsAsync(plan, Ready(plan, path), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        string reportPath = Path.Combine(plan.Workspace, "native.trx");
        await File.WriteAllTextAsync(reportPath, "<TestRun><UnitTestResult outcome=\"Passed\"><Output><StdOut>" + _secret + "</StdOut></Output></UnitTestResult></TestRun>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        (NativeTestReportLoadResult report, byte[]? bytes) = await SelectedNativeTestReport.ReadAsync(reportPath, plan.Workspace, secrets, TestContext.Current.CancellationToken).ConfigureAwait(true);
        report.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT008");
        bytes.ShouldBeNull();
        File.Exists(reportPath).ShouldBeFalse();
        string archive = Path.Combine(workspace.Root, "archive");
        CompositionWorkspace.CreatePrivateDirectory(archive);
        await File.WriteAllTextAsync(Path.Combine(plan.Workspace, "plan.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        MethodInfo copy = typeof(CompositionEngine).GetMethod("CopySelectedMetadataAsync", BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull();
        await ((Task)copy.Invoke(null, [plan.Workspace, archive, TestContext.Current.CancellationToken])!).ConfigureAwait(true);
        Directory.GetFiles(archive, "*", SearchOption.AllDirectories).Select(Path.GetFileName).ShouldBe(["selected-plan.json"]);
        File.Exists(path).ShouldBeTrue();
    }

    private static CompositionReadiness Ready(CompositionRunPlan plan, string path) =>
        new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://localhost:18080"), null, CompositionNativeHttpEndpoints.Bind(Resources(), [Endpoint()]))
        {
            NativeHttpEndpoints = [Endpoint()],
            NativeEndpointCredentialsPath = path,
        };

    private static CompositionNativeHttpEndpoint Endpoint() => new("memories", "memories", "dapr-invocation", _invocation, "memories-dapr-cli");

    private static CompositionResourceReadiness[] Resources() =>
    [
        new("memories", "project", "Healthy") { ModuleId = "memories" },
        new("memories-dapr-cli", "executable", "Healthy") { ModuleId = "memories", Endpoints = [new("http", "http", new Uri("http://localhost:18081"))] },
    ];

    private static JsonObject Document(CompositionRunPlan plan) => new()
    {
        ["schema"] = NativeTestHandoff.EndpointCredentialsSchema,
        ["runId"] = plan.RunId,
        ["syntheticTenantId"] = plan.TenantNamespace,
        ["dataIsolationId"] = plan.ResourceNamespace,
        ["endpoints"] = new JsonArray(new JsonObject
        {
            ["resourceId"] = "memories",
            ["moduleId"] = "memories",
            ["endpointName"] = "dapr-invocation",
            ["uri"] = _invocation.AbsoluteUri,
            ["headerName"] = "dapr-api-token",
            ["value"] = _secret,
        }),
    };
}
