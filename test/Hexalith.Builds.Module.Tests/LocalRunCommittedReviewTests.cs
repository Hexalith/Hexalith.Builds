// <copyright file="LocalRunCommittedReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Guards committed-state lifecycle review boundaries against durable publication and cleanup races.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class LocalRunCommittedReviewTests
{
    /// <summary>Exact cleanup cannot reissue an owner's published attempt and environment during resumed initialization.</summary>
    /// <param name="mode">The local artifact mode.</param>
    /// <returns>The immutable owner and result identity assertions.</returns>
    [Theory]
    [InlineData("source")]
    [InlineData("package")]
    public async Task InitializationAfterExactCleanupPreservesIssuedIdentitiesAsync(string mode)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace, mode);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        MethodInfo initialize = typeof(CompositionEngine).GetMethod("InitializeStateAsync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
        CompositionRunState issued = await ((Task<CompositionRunState>)initialize.Invoke(engine, [state.RunId, selected.ManifestHash, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null), TestContext.Current.CancellationToken])!).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult("completed", ToolOutcome.Passed(), []), "unknown", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        byte[] published = await File.ReadAllBytesAsync(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Task<CompositionRunState> ResumeAsync() => (Task<CompositionRunState>)initialize.Invoke(engine, [state.RunId, selected.ManifestHash, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null), TestContext.Current.CancellationToken])!;
        _ = await Should.ThrowAsync<IOException>(ResumeAsync).ConfigureAwait(true);
        CompositionRunState owner = (await CompositionDocumentStore.TryReadAsync<CompositionRunState>(Path.Combine(directory, "owner.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        owner.AttemptId.ShouldBe(issued.AttemptId);
        owner.EnvironmentId.ShouldBe(issued.EnvironmentId);
        (await File.ReadAllBytesAsync(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(published);
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeFalse();
    }

    /// <summary>Interruption before Docker's first response retains the last committed container identity.</summary>
    /// <returns>The returned and durable incomplete cleanup assertions.</returns>
    [Fact]
    public async Task InterruptedFirstDiscoveryKeepsKnownContainerAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string container = new('c', 64);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([container], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionTestFiles.WriteScript(options.DockerCommand, "sleep 30");
        using CancellationTokenSource bound = new(TimeSpan.FromMilliseconds(250));
        using Process appHost = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected };
        CompositionRunSession session = new(plan, new CompositionReadiness(CompositionReadiness.SupportedSchema, state.RunId, new Uri("http://127.0.0.1:20012/"), null, []), "private-key", appHost);
        CompositionDownResult down = await engine.DownWithinBudgetAsync(session, bound.Token).ConfigureAwait(true);
        down.Remaining.Unverified.ShouldBeTrue();
        down.Remaining.ContainerIds.ShouldContain(container);
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeTrue();
    }

    /// <summary>Publication failure does not hide an authoritative failure from listing or affect verified peers.</summary>
    /// <returns>The reconciled failure, retention and leftover assertions.</returns>
    [Fact]
    public async Task ListingReconcilesCommittedFailureAfterResultPublicationFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        CompositionWorkspace.CreatePrivateDirectory(Path.Combine(directory, "test-result.json"));
        string container = new('d', 64);
        _ = await Should.ThrowAsync<IOException>(() => engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([container], []), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(engine.StateStore, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        CompositionRunListingEntry entry = listed.Environments.ShouldNotBeNull().Single();
        entry.FailureRunIds.ShouldBe([state.RunId]);
        entry.Retention.ShouldBe("failure-retained");
        entry.RemainingResources.ShouldNotBeNull().ContainerIds.ShouldContain(container);
    }

    /// <summary>A completion interrupted before v1 publication keeps a discoverable retry entry without claiming completion.</summary>
    /// <returns>The interrupted transaction listing and exact retry assertions.</returns>
    [Fact]
    public async Task InterruptedCompletionRemainsListedUntilVersionedPublicationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ModuleTestRunRecord completing = first with { CleanupDisposition = "complete", CleanupAttemptId = CompositionRunPlanFactory.NewRunId(), ResourceDisposition = "removed" };
        await CompositionDocumentStore.WriteAsync(Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "selected-test.json"), completing, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(engine.StateStore, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        CompositionRunListingEntry entry = listed.Environments.ShouldNotBeNull().Single();
        entry.OwnerRunId.ShouldBe(state.RunId);
        entry.Cleanup.ShouldBe("incomplete");
        entry.RemainingResources.ShouldNotBeNull().Unverified.ShouldBeTrue();
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeTrue();
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeFalse();
    }

    /// <summary>Later archive and cleanup errors remain in referenced durable diagnostics without changing a passed terminal cause.</summary>
    /// <returns>The immutable first outcome and evolving diagnostic assertions.</returns>
    [Fact]
    public async Task LaterDiagnosticsPreserveFirstTerminalRulesAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult("completed", ToolOutcome.Passed(), []), "unknown", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ToolDiagnostic archive = new("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Archive unavailable.", "evidence");
        ToolDiagnostic cleanup = new("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Cleanup unavailable.", "down");
        ToolDiagnostic cancelled = new("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "Synthetic stop observation.", "down");
        ToolCommandResult later = new("failed", ToolOutcome.Passed().Fail(cleanup.Phase, cleanup.Category, cleanup.RuleId, ToolExitCode.TopologyOrLifecycle), [archive, cleanup, cancelled]);
        ModuleTestRunRecord recorded = (await engine.RecordTestOutcomeAsync(state.RunId, [], later, "remaining", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken, cleanupDisposition: "incomplete", cleanupAttemptId: CompositionRunPlanFactory.NewRunId()).ConfigureAwait(true)).ShouldNotBeNull();
        recorded.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        recorded.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        recorded.DiagnosticRuleIds.ShouldBeEmpty();
        recorded.LaterDiagnosticRuleIds.ShouldBe(["HXR023", "HXT008"]);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        result.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("tests-completed");
        string reference = result.GetProperty("diagnostics")[0].GetProperty("reference").GetString().ShouldNotBeNull();
        using JsonDocument diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, reference), TestContext.Current.CancellationToken).ConfigureAwait(true));
        diagnostic.RootElement.GetProperty("ruleIds").EnumerateArray().Select(rule => rule.GetString()).ShouldBe(["HXR023", "HXT008"]);
        diagnostic.RootElement.GetProperty("terminalRuleIds").GetArrayLength().ShouldBe(0);
    }

    /// <summary>Exact down retains its actual cleanup failure after the resource stopping allowance has expired.</summary>
    /// <returns>The unchanged pass and durably referenced cleanup-error assertions.</returns>
    [Fact]
    public async Task ExactDownRetainsCleanupDiagnosticsAfterStoppingExpiryAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult("completed", ToolOutcome.Passed(), []), "unknown", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionTestFiles.WriteScript(options.DockerCommand, "exit 1");
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        result.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("passed");
        result.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe("incomplete");
        string reference = result.GetProperty("diagnostics")[0].GetProperty("reference").GetString().ShouldNotBeNull();
        using JsonDocument diagnostic = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, reference), TestContext.Current.CancellationToken).ConfigureAwait(true));
        diagnostic.RootElement.GetProperty("ruleIds").EnumerateArray().Select(rule => rule.GetString()).ShouldContain("HXR023");
        diagnostic.RootElement.GetProperty("ruleIds").EnumerateArray().Select(rule => rule.GetString()).ShouldNotContain("HXC130");
    }

    /// <summary>Null readiness entries in a committed outcome return retryable uncertainty before teardown can consume them.</summary>
    /// <returns>The structured refusal and preserved workspace assertions.</returns>
    [Fact]
    public async Task NullCommittedResourceEntryReturnsStructuredUncertaintyAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "selected-test.json");
        System.Text.Json.Nodes.JsonNode document = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        document["resources"] = new System.Text.Json.Nodes.JsonArray((System.Text.Json.Nodes.JsonNode?)null);
        await File.WriteAllTextAsync(path, document.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        down.Remaining.Unverified.ShouldBeTrue();
        Directory.Exists(state.Workspace).ShouldBeTrue();
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeTrue();
    }

    /// <summary>Supported-host discovery can verify empty cleanup, while failed process inspection remains retryable.</summary>
    /// <param name="verified">Whether the native process observation completed.</param>
    /// <returns>The exact scanner uncertainty classification.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SupportedHostProcessObservationControlsCleanupVerificationAsync(bool verified)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionRunResourceScanner scanner = new(CompositionTestFiles.CreateStubDocker(workspace.Root)) { ProcessDiscoveryOverride = (_, _) => ([], verified) };
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (!verified)
        {
            bound.CancelAfter(TimeSpan.FromMilliseconds(200));
        }

        CompositionRunResources remaining = await scanner.RemoveAsync(CompositionTestFiles.RunId, bound.Token).ConfigureAwait(true);
        remaining.IsEmpty.ShouldBe(verified);
        remaining.Unverified.ShouldBe(!verified);
        remaining.DiscoveryFailures.Contains("processes", StringComparer.Ordinal).ShouldBe(!verified);
    }

    /// <summary>Native process environment matching requires the entire owner entry in both OS encodings.</summary>
    /// <param name="unicode">Whether the environment block is Windows UTF-16.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NativeEnvironmentTagMatchingPreservesOwnerBoundary(bool unicode)
    {
        const string entry = CompositionEnvironment.RunId + "=" + CompositionTestFiles.RunId;
        Encoding encoding = unicode ? Encoding.Unicode : Encoding.UTF8;
        CompositionProcessEnvironmentReader.ContainsEntry(encoding.GetBytes("OTHER=value\0" + entry + "\0SECRET=never-retained\0\0"), entry, unicode).ShouldBeTrue();
        CompositionProcessEnvironmentReader.ContainsEntry(encoding.GetBytes("OTHER=" + entry + "\0" + entry + "suffix\0\0"), entry, unicode).ShouldBeFalse();
    }

    /// <summary>Legacy exported evidence preserves the same committed pass as final command output after cleanup errors.</summary>
    /// <returns>The written and rendered terminal outcome assertions.</returns>
    [Fact]
    public async Task LegacyEvidenceUsesCommittedOutcomeBeforeExportAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        byte[] report = Encoding.UTF8.GetBytes("<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>");
        NativeTestExecutionResult native = new(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new Hexalith.Builds.Tooling.TestReports.NativeTestReport("native.trx", Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(report)), 1, 1, 0, 0), report);
        ModuleTestRunRecord terminal = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "remaining", new CompositionRunResources([], [], true)) { ReportHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(report)), TestCounts = new ModuleRunTestCounts(true, 1, 1, 0, 0) };
        const string evidence = "result.json";
        string evidencePath = Path.Combine(workspace.Root, evidence);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        MethodInfo write = typeof(ModuleCommandExecutionService).GetMethod("WriteResultAsync", BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull();
        ToolDiagnostic cleanup = new("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned cleanup incomplete.", "down");
        Task<int> invocation = (Task<int>)write.Invoke(null, ["failed", ToolOutcome.Passed().Fail(cleanup.Phase, cleanup.Category, cleanup.RuleId, ToolExitCode.TopologyOrLifecycle), new[] { cleanup }, ToolOutputFormat.Json, output, ModuleInvocationCommand.Test, workspace.Save(), null, "verify", null, evidence, selected.RequestedAtUtc, TestContext.Current.CancellationToken, state.RunId, null, null, native, selected.Executor, terminal, terminal.RemainingResources, null])!;
        (await invocation.ConfigureAwait(true)).ShouldBe(0, output.ToString());
        using JsonDocument rendered = JsonDocument.Parse(output.ToString());
        using JsonDocument exported = JsonDocument.Parse(await File.ReadAllTextAsync(evidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        rendered.RootElement.GetProperty("status").GetString().ShouldBe("completed");
        exported.RootElement.GetProperty("finalStatus").GetString().ShouldBe("completed");
        exported.RootElement.GetProperty("outcome").GetProperty("exitCode").GetInt32().ShouldBe(0);
        exported.RootElement.GetProperty("selectedTestRun").GetProperty("resourceDisposition").GetString().ShouldBe("remaining");
        ModuleRunEvidenceArtifactValidator.TryValidate(await File.ReadAllBytesAsync(evidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true), out _).ShouldBeTrue(exported.RootElement.GetRawText());
        output.ToString().ShouldContain("HXR023");
    }

    /// <summary>Runner activation is durable and visible to listing before the native child completes.</summary>
    /// <returns>The active independently issued execution deadline and completed child assertions.</returns>
    [Fact]
    public async Task RunningSuitePublishesExecutionActivationForListingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "15");
        string gate = Path.Combine(workspace.Root, "release-child");
        string started = Path.Combine(workspace.Root, "child-started");
        string script = "import os, pathlib, time\n" +
            "pathlib.Path(" + JsonSerializer.Serialize(started) + ").write_text('started')\n" +
            "while not pathlib.Path(" + JsonSerializer.Serialize(gate) + ").exists(): time.sleep(0.01)\n" +
            "pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n";
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), script, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        selected = ModuleTestSelection.Select(Hexalith.Builds.Tooling.Manifest.PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, selected.RequestedAtUtc).ShouldNotBeNull();
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(true);
        CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
        using Process appHost = new();
        CompositionRunSession session = new(plan, ready, "private-key", appHost);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selected.ManifestHash, selected.RequestedAtUtc) with { AttemptId = plan.AttemptId, EnvironmentId = plan.EnvironmentId, SelectedTest = selected };
        await store.WriteAsync(issued, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string nativeDotnet = Path.Combine(workspace.Root, "native-dotnet");
        CompositionTestFiles.WriteScript(nativeDotnet, "exec python3 '" + Path.Combine(workspace.Root, "runner.py") + "'");
        Task<NativeTestExecutionResult> execution = NativeTestExecutor.ExecuteAsync(session, new PersistedProfileNativeTests(selected.Entry, selected.Executor), manifest, TestContext.Current.CancellationToken, nativeDotnet, selected, Path.Combine(store.Directory, "diagnostics", plan.RunId));
        try
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(started) && !execution.IsCompleted)
            {
                await Task.Delay(20, wait.Token).ConfigureAwait(true);
            }

            File.Exists(started).ShouldBeTrue();
            execution.IsCompleted.ShouldBeFalse();
            ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
            listed.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            DateTimeOffset deadline = listed.Environments.ShouldNotBeNull().Single().ExecutionDeadlineUtc.ShouldNotBeNull();
            deadline.ShouldBe(session.Plan.ExecutionTiming.ShouldNotBeNull().DeadlineUtc);
            deadline.ShouldBeGreaterThan(selected.ExecutionDeadlineUtc);
            (await store.TryReadAsync(plan.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull().TestRun.ShouldBeNull();
        }
        finally
        {
            await File.WriteAllTextAsync(gate, "finish", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await execution.ConfigureAwait(true);
        }

        (await execution.ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    private static ToolCommandResult Failed() => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
}
