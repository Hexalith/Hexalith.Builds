// <copyright file="PlatformExecutionReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Exercises v2 command ownership and result boundaries without launching real resources.</summary>
public sealed class PlatformExecutionReviewTests
{
    /// <summary>Verifies non-object JSON uses normal manifest diagnostics.</summary>
    /// <param name="json">The valid JSON root.</param>
    /// <returns>The command assertion.</returns>
    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task NonObjectRootsReachManifestDiagnosticsAsync(string json)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        await File.WriteAllTextAsync(path, json, TestContext.Current.CancellationToken).ConfigureAwait(true);
        PlatformManifestCommand.IsV2(path).ShouldBeFalse();
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Run, path, null, null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        writer.ToString().ShouldContain("Manifest");
    }

    /// <summary>Verifies a valid multi-module declaration selects reference composition.</summary>
    [Fact]
    public void MultiModuleDocumentsUseTheReferenceDefault()
    {
        using PlatformManifestTestWorkspace workspace = new();
        JsonNode second = workspace.Document["modules"].ShouldNotBeNull()[0]!.DeepClone();
        second = JsonNode.Parse(second.ToJsonString().Replace("sample", "second", StringComparison.Ordinal))!;
        workspace.Document["modules"]!.AsArray().Add(second);
        PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        validation.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, validation.Diagnostics.Select(d => d.Message)));
        PlatformManifestCommand.ActiveModule(validation.Declarations).ShouldBeNull();
        PlatformManifestCommand.ActiveModule([.. validation.Declarations!.Take(1)]).ShouldBe("sample");
    }

    /// <summary>Verifies output failure and cancellation preserve bounded cleanup ownership.</summary>
    /// <param name="failure">The result-writing failure.</param>
    /// <returns>The cleanup assertion.</returns>
    [Theory]
    [InlineData("output")]
    [InlineData("cancel")]
    [InlineData("success")]
    [InlineData("evidence")]
    public async Task SessionRemainsOwnedUntilSuccessfulRunOutputAsync(string failure)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string manifest = workspace.Save();
        using CancellationTokenSource cancellation = new();
        using Process process = new();
        CompositionRunSession session = Session(workspace.Root, process);
        int cleanupCalls = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            PlatformStartOverride = async (_, _) =>
            {
                if (failure == "cancel")
                {
                    await cancellation.CancelAsync().ConfigureAwait(false);
                }

                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), session.RunId, session);
            },
            PlatformDownOverride = (_, token) =>
            {
                token.ShouldBe(CancellationToken.None);
                cleanupCalls++;
                return Task.FromResult(PassedCleanup());
            },
        };
        TextWriter writer = failure == "output" ? new PlatformExecutionFailingWriter() : new StringWriter();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        async Task<int> ExecuteAsync() => await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Run, manifest, null, null, failure == "evidence" ? "../outside.json" : null, ToolOutputFormat.Json, writer, cancellation.Token, "child.dll", compositionOptions: options).ConfigureAwait(true);
        if (failure == "output")
        {
            _ = await Should.ThrowAsync<IOException>(ExecuteAsync).ConfigureAwait(true);
        }
        else
        {
            ToolExitCode expected = failure switch
            {
                "cancel" => ToolExitCode.Cancelled,
                "evidence" => ToolExitCode.EvidenceSchemaOrPolicy,
                _ => ToolExitCode.Success,
            };
            (await ExecuteAsync().ConfigureAwait(true)).ShouldBe((int)expected);
            if (failure == "evidence")
            {
                using JsonDocument output = JsonDocument.Parse(writer.ToString() ?? string.Empty);
                output.RootElement.GetProperty("runId").GetString().ShouldBe(session.RunId);
            }
        }

        cleanupCalls.ShouldBe(failure == "success" ? 0 : 1);
    }

    /// <summary>Verifies failed test teardown retains its diagnostics and run identity.</summary>
    /// <returns>The result assertion.</returns>
    [Fact]
    public async Task TestRefusalRetainsFailedCleanupAndRunIdentityAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using Process process = new();
        CompositionRunSession session = Session(workspace.Root, process);
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            PlatformStartOverride = (_, _) => Task.FromResult(new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), session.RunId, session)),
            PlatformDownOverride = (_, token) =>
            {
                token.ShouldBe(CancellationToken.None);
                ToolDiagnostic failed = new("HXR029", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Cleanup retained resources.", "down");
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(failed.Phase, failed.Category, failed.RuleId, ToolExitCode.TopologyOrLifecycle), [failed]), new CompositionRunResources([], [])));
            },
        };
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), null, null, "artifacts/v2.json", ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle);
        using JsonDocument result = JsonDocument.Parse(writer.ToString());
        result.RootElement.GetProperty("runId").GetString().ShouldBe(session.RunId);
        result.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("ruleId").GetString()).ShouldContain("HXR029");
        using JsonDocument evidence = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "artifacts", "v2.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        evidence.RootElement.GetProperty("runId").GetString().ShouldBe(session.RunId);
        evidence.RootElement.GetProperty("testCounts").GetProperty("reported").GetBoolean().ShouldBeFalse();
        evidence.RootElement.GetProperty("invocation").GetProperty("profile").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>Verifies repeated down is idempotent and preserves foreign runs and identity refusals.</summary>
    /// <returns>The teardown assertions.</returns>
    [Fact]
    public async Task DownIsIdempotentButRefusesMalformedOrAmbiguousIdentityAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stub Docker boundary uses a POSIX script.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string manifest = workspace.Save();
        string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true)));
        CompositionEngineOptions options = Options(workspace.Root) with { DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        const string foreign = "fedcba9876543210fedcba9876543210";
        async Task StateAsync(string run, string selectedHash) => await engine.StateStore.WriteAsync(new CompositionRunState(CompositionRunState.SupportedSchema, run, CompositionRunStatus.Ready, selectedHash, Path.Combine(options.WorkspaceRoot, run), null, null, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await StateAsync(CompositionTestFiles.RunId, hash).ConfigureAwait(true);
        await StateAsync(foreign, "foreign").ConfigureAwait(true);
        foreach (string? run in new[] { CompositionTestFiles.RunId, CompositionTestFiles.RunId, null })
        {
            StringWriter writer = new();
            await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
            (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, manifest, null, null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", run, options).ConfigureAwait(true)).ShouldBe(0);
        }

        File.Exists(engine.StateStore.PathFor(foreign)).ShouldBeTrue();
        foreach (string malformed in new[] { "../escape", string.Empty })
        {
            StringWriter writer = new();
            await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
            (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, manifest, null, null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", malformed, options).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.UsageOrManifest);
            writer.ToString().ShouldContain("HXR024");
        }

        await StateAsync(CompositionTestFiles.RunId, hash).ConfigureAwait(true);
        await StateAsync(foreign, hash).ConfigureAwait(true);
        StringWriter ambiguous = new();
        await using ConfiguredAsyncDisposable ambiguousLifetime = ambiguous.ConfigureAwait(true);
        (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, manifest, null, null, null, ToolOutputFormat.Json, ambiguous, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.UsageOrManifest);
        File.Exists(engine.StateStore.PathFor(foreign)).ShouldBeTrue();
    }

    /// <summary>Verifies dispatch reads the selected v2 bytes after direct-reference restoration.</summary>
    /// <returns>The dispatch assertion.</returns>
    [Fact]
    public async Task MissingDirectV2ManifestIsRestoredBeforeDispatchAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withDirectManifest: true).ConfigureAwait(true);
        using PlatformManifestTestWorkspace declaration = new();
        string dependency = Path.Combine(fixture.Directory, "dependency");
        await File.WriteAllTextAsync(Path.Combine(dependency, "module.json"), declaration.Document.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(dependency, "README.md"), "Declared task fixture; never execute.", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(dependency, "add", "module.json", "README.md").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(dependency, "commit", "-m", "test: select v2 declaration").ConfigureAwait(true);
        string commit = (await WorkspaceGitFixture.RunGitAsync(dependency, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--cacheinfo", $"160000,{commit},references/Hexalith.Dep").ConfigureAwait(true);
        string manifest = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "module.json");
        File.Exists(manifest).ShouldBeFalse();
        int starts = 0;
        CompositionEngineOptions options = Options(declaration.Root) with
        {
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                PlatformManifestCommand.IsV2(manifest).ShouldBeTrue();
                ToolDiagnostic unavailable = new("HXR014", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "Fixture stops before resources.", "apphost");
                return Task.FromResult(new CompositionStartResult(new ToolCommandResult("unavailable", ToolOutcome.Passed().Fail(unavailable.Phase, unavailable.Category, unavailable.RuleId, ToolExitCode.PrerequisiteUnavailable), [unavailable]), null, null));
            },
        };
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Run, manifest, null, null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.PrerequisiteUnavailable, writer.ToString());
        starts.ShouldBe(1);
        writer.ToString().ShouldContain("HXR014");
        writer.ToString().ShouldNotContain("HXM005");
    }

    private static CompositionEngineOptions Options(string root) => new("unused.dll", "child.dll") { StateDirectory = Path.Combine(root, "state"), WorkspaceRoot = Path.Combine(root, "runs") };

    private static CompositionRunSession Session(string root, Process process)
    {
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
        return new CompositionRunSession(plan, new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:12345"), null, []), "secret", process);
    }

    private static CompositionDownResult PassedCleanup() => new(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], []));
}