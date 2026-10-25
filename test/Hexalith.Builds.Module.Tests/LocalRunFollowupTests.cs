// <copyright file="LocalRunFollowupTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Guards retry ownership and first-observation recovery at lifecycle publication boundaries.</summary>
public sealed class LocalRunFollowupTests
{
    /// <summary>A failed final publication restores a listed retry owner after active state was deleted.</summary>
    /// <returns>The discoverable retry and successful later cleanup assertions.</returns>
    [Fact]
    public async Task CompletedPublicationFailureRestoresDiscoverableTrackingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "test-result.json");
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "stub-docker"), $"if [ \"$1\" = ps ]; then rm -f '{path}'; mkdir -p '{path}'; fi; exit 0");
        TaskCompletionSource<bool> deleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(engine.StateStore.Directory, state.RunId + ".json") { EnableRaisingEvents = true };
        watcher.Deleted += (_, _) => _ = deleted.TrySetResult(true);
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        (await deleted.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeTrue();
        CompositionRunState retry = (await engine.StateStore.TryReadAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        retry.CleanupDisposition.ShouldBe("incomplete");
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(engine.StateStore, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Environments.ShouldNotBeNull().Single().OwnerRunId.ShouldBe(state.RunId);
        Directory.Delete(path);
        _ = CompositionTestFiles.CreateStubDocker(workspace.Root);
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    /// <summary>Partial startup expiry has issued lifecycle identities and a reader-valid v1 result.</summary>
    /// <returns>The original deadline and startup-timeout result assertions.</returns>
    [Fact]
    public async Task SelectedSetupExpiryPublishesIssuedStartupTimeoutAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        DateTimeOffset requested = DateTimeOffset.UtcNow.AddMinutes(-11);
        CompositionStartResult start = await engine.ReportSetupTimeoutAsync(requested, new CompositionEffectiveDeadline(600, false, null, null)).ConfigureAwait(true);
        string runId = start.RunId.ShouldNotBeNull();
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(Path.Combine(options.StateDirectory, "diagnostics", runId, "test-result.json"), TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(JsonSerializer.Serialize(start.Result));
        JsonElement document = result.Document!.Value;
        document.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("startup-timeout");
        document.GetProperty("bindings").GetProperty("run").GetProperty("runId").GetString().ShouldBe(runId);
        document.GetProperty("bindings").GetProperty("deadlines").GetProperty("startup").GetProperty("originAt").GetDateTimeOffset().ShouldBe(requested);
    }

    /// <summary>Missing active state recovers exact remainder IDs from the durable first observation.</summary>
    /// <returns>The retained known container and retryable uncertainty assertions.</returns>
    [Fact]
    public async Task MissingActiveStateAndFailedDiscoveryPreserveDurableRemainderAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string container = new('c', 64);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([container], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        engine.StateStore.Delete(state.RunId).ShouldBeTrue();
        CompositionTestFiles.WriteScript(options.DockerCommand, "exit 1");
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Remaining.Unverified.ShouldBeTrue();
        down.Remaining.ContainerIds.ShouldContain(container);
        (await engine.ReadCommittedOutcomeAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull().RemainingResources.ContainerIds.ShouldContain(container);
    }

    /// <summary>Malformed nested resource arrays refuse teardown before any failed scan can merge them.</summary>
    /// <returns>The structured uncertainty and preserved tracking assertions.</returns>
    [Fact]
    public async Task NullContainerArrayReturnsRetryableOwnershipRefusalAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state with { RemainingResources = new CompositionRunResources([], []) }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        System.Text.Json.Nodes.JsonNode stored = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(engine.StateStore.PathFor(state.RunId), TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        stored["remainingResources"]!["containerIds"] = null;
        await File.WriteAllTextAsync(engine.StateStore.PathFor(state.RunId), stored.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionTestFiles.WriteScript(options.DockerCommand, "exit 1");
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        down.Remaining.Unverified.ShouldBeTrue();
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeTrue();
    }

    /// <summary>The exact-down CLI delegates nullable workspace validation to the engine.</summary>
    /// <returns>The parsed structured CLI refusal.</returns>
    [Fact]
    public async Task ExactDownCliRefusesNullWorkspaceWithoutThrowingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        await new CompositionRunStateStore(options.StateDirectory).WriteAsync(state with { Workspace = null! }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["down", "--run-id", state.RunId, "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
        exit.ShouldNotBe(0);
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("remainingResources").GetProperty("unverified").GetBoolean().ShouldBeTrue();
    }

    /// <summary>A returned session cannot resurrect tracking after exact cleanup completed before return.</summary>
    /// <returns>The deterministic concurrent cleanup and child-not-launched assertions.</returns>
    [Fact]
    public async Task CleanupBeforeStartReturnCannotRecreateOwnerAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        using Process appHost = new();
        CompositionRunSession? session = null;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            SelectedTest = null,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            ContinuousIntegrationOverride = false,
        };
        options = options with
        {
            SelectedPlatformStartOverride = async (_, entry, token) =>
            {
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = entry }, manifest, workspace.Root).ConfigureAwait(false);
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                session = new CompositionRunSession(plan, ready, "private-key", appHost);
                CompositionEngine owner = new(options with { SelectedTest = entry });
                CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, entry.ManifestHash, entry.RequestedAtUtc) with { SelectedTest = entry, AttemptId = plan.AttemptId, EnvironmentId = plan.EnvironmentId, ExecutionContext = JsonSerializer.SerializeToElement(new { kind = "local" }) };
                await owner.StateStore.WriteAsync(issued, token).ConfigureAwait(false);
                (await owner.DownAsync(plan.RunId, token).ConfigureAwait(false)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        _ = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        _ = session.ShouldNotBeNull();
        File.Exists(new CompositionRunStateStore(options.StateDirectory).PathFor(session.RunId)).ShouldBeFalse();
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
        using JsonDocument document = JsonDocument.Parse(output.ToString());
        document.RootElement.GetProperty("status").GetString().ShouldBe("cancelled");
    }

    /// <summary>A committed pass remains authoritative when every optional result publication throws.</summary>
    /// <returns>The recovered first pass, cleanup decision and visible recording failure.</returns>
    [Fact]
    public async Task CommandRecoversCommittedPassAfterOptionalRecordingFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        const string script = """
            import os, pathlib
            pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>')
            blocked = pathlib.Path(__file__).parent / 'state' / 'diagnostics' / os.environ['HEXALITH_G4_RUN_ID'] / 'test-result.json'
            blocked.mkdir(parents=True)
            """;
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), script, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using Process appHost = new();
        CompositionRunSession? session = null;
        int cleanups = 0;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            SelectedTest = null,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            ContinuousIntegrationOverride = false,
            SelectedPlatformStartOverride = async (_, entry, token) =>
            {
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = entry }, manifest, workspace.Root).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                session = new CompositionRunSession(plan, ready, "private-key", appHost);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = (owned, _) =>
            {
                cleanups++;
                Directory.Delete(owned.Plan.Workspace, true);
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], [])));
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe(0, output.ToString());
        cleanups.ShouldBe(1);
        _ = session.ShouldNotBeNull();
        ModuleTestRunRecord terminal = (await new CompositionEngine(options).ReadCommittedOutcomeAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        terminal.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        terminal.FailureRetained.ShouldBeFalse();
        output.ToString().ShouldContain("HXT008");
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
    }

    private static ToolCommandResult Failed() => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
}
