// <copyright file="LocalRunLifecycleReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Guards the reviewed first-outcome, exact-owner cleanup and diagnostic custody defects.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class LocalRunLifecycleReviewTests
{
    /// <summary>An unreadable first outcome cannot be replaced with a later success.</summary>
    /// <param name="malformed">Whether corruption replaces an exclusive file lock.</param>
    /// <returns>The refused replacement and unchanged document assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnreadableFirstOutcomeCannotBeOverwrittenAsync(bool malformed)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "selected-test.json");
        if (malformed)
        {
            await File.WriteAllTextAsync(path, "{unreadable-first-outcome}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        FileStream? held = malformed ? null : new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using ((held ?? Stream.Null).ConfigureAwait(true))
        {
            _ = await Should.ThrowAsync<IOException>(() => engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult("completed", ToolOutcome.Passed(), []), "removed", new CompositionRunResources([], []), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(original);
    }

    /// <summary>Cleanup cannot invent owner cancellation diagnostics for an already passed test.</summary>
    /// <returns>The preserved terminal rules, cleanup and discoverable result assertions.</returns>
    [Fact]
    public async Task SuccessfulCleanupPreservesTerminalRulesAndReturnsResultPathAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolDiagnostic completed = new("HXI005", ToolPhase.Test, ToolFailureCategory.None, "Tests completed.", "test");
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult("completed", ToolOutcome.Passed(), [completed]), "unknown", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        string path = down.Result.TestResultPath.ShouldNotBeNull();
        JsonElement result = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("passed");
        string directory = Path.GetDirectoryName(path)!;
        string diagnostic = await File.ReadAllTextAsync(Path.Combine(directory, result.GetProperty("diagnostics")[0].GetProperty("reference").GetString()!), TestContext.Current.CancellationToken).ConfigureAwait(true);
        diagnostic.ShouldNotContain("HXC130");
        diagnostic.ShouldContain(first.DiagnosticRuleIds.Single());
    }

    /// <summary>Corrupt claims belonging to another recorded owner do not block exact cleanup.</summary>
    /// <returns>The independent owner and preserved peer claim assertions.</returns>
    [Fact]
    public async Task UnverifiablePeerClaimDoesNotBlockOwnedCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string peer = CompositionRunPlanFactory.NewRunId();
        engine.StateStore.Claims.Acquire(peer, ["peer-exclusive"]);
        string peerClaim = Directory.GetFiles(Path.Combine(engine.StateStore.Directory, "claims"), "*.json").Single();
        engine.StateStore.Claims.Acquire(state.RunId, ["own-exclusive"]);
        await File.WriteAllTextAsync(peerClaim, "{invalid-peer-claim}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        down.Remaining.IsEmpty.ShouldBeTrue();
        File.Exists(peerClaim).ShouldBeTrue();
        _ = Should.Throw<IOException>(() => engine.StateStore.Claims.FindOwned(peer));
    }

    /// <summary>Invalid Docker identifiers are discovery failures, never retained container identities.</summary>
    /// <returns>The explicit uncertainty and empty container assertions.</returns>
    [Fact]
    public async Task NonHexDockerOutputRemainsDiscoveryFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string docker = Path.Combine(workspace.Root, "docker");
        CompositionTestFiles.WriteScript(docker, "echo not-a-container-identifier");
        CompositionRunResources observed = await new CompositionRunResourceScanner(docker).FindAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        observed.ContainerIds.ShouldBeEmpty();
        observed.Unverified.ShouldBeTrue();
        observed.DiscoveryFailures.ShouldContain("containers");
    }

    /// <summary>A run workspace link cannot grant authority to delete the target or its data.</summary>
    /// <returns>The refusal and preserved target assertions.</returns>
    [Fact]
    public async Task SymlinkedWorkspaceCannotDeletePeerDataAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string peer = Path.Combine(workspace.Root, "peer");
        CompositionWorkspace.CreatePrivateDirectory(peer);
        string data = Path.Combine(peer, "data.txt");
        await File.WriteAllTextAsync(data, "peer-content", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string link = Path.Combine(workspace.Root, "owned-link");
        _ = Directory.CreateSymbolicLink(link, peer);
        (await CompositionWorkspace.TryDeleteAsync(link, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeFalse();
        (await File.ReadAllTextAsync(data, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("peer-content");
        _ = new DirectoryInfo(link).LinkTarget.ShouldNotBeNull();
        Directory.Delete(link);
    }

    /// <summary>Secret-bearing child copies are refused while trusted first outcomes remain publishable.</summary>
    /// <param name="file">The child-writable metadata copy.</param>
    /// <param name="archived">The corresponding retained name.</param>
    /// <returns>The omitted unsafe copy and valid result assertions.</returns>
    [Theory]
    [InlineData("plan.json", "selected-plan.json")]
    [InlineData("readiness.json", "selected-readiness.json")]
    public async Task SecretBearingMetadataCopiesAreNotRetainedAsync(string file, string archived)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(state.Workspace, file), "{\"nested\":{\"value\":\"password=fixture-secret-value\"}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        File.Exists(Path.Combine(directory, archived)).ShouldBeFalse();
        TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Execution cancellation differs from owner cancellation, even when processing has no time left.</summary>
    /// <returns>The immediately committed timeout and live metadata callback assertions.</returns>
    [Fact]
    public async Task ExecutionLifetimeExpiryCommitsFailureBeforeExpiredProcessingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "import pathlib, time\n(pathlib.Path(__file__).parent / 'execution-ready').touch()\ntime.sleep(10)", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, selected.RequestedAtUtc).ShouldNotBeNull();
        string runId = CompositionRunPlanFactory.NewRunId();
        CompositionRunPlan template = CompositionTestFiles.CreatePlan(workspace.Root);
        CompositionRunPlan isolated = CompositionRunPlanFactory.Create(runId, Path.Combine(workspace.Root, "workspace", runId), template.DaprHome, template.Ports, template.Modules, template.UiMarkers) with { SelectedTest = selected };
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(isolated, manifest, workspace.Root).ConfigureAwait(true);
        CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
        using Process appHost = new();
        CompositionRunSession session = new(plan, readiness, "private-key", appHost);
        state = state with { RunId = plan.RunId, Workspace = plan.Workspace, AttemptId = plan.AttemptId, EnvironmentId = plan.EnvironmentId, SelectedTest = selected };
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { WorkspaceRoot = Path.GetDirectoryName(plan.Workspace)! };
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using CancellationTokenSource execution = new();
        TaskCompletionSource<bool> childReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(workspace.Root, "execution-ready");
        watcher.Created += (_, _) => _ = childReady.TrySetResult(true);
        watcher.EnableRaisingEvents = true;
        ModuleTestRunRecord? first = null;
        int callbacks = 0;
        Task<NativeTestExecutionResult> executionTask = NativeTestExecutor.ExecuteAsync(
            session,
            new(selected.Entry, selected.Executor),
            manifest,
            execution.Token,
            selection: selected,
            onOutcome: async (observed, token) =>
        {
            token.IsCancellationRequested.ShouldBeFalse();
            callbacks++;
            ModuleTestRunRecord saved = (await engine.RecordTestOutcomeAsync(state.RunId, readiness.Resources, observed.Result, "retained", new CompositionRunResources([], [], true), token, executionTiming: session.Plan.ExecutionTiming, environmentBindings: session.EnvironmentBindings, terminalCause: observed.TerminalCause).ConfigureAwait(false)).ShouldNotBeNull();
            first ??= saved;
            saved.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
            saved.TerminalCause.ShouldBe("execution-timeout");
            if (callbacks == 1)
            {
                await Task.Delay(TimeSpan.FromSeconds(selected.StopTimeoutSeconds + 0.1), TestContext.Current.CancellationToken).ConfigureAwait(false);
            }
        },
            ownerCancellationToken: TestContext.Current.CancellationToken);
        (await childReady.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeTrue();
        await execution.CancelAsync().ConfigureAwait(true);
        NativeTestExecutionResult executionResult = await executionTask.ConfigureAwait(true);
        execution.IsCancellationRequested.ShouldBeTrue();
        callbacks.ShouldBe(2);
        first.ShouldNotBeNull().FailureRetained.ShouldBeTrue();
        executionResult.Result.Status.ShouldBe("failed");
    }

    /// <summary>Legacy evidence preserves exact workspace and claim leftovers without inventing discovery success.</summary>
    [Fact]
    public void LegacyEvidenceIncludesWorkspaceAndClaimIdentities()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunResources remaining = new([], [], true) { WorkspaceIds = ["workspace:" + state.RunId], ClaimIds = ["claim:" + new string('a', 64)], DiscoveryFailures = ["claims"] };
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", Failed().Outcome, DateTimeOffset.UtcNow, "remaining", remaining);
        ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(ModuleInvocationCommand.Test, workspace.Save(), null, null, null, Failed(), selected.RequestedAtUtc, DateTimeOffset.UtcNow, state.RunId) with { SelectedTestRun = record };
        using JsonDocument document = JsonDocument.Parse(ModuleRunEvidenceWriter.SerializeCanonical(evidence));
        JsonElement actual = document.RootElement.GetProperty("selectedTestRun").GetProperty("remainingResources");
        actual.GetProperty("workspaceIds")[0].GetString().ShouldBe(remaining.WorkspaceIds.Single());
        actual.GetProperty("claimIds")[0].GetString().ShouldBe(remaining.ClaimIds.Single());
        actual.GetProperty("discoveryFailures")[0].GetString().ShouldBe("claims");
        actual.GetProperty("unverified").GetBoolean().ShouldBeTrue();
        actual.GetProperty("isEmpty").GetBoolean().ShouldBeFalse();
    }

    private static ToolCommandResult Failed() => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
}
