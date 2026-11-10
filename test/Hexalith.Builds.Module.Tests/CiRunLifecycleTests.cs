// <copyright file="CiRunLifecycleTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Exercises durable CI ownership recovery and withdrawal before exact resource cleanup.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CiRunLifecycleTests
{
    /// <summary>CI discovery failure reports failed termination, then exact retry advances observations without changing the first outcome or executor.</summary>
    /// <returns>The failure, retry and immutable-binding assertions.</returns>
    [Fact]
    public async Task CiTerminationDiscoveryFailureAndRetryPreserveFirstIdentityAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState owner) = LocalRunLifecycleFixture.Create(workspace, "package");
        owner = owner with { ExecutionContext = Context(), RetainOnFailure = false, EnvironmentWorkIssued = true, StartupDurationSeconds = 0.5, ExecutingTool = Hexalith.Builds.Tooling.TestEnvironment.ExecutingToolIdentity.Capture() };
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string docker = Path.Combine(workspace.Root, "discovery-docker");
        CompositionTestFiles.WriteScript(docker, "exit 1");
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { DockerCommand = docker, ContinuousIntegrationOverride = true, RetainOnFailure = false };
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult failed = new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
        NativeTestExecutionResult native = new(failed, null, null) { Launched = true, StopObserved = true, CredentialExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
        _ = await engine.RecordTestOutcomeAsync(owner.RunId, [], failed, "unknown", new([], [], true), TestContext.Current.CancellationToken, native).ConfigureAwait(true);
        CompositionDownResult first = await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        first.Remaining.Unverified.ShouldBeTrue();
        string path = Path.Combine(engine.StateStore.Directory, "diagnostics", owner.RunId, "test-result.json");
        Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReadResult failedRead = Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        failedRead.IsValid.ShouldBeTrue();
        JsonElement before = failedRead.Document!.Value;
        before.GetProperty("termination").GetProperty("ownedWork").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "environment:" + owner.RunId).GetProperty("status").GetString().ShouldBe("failed");
        before.GetProperty("termination").GetProperty("environmentAccess")[0].GetProperty("status").GetString().ShouldBe("failed");
        before.GetProperty("termination").GetProperty("failures").GetArrayLength().ShouldBeGreaterThan(0);
        string terminationReference = before.GetProperty("diagnostics").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "termination").GetProperty("reference").GetString().ShouldNotBeNull();
        using (JsonDocument terminationDiagnostic = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(engine.StateStore.Directory, "diagnostics", owner.RunId, terminationReference), TestContext.Current.CancellationToken).ConfigureAwait(true)))
        {
            terminationDiagnostic.RootElement.GetProperty("runId").GetString().ShouldBe(owner.RunId);
        }

        CompositionTestFiles.WriteScript(docker, "exit 0");
        CompositionEngine cleaner = new(options with { ExecutingToolCaptureOverride = () => throw new InvalidOperationException("Cleanup must preserve the original executor.") });
        (await cleaner.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Remaining.IsEmpty.ShouldBeTrue();
        JsonElement after = Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        JsonElement.DeepEquals(before.GetProperty("terminal"), after.GetProperty("terminal")).ShouldBeTrue();
        JsonElement.DeepEquals(before.GetProperty("bindings"), after.GetProperty("bindings")).ShouldBeTrue();
        after.GetProperty("termination").GetProperty("ownedWork").EnumerateArray().ShouldAllBe(item => item.GetProperty("status").GetString() == "terminated");
        after.GetProperty("termination").GetProperty("environmentAccess")[0].GetProperty("status").GetString().ShouldBe("terminated");
    }

    /// <summary>A durable external receipt preserves exact identities before allocation and cannot be rebound.</summary>
    /// <returns>The recovery binding assertions.</returns>
    [Fact]
    public async Task ReceiptIsDurableBeforeWorkspaceAllocationAndCannotBeReboundAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState owner) = LocalRunLifecycleFixture.Create(workspace, "package");
        owner = owner with { ExecutionContext = Context(), RetainOnFailure = false };
        string receipt = Path.Combine(workspace.Root, "receipt.json");
        string state = Path.Combine(workspace.Root, "state");
        await CompositionCiOwnerReceipt.PersistAsync(receipt, owner, state, Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Directory.Exists(owner.Workspace).ShouldBeFalse();
        CompositionCiOwnerReceipt recorded = (await CompositionDocumentStore.TryReadAsync<CompositionCiOwnerReceipt>(receipt, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        recorded.RunId.ShouldBe(owner.RunId);
        recorded.EnvironmentId.ShouldBe(owner.EnvironmentId);
        recorded.AttemptId.ShouldBe(owner.AttemptId);
        recorded.ExecutionContext.GetProperty("jobId").GetString().ShouldBe("ci");
        await CompositionCiOwnerReceipt.PersistAsync(receipt, owner, state, Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await Should.ThrowAsync<IOException>(() => CompositionCiOwnerReceipt.PersistAsync(receipt, owner with { RunId = CompositionRunPlanFactory.NewRunId() }, state, Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    /// <summary>Concurrent allocations cannot replace another owner's preallocation recovery identity.</summary>
    /// <returns>The single-owner durable receipt assertions.</returns>
    [Fact]
    public async Task ConcurrentReceiptPublicationIssuesAtMostOneOwnerAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState initial) = LocalRunLifecycleFixture.Create(workspace, "package");
        CompositionRunState first = initial with { ExecutionContext = Context() };
        CompositionRunState second = first with { RunId = CompositionRunPlanFactory.NewRunId() };
        string receipt = Path.Combine(workspace.Root, "receipt.json");
        async Task<bool> PublishAsync(CompositionRunState owner)
        {
            try
            {
                await CompositionCiOwnerReceipt.PersistAsync(receipt, owner, Path.Combine(workspace.Root, "state"), Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken).ConfigureAwait(true);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }

        bool[] accepted = await Task.WhenAll(PublishAsync(first), PublishAsync(second)).ConfigureAwait(true);
        accepted.Count(value => value).ShouldBe(1);
        CompositionCiOwnerReceipt recorded = (await CompositionDocumentStore.TryReadAsync<CompositionCiOwnerReceipt>(receipt, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        recorded.RunId.ShouldBe(accepted[0] ? first.RunId : second.RunId);
    }

    /// <summary>Unwritable or workspace-contained receipt destinations refuse before allocating resources.</summary>
    /// <param name="location">The refused destination.</param>
    /// <returns>The preallocation assertions.</returns>
    [Theory]
    [InlineData("directory")]
    [InlineData("workspace")]
    public async Task UnrecoverableReceiptRefusesBeforeAllocationAsync(string location)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState owner) = LocalRunLifecycleFixture.Create(workspace, "package");
        owner = owner with { ExecutionContext = Context(), RetainOnFailure = false };
        string receipt = location == "workspace" ? Path.Combine(owner.Workspace, "receipt.json") : workspace.Root;
        Task PublishAsync() => CompositionCiOwnerReceipt.PersistAsync(receipt, owner, Path.Combine(workspace.Root, "state"), Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken);
        if (location == "workspace")
        {
            _ = await Should.ThrowAsync<InvalidDataException>(PublishAsync).ConfigureAwait(true);
        }
        else
        {
            _ = await Should.ThrowAsync<IOException>(PublishAsync).ConfigureAwait(true);
        }

        Directory.Exists(owner.Workspace).ShouldBeFalse();
    }

    /// <summary>All CI terminal paths withdraw active holds before exact cleanup, preserve expiry and leave peer owners untouched.</summary>
    /// <param name="terminal">The first terminal outcome, or partial startup.</param>
    /// <returns>The withdrawal, first outcome and idempotence assertions.</returns>
    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("partial-startup")]
    [InlineData("failed-then-job-cancelled")]
    public async Task CiTerminalCleanupWithdrawsWithoutWaitingAndPreservesPeersAsync(string terminal)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState original) = LocalRunLifecycleFixture.Create(workspace, "package");
        selected = selected with { StopTimeoutSeconds = 30 };
        string runId = CompositionRunPlanFactory.NewRunId();
        CompositionRunState owner = original with { SelectedTest = selected, RunId = runId, Workspace = Path.Combine(workspace.Root, "runs", runId) };
        owner = owner with { Status = CompositionRunStatus.Ready, ExecutionContext = Context(), RetainOnFailure = false, ReadinessResults = [new("owned", "process", "Healthy")] };
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { RetainOnFailure = true, ContinuousIntegrationOverride = true, CiJobCancelled = terminal == "failed-then-job-cancelled", StopTimeout = TimeSpan.FromSeconds(10) };
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string peer = Path.Combine(options.WorkspaceRoot, CompositionRunPlanFactory.NewRunId());
        CompositionWorkspace.CreatePrivateDirectory(peer);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment active = new(CompositionRunPlanFactory.NewRunId(), owner.EnvironmentId!, owner.RunId, CompositionRunPlanFactory.NewRunId(), 14400, now, 14400, now.AddHours(4));
        string diagnostics = Path.Combine(options.StateDirectory, "diagnostics", owner.RunId);
        _ = await CompositionAttachmentStore.UpdateAsync<int>(diagnostics, owner.RunId, _ => (0, [active]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (terminal != "partial-startup")
        {
            ToolOutcome outcome = terminal switch
            {
                "passed" => ToolOutcome.Passed(),
                "failed" or "failed-then-job-cancelled" => ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT001", ToolExitCode.ProductOrTest),
                _ => ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled),
            };
            _ = await engine.RecordTestOutcomeAsync(owner.RunId, owner.ReadinessResults, new(terminal switch { "passed" => "completed", "failed-then-job-cancelled" => "failed", _ => terminal }, outcome, []), "unknown", new([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CompositionDownResult down = await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(down.Result));
        down.Remaining.IsEmpty.ShouldBeTrue();
        CompositionAttachment ended = (await CompositionAttachmentStore.ReadAsync(diagnostics, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        ended.Outcome.ShouldBe("withdrawn");
        ended.ExpiresAtUtc.ShouldBe(active.ExpiresAtUtc);
        ended.EndReason.ShouldBe(terminal is "cancelled" or "partial-startup" or "failed-then-job-cancelled" ? "ci-job-cancelled" : "ci-job-completed");
        File.Exists(Path.Combine(diagnostics, "attachment-withdrawals.json")).ShouldBeTrue();
        Directory.Exists(owner.Workspace).ShouldBeFalse();
        Directory.Exists(peer).ShouldBeTrue();
        ModuleTestRunRecord first = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(diagnostics, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        first.TerminalStatus.ShouldBe(terminal switch { "partial-startup" => "cancelled", "passed" => "completed", "failed-then-job-cancelled" => "failed", _ => terminal });
        first.FailureRetained.ShouldBeFalse();
        StringWriter retryOutput = new();
        await using (retryOutput.ConfigureAwait(true))
        {
            int retryExit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, workspace.Save(), null, null, null, ToolOutputFormat.Json, retryOutput, TestContext.Current.CancellationToken, "child.dll", owner.RunId, options).ConfigureAwait(true);
            retryExit.ShouldBe(0, retryOutput.ToString());
            using JsonDocument retry = JsonDocument.Parse(retryOutput.ToString());

            // The helper verifies exactly these fields of the retried down.
            retry.RootElement.GetProperty("runId").GetString().ShouldBe(owner.RunId);
            retry.RootElement.GetProperty("outcome").GetProperty("exitCode").GetString().ShouldBe("Success");
            retry.RootElement.GetProperty("remainingResources").GetProperty("unverified").GetBoolean().ShouldBeFalse();
        }

        (await CompositionAttachmentStore.ReadAsync(diagnostics, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().ShouldBe(ended);
    }

    /// <summary>The engine persists the CI owner receipt before owner state, and an unwritable receipt allocates nothing.</summary>
    /// <param name="writable">Whether the receipt destination is publishable.</param>
    /// <returns>The receipt-first ordering assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EngineWritesCiOwnerReceiptBeforeStateAndWorkspaceAsync(bool writable)
    {
        Dictionary<string, string?> previous = EnterCiContext();
        try
        {
            using PlatformManifestTestWorkspace workspace = new();
            (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace, "package");
            string receipt = writable ? Path.Combine(workspace.Root, "runner-temp", "hexalith-ci-owner.json") : Path.Combine(workspace.Root, "runner-temp");
            _ = Directory.CreateDirectory(Path.Combine(workspace.Root, "runner-temp"));
            CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { CiOwnerReceiptPath = receipt, ContinuousIntegrationOverride = true };
            CompositionEngine engine = new(options);
            string runId = CompositionRunPlanFactory.NewRunId();
            System.Reflection.MethodInfo initialize = typeof(CompositionEngine).GetMethod("InitializeStateAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).ShouldNotBeNull();
            Task<CompositionRunState> IssueAsync() => (Task<CompositionRunState>)initialize.Invoke(engine, [runId, selected.ManifestHash, DateTimeOffset.UtcNow, new CompositionEffectiveDeadline(600, false, null, null), TestContext.Current.CancellationToken])!;
            if (!writable)
            {
                _ = await Should.ThrowAsync<IOException>(IssueAsync).ConfigureAwait(true);
                File.Exists(engine.StateStore.PathFor(runId)).ShouldBeFalse();
                File.Exists(Path.Combine(options.StateDirectory, "diagnostics", runId, "owner.json")).ShouldBeFalse();
                Directory.Exists(Path.Combine(options.WorkspaceRoot, runId)).ShouldBeFalse();
                return;
            }

            CompositionRunState issued = await IssueAsync().ConfigureAwait(true);
            CompositionCiOwnerReceipt recorded = (await CompositionDocumentStore.TryReadAsync<CompositionCiOwnerReceipt>(receipt, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            (recorded.RunId, recorded.AttemptId, recorded.EnvironmentId).ShouldBe((issued.RunId, issued.AttemptId!, issued.EnvironmentId!));
            recorded.StateDirectory.ShouldBe(Path.GetFullPath(options.StateDirectory));
            File.GetLastWriteTimeUtc(receipt).ShouldBeLessThanOrEqualTo(File.GetLastWriteTimeUtc(engine.StateStore.PathFor(runId)));
            Directory.Exists(Path.Combine(options.WorkspaceRoot, runId)).ShouldBeFalse();
        }
        finally
        {
            ExitCiContext(previous);
        }
    }

    /// <summary>The receipt's raw JSON carries exactly the fields and schema the CI integration helper reads.</summary>
    /// <returns>The cross-language receipt contract assertions.</returns>
    [Fact]
    public async Task ReceiptJsonKeysMatchTheIntegrationHelperContractAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState owner) = LocalRunLifecycleFixture.Create(workspace, "package");
        owner = owner with { ExecutionContext = Context(), RetainOnFailure = false };
        string receipt = Path.Combine(workspace.Root, "receipt.json");
        await CompositionCiOwnerReceipt.PersistAsync(receipt, owner, Path.Combine(workspace.Root, "state"), Path.Combine(workspace.Root, "runs"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(receipt, TestContext.Current.CancellationToken).ConfigureAwait(true));
        string[] keys = [.. document.RootElement.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)];
        keys.ShouldBe(["attemptId", "createdAtUtc", "environmentId", "executionContext", "runId", "schema", "stateDirectory"]);
        string helper = await File.ReadAllTextAsync(Path.Combine(BuildsRoot(), "Github", "run-module-integration", "run_module_integration.py"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        helper.ShouldContain("SCHEMA = \"" + document.RootElement.GetProperty("schema").GetString() + "\"");
        foreach (string key in new[] { "runId", "attemptId", "environmentId", "executionContext", "stateDirectory" })
        {
            helper.ShouldContain("\"" + key + "\"", customMessage: key);
        }

        JsonElement.DeepEquals(document.RootElement.GetProperty("executionContext"), Context()).ShouldBeTrue();
    }

    /// <summary>The finalizer's job status reaches the engine through the production command options.</summary>
    /// <param name="jobStatus">The workflow job status the helper exports, or null when absent.</param>
    /// <param name="cancelled">Whether the engine treats the job as cancelled.</param>
    /// <returns>The status bridge and withdrawal end-reason assertions.</returns>
    [Theory]
    [InlineData("cancelled", true)]
    [InlineData("failure", false)]
    [InlineData("success", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public async Task CiJobStatusBridgesThroughCommandOptionsAsync(string? jobStatus, bool cancelled)
    {
        string? prior = Environment.GetEnvironmentVariable("HEXALITH_CI_JOB_STATUS");
        try
        {
            Environment.SetEnvironmentVariable("HEXALITH_CI_JOB_STATUS", jobStatus);
            CompositionEngineOptions bridged = CompositionCommandOptions.Create(typeof(CiRunLifecycleTests).Assembly.Location);
            bridged.CiJobCancelled.ShouldBe(cancelled);
            using PlatformManifestTestWorkspace workspace = new();
            (ModuleTestSelection selected, CompositionRunState original) = LocalRunLifecycleFixture.Create(workspace, "package");
            CompositionRunState owner = original with { Status = CompositionRunStatus.Ready, ExecutionContext = Context(), RetainOnFailure = false, ReadinessResults = [new("owned", "process", "Healthy")] };
            CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { ContinuousIntegrationOverride = true, CiJobCancelled = bridged.CiJobCancelled, StopTimeout = TimeSpan.FromSeconds(10) };
            CompositionEngine engine = new(options);
            await engine.StateStore.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CompositionAttachment active = new(CompositionRunPlanFactory.NewRunId(), owner.EnvironmentId!, owner.RunId, CompositionRunPlanFactory.NewRunId(), 14400, now, 14400, now.AddHours(4));
            string diagnostics = Path.Combine(options.StateDirectory, "diagnostics", owner.RunId);
            _ = await CompositionAttachmentStore.UpdateAsync<int>(diagnostics, owner.RunId, _ => (0, [active]), TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await engine.RecordTestOutcomeAsync(owner.RunId, owner.ReadinessResults, new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT001", ToolExitCode.ProductOrTest), []), "unknown", new([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
            (await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            CompositionAttachment ended = (await CompositionAttachmentStore.ReadAsync(diagnostics, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
            ended.EndReason.ShouldBe(cancelled ? "ci-job-cancelled" : "ci-job-completed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEXALITH_CI_JOB_STATUS", prior);
        }
    }

    /// <summary>A CI owner is never listed as failure-retained, whatever its terminal or attachments record.</summary>
    /// <returns>The local and CI retention listing assertions.</returns>
    [Fact]
    public async Task CiOwnerListingNeverReportsFailureRetentionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState original) = LocalRunLifecycleFixture.Create(workspace, "package");
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        original = original with { Status = CompositionRunStatus.Ready, ReadinessResults = [new("owned", "process", "Healthy")] };
        await engine.StateStore.WriteAsync(original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord recorded = (await engine.RecordTestOutcomeAsync(original.RunId, original.ReadinessResults, new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT001", ToolExitCode.ProductOrTest), []), "retained", new([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ModuleTestRunRecord retained = recorded with { FailureRetained = true, CleanupDisposition = "not-requested" };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment failed = new CompositionAttachment(CompositionRunPlanFactory.NewRunId(), original.EnvironmentId!, original.RunId, CompositionRunPlanFactory.NewRunId(), 600, now, 600, now.AddMinutes(10)) with { Outcome = "failed", EndedAtUtc = now };
        CompositionRunState local = original with { TestRun = retained, CreatedAt = now };
        CompositionRunListing.Retention(local, [failed]).ShouldBe("failure-retained");
        CompositionRunListing.Retention(local).ShouldBe("failure-retained");
        CompositionRunState ci = local with { ExecutionContext = Context() };
        CompositionRunListing.Retention(ci, [failed]).ShouldBe("not-retained");
        CompositionRunListing.Retention(ci).ShouldBe("not-retained");
    }

    /// <summary>A partial CI startup whose withdrawal cannot be committed stops nothing and reports explicit leftovers.</summary>
    /// <param name="cancelled">Whether startup ended by cancellation rather than failure.</param>
    /// <returns>The refused withdrawal, live resource and later exact cleanup assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialStartupWithFailedCiWithdrawalKeepsResourcesAndReportsLeftoversAsync(bool cancelled)
    {
        Dictionary<string, string?> previous = EnterCiContext();
        try
        {
            using PlatformManifestTestWorkspace workspace = new();
            string manifest = ConfigureExecutingWorkspace(workspace);
            ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
            (CompositionEngineOptions options, CompositionRunPlan plan, string diagnostics) = await PreparePartialStartupAsync(workspace, manifest, selected).ConfigureAwait(true);
            using Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready', flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId }) };
            appHost.Start().ShouldBeTrue();
            (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
            try
            {
                // A directory at the cleanup-request name makes the withdrawal commit fail before anything stops.
                string blocker = Path.Combine(diagnostics, "cleanup-request.json");
                _ = Directory.CreateDirectory(blocker);
                CompositionEngine engine = new(options with { SelectedTest = selected });
                CompositionStartResult refused = await FailPartialStartupAsync(engine, plan.RunId, appHost, selected, cancelled).ConfigureAwait(true);
                await AssertWithdrawalRefusedAsync(refused, cancelled, appHost, diagnostics, plan.RunId).ConfigureAwait(true);
                Directory.Delete(blocker);
                CompositionDownResult down = await engine.DownAsync(plan.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(down.Result));
                down.Remaining.IsEmpty.ShouldBeTrue();
                (await CompositionAttachmentStore.ReadAsync(diagnostics, plan.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().Outcome.ShouldBe("withdrawn");
                await appHost.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            finally
            {
                if (!appHost.HasExited)
                {
                    CompositionProcess.Kill(appHost);
                }
            }
        }
        finally
        {
            ExitCiContext(previous);
        }
    }

    /// <summary>Executes the CI service path and observes withdrawal from the first live composed resource at its stop boundary.</summary>
    /// <param name="scenario">The selected-suite result or actual partial-startup teardown path.</param>
    /// <returns>The live-process ordering, first outcome and no-retention assertions.</returns>
    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("partial-startup-failed")]
    [InlineData("partial-startup-cancelled")]
    public async Task ExecutingCiOwnerReportsWithdrawalBeforeFirstComposedResourceStopsAsync(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        Dictionary<string, string?> previous = EnterCiContext();
        try
        {
            using PlatformManifestTestWorkspace workspace = new();
            string manifest = ConfigureExecutingWorkspace(workspace);
            ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
            using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            using Process appHost = new();
            CiOwnerExecution run = new(workspace, manifest, selected, scenario, cancellation, appHost);
            await WriteExecutingNativeEntryAsync(run).ConfigureAwait(true);
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            try
            {
                await ExecuteCiOwnerAsync(run, output).ConfigureAwait(true);
            }
            finally
            {
                StopCiObservers(run);
            }
        }
        finally
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Failed outcome metadata never renews stopping, and blocked withdrawal prevents every resource stop.</summary>
    /// <param name="blockedWithdrawal">Whether outcome ownership remains unavailable during withdrawal.</param>
    /// <returns>The original stop origin, finite metadata and resource-stop ordering assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutcomeCallbackFailureDoesNotRenewStoppingAllowanceAsync(bool blockedWithdrawal)
    {
        Dictionary<string, string?> previous = EnterCiContext();
        try
        {
            using PlatformManifestTestWorkspace workspace = new();
            _ = LocalRunLifecycleFixture.Create(workspace);
            workspace.Change("modules.0.tests.0.stopTimeoutSeconds", blockedWithdrawal ? "4" : "1");
            string manifest = workspace.Save();
            ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
            using Process appHost = new();
            CompositionRunSession? session = null;
            bool? cancelledCleanup = null;
            CompositionEngineOptions options = BudgetOptions(workspace, selected, manifest, appHost, ready => session = ready, cancelled => cancelledCleanup = cancelled);
            string directory = Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId);
            CompositionWorkspace.CreatePrivateDirectory(directory);
            if (!blockedWithdrawal)
            {
                _ = Directory.CreateDirectory(Path.Combine(directory, "test-result.json"));
            }

            FileStream heldLock = new(Path.Combine(directory, "outcome.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lockLifetime = heldLock.ConfigureAwait(true);
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            await ExecuteBudgetCaseAsync(options, manifest, heldLock, () => session, () => cancelledCleanup, blockedWithdrawal, directory, output).ConfigureAwait(true);
            if (blockedWithdrawal)
            {
                await AssertBlockedWithdrawalAsync(options, session.ShouldNotBeNull(), directory).ConfigureAwait(true);
            }
        }
        finally
        {
            foreach ((string name, string? value) in previous)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>A CI owner without a recorded first outcome closes admission as failed while stop allowance remains.</summary>
    /// <returns>The failed owner status and the owner-failed attachment refusal assertions.</returns>
    [Fact]
    public async Task FailedOwnerStatusWithinStopAllowanceRefusesLaterAttachmentAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState created) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { ContinuousIntegrationOverride = true };
        CompositionRunState issued = created with
        {
            Status = CompositionRunStatus.Ready,
            ReadinessResults = [new CompositionResourceReadiness("owned", "process", "Healthy")],
            RemainingResources = null,
            ExecutionContext = Context(),
        };
        CompositionRunStateStore store = new(options.StateDirectory);
        await store.WriteAsync(issued, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(Path.Combine(options.StateDirectory, "diagnostics", issued.RunId));
        CompositionEngine engine = new(options);

        // The service's no-recorded-terminal branch: the remaining stop allowance is still open.
        using CancellationTokenSource allowance = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        allowance.CancelAfter(TimeSpan.FromSeconds(60));
        await engine.RecordOwnerStatusAsync(issued.RunId, CompositionRunStatus.Failed, allowance.Token).ConfigureAwait(true);
        allowance.IsCancellationRequested.ShouldBeFalse();
        CompositionRunState owner = (await store.TryReadAsync(issued.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        owner.Status.ShouldBe(CompositionRunStatus.Failed);
        owner.TestRun.ShouldBeNull();
        CompositionAttachRequest request = new(issued.RunId, "source", selected.ManifestHash) { AttacherContext = Context() };
        ToolCommandResult admission = await CompositionAttachAdmission.AdmitAsync(store, request, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        admission.Attachment.ShouldBeNull();
        admission.Diagnostics.Single().Field.ShouldBe("owner-failed");
    }

    private static async Task ExecuteCiOwnerAsync(CiOwnerExecution run, StringWriter output)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<int> running = ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, run.Manifest, "verify", null, null, ToolOutputFormat.Json, output, run.Cancellation.Token, "child.dll", compositionOptions: run.Options);
        try
        {
            if (run.Scenario == "cancelled")
            {
                await CancelExecutingNativeAsync(run).ConfigureAwait(true);
            }

            int exit = await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await AssertCiExecutionAsync(run, exit, elapsed, output.ToString()).ConfigureAwait(true);
        }
        finally
        {
            if (!running.IsCompleted)
            {
                await run.Cancellation.CancelAsync().ConfigureAwait(true);
                _ = await running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static string ConfigureExecutingWorkspace(PlatformManifestTestWorkspace workspace)
    {
        _ = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "30");
        workspace.Change("modules.0.tests.0.stopTimeoutSeconds", "30");
        return workspace.Save();
    }

    private static Task WriteExecutingNativeEntryAsync(CiOwnerExecution run)
    {
        string native = """
            import os, pathlib, time
            pathlib.Path(__file__).with_name('native-started').write_text('executed')
            """;
        native += run.Scenario == "cancelled"
            ? "\nwhile True: time.sleep(0.01)\n"
            : "\npathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n";
        if (run.Scenario == "failed")
        {
            native += "raise SystemExit(1)\n";
        }

        return File.WriteAllTextAsync(Path.Combine(run.Workspace.Root, "runner.py"), native, TestContext.Current.CancellationToken);
    }

    private static async Task CancelExecutingNativeAsync(CiOwnerExecution run)
    {
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(run.NativeMarker))
        {
            await Task.Delay(10, startup.Token).ConfigureAwait(true);
        }

        run.Observers.ShouldHaveSingleItem().HasExited.ShouldBeFalse();
        File.Exists(run.Observation).ShouldBeFalse();
        await run.Cancellation.CancelAsync().ConfigureAwait(true);
    }

    private static async Task AssertCiExecutionAsync(CiOwnerExecution run, int exit, Stopwatch elapsed, string output)
    {
        exit.ShouldBe((int)run.FirstExit, output);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(20));
        File.Exists(run.NativeMarker).ShouldBe(!run.Partial);
        Process stopped = run.Observers.ShouldHaveSingleItem();
        await stopped.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(
            await File.ReadAllTextAsync(run.Observation, TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement seen = document.RootElement;
        AssertCiStopSnapshot(run, seen, stopped.Id);
        await AssertRetainedCiResultAsync(run, seen).ConfigureAwait(true);
    }

    private static void AssertCiStopSnapshot(CiOwnerExecution run, JsonElement seen, int processId)
    {
        CompositionRunPlan plan = run.Plan.ShouldNotBeNull();
        CompositionAttachment hold = run.Attachment.ShouldNotBeNull();
        seen.GetProperty("processId").GetInt32().ShouldBe(processId);
        seen.GetProperty("request").GetProperty("runId").GetString().ShouldBe(plan.RunId);
        JsonElement ended = seen.GetProperty("report").GetProperty("attachments").EnumerateArray().ShouldHaveSingleItem();
        ended.GetProperty("attachedRunId").GetString().ShouldBe(hold.AttachedRunId);
        ended.GetProperty("outcome").GetString().ShouldBe("withdrawn");
        ended.GetProperty("endReason").GetString().ShouldBe(run.Cancelled ? "ci-job-cancelled" : "ci-job-completed");
        ended.GetProperty("expiresAtUtc").GetDateTimeOffset().ShouldBe(hold.ExpiresAtUtc);
        seen.GetProperty("terminal").GetProperty("terminalStatus").GetString().ShouldBe(run.FirstStatus);
        seen.GetProperty("terminal").GetProperty("failureRetained").GetBoolean().ShouldBeFalse();

        // Only an actual cancellation records the owner as cancelled before withdrawal and stop.
        string expectedStatus = run.Cancelled ? "Cancelled" : "Ready";
        seen.GetProperty("ownerStatus").GetString().ShouldBe(run.Partial ? "Starting" : expectedStatus);
    }

    private static async Task AssertRetainedCiResultAsync(CiOwnerExecution run, JsonElement seen)
    {
        CompositionRunPlan plan = run.Plan.ShouldNotBeNull();
        string path = Path.Combine(run.BaseOptions.StateDirectory, "diagnostics", plan.RunId, "selected-test.json");
        ModuleTestRunRecord first = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(
            path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        first.TerminalStatus.ShouldBe(run.FirstStatus);
        first.TerminalOutcome.ExitCode.ShouldBe(run.FirstExit);
        first.TerminalAtUtc.ShouldBe(seen.GetProperty("terminal").GetProperty("terminalAtUtc").GetDateTimeOffset());
        first.FailureRetained.ShouldBeFalse();
        first.CleanupDisposition.ShouldBe("complete");
        first.RemainingResources.IsEmpty.ShouldBeTrue();
        Directory.Exists(plan.Workspace).ShouldBeFalse();
    }

    private static void StopCiObservers(CiOwnerExecution run)
    {
        foreach (Process process in run.Observers)
        {
            CompositionProcess.Kill(process);
            process.Dispose();
        }
    }

    private static async Task ExecuteBudgetCaseAsync(CompositionEngineOptions options, string manifest, FileStream heldLock, Func<CompositionRunSession?> session, Func<bool?> cancelledCleanup, bool blockedWithdrawal, string directory, StringWriter output)
    {
        Task released = blockedWithdrawal ? Task.CompletedTask : ReleaseBudgetLockAsync(heldLock, session);
        Stopwatch elapsed = Stopwatch.StartNew();
        try
        {
            int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.ProductOrTest, output.ToString());
            AssertBudgetOutcome(session(), cancelledCleanup(), blockedWithdrawal, directory, elapsed.Elapsed, output.ToString());
        }
        finally
        {
            await released.ConfigureAwait(true);
        }
    }

    private static async Task AssertBlockedWithdrawalAsync(CompositionEngineOptions options, CompositionRunSession session, string directory)
    {
        CompositionEngine engine = new(options);
        CompositionDownResult refused = await engine.DownAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
        refused.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXR037");
        refused.Remaining.Unverified.ShouldBeTrue();
        CompositionRunState state = (await engine.StateStore.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        state.CleanupDisposition.ShouldNotBe("complete");
        CompositionAttachment attachment = (await CompositionAttachmentStore.ReadAsync(directory, session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        attachment.EndedAtUtc.ShouldBeNull();
        File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeFalse();
    }

    private static CompositionEngineOptions BudgetOptions(PlatformManifestTestWorkspace workspace, ModuleTestSelection selected, string manifest, Process appHost, Action<CompositionRunSession> observeSession, Action<bool> observeCleanup)
    {
        string nativeDotnet = Path.Combine(workspace.Root, "native-dotnet");
        CompositionTestFiles.WriteScript(nativeDotnet, "exit 1");
        return LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            NativeDotnetCommand = nativeDotnet,
            ContinuousIntegrationOverride = true,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = async (_, selection, token) =>
            {
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                CompositionRunSession session = new(plan, ready, "private-signing-key", appHost);
                observeSession(session);
                CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selection.ManifestHash, selection.RequestedAtUtc) with
                {
                    SelectedTest = selection,
                    AttemptId = plan.AttemptId,
                    EnvironmentId = plan.EnvironmentId,
                    ExecutionContext = Context(),
                };
                string directory = Path.Combine(workspace.Root, "state", "diagnostics", plan.RunId);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                CompositionAttachment attachment = new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), 14400, now, 14400, now.AddHours(4));
                int admitted = await CompositionAttachmentStore.UpdateAsync<int>(directory, plan.RunId, _ => (0, [attachment]), token).ConfigureAwait(false);
                admitted.ShouldBe(0);
                await new CompositionRunStateStore(Path.Combine(workspace.Root, "state")).WriteAsync(issued, token).ConfigureAwait(false);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = async (session, token) =>
            {
                string report = Path.Combine(workspace.Root, "state", "diagnostics", session.RunId, "attachment-withdrawals.json");
                using JsonDocument withdrawal = JsonDocument.Parse(await File.ReadAllTextAsync(report, TestContext.Current.CancellationToken).ConfigureAwait(false));
                withdrawal.RootElement.GetProperty("ownerRunId").GetString().ShouldBe(session.RunId);
                JsonElement attachment = withdrawal.RootElement.GetProperty("attachments").EnumerateArray().Single();
                attachment.GetProperty("endReason").GetString().ShouldBe("ci-job-completed");
                attachment.GetProperty("outcome").GetString().ShouldBe("withdrawn");
                observeCleanup(token.IsCancellationRequested);
                return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "HXR023", ToolExitCode.TopologyOrLifecycle), []), new CompositionRunResources([], [], true));
            },
        };
    }

    private static async Task ReleaseBudgetLockAsync(FileStream heldLock, Func<CompositionRunSession?> session)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(10));
        while (session()?.FirstObservedOutcome?.MetadataRecordingTimestamp is not long)
        {
            await Task.Delay(10, bound.Token).ConfigureAwait(false);
        }

        long origin = session()!.FirstObservedOutcome!.MetadataRecordingTimestamp!.Value;
        TimeSpan remaining = TimeSpan.FromMilliseconds(1250) - Stopwatch.GetElapsedTime(origin);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, bound.Token).ConfigureAwait(false);
        }

        // The one-second stopping allowance expires before the metadata lock opens; the blocked destination then fails the callback.
        await heldLock.DisposeAsync().ConfigureAwait(false);
    }

    private static void AssertBudgetOutcome(CompositionRunSession? session, bool? cancelledCleanup, bool blockedWithdrawal, string directory, TimeSpan elapsed, string output)
    {
        CompositionRunSession owned = session.ShouldNotBeNull();
        long stopping = owned.StoppingTimestamp.ShouldNotBeNull();
        owned.FirstObservedOutcome.ShouldNotBeNull().MetadataRecordingTimestamp.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(stopping);
        Stopwatch.GetElapsedTime(stopping).ShouldBeGreaterThan(TimeSpan.FromSeconds(blockedWithdrawal ? 4 : 1));

        // Withdrawal never outlasts the remaining stop allowance; once it is spent, the job finalizer owns withdrawal and cleanup.
        cancelledCleanup.ShouldBeNull();
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(blockedWithdrawal ? 11 : 6));
        output.ShouldContain("HXT008");
        File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeFalse();
        Directory.Exists(owned.Plan.Workspace).ShouldBeTrue();
    }

    private static Dictionary<string, string?> EnterCiContext()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["CI"] = "true",
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_REPOSITORY"] = "o/r",
            ["GITHUB_RUN_ID"] = "101",
            ["GITHUB_RUN_ATTEMPT"] = "2",
            ["GITHUB_JOB"] = "ci",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["RUNNER_OS"] = "Linux",
        };
        Dictionary<string, string?> previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach ((string name, string value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return previous;
    }

    private static void ExitCiContext(Dictionary<string, string?> previous)
    {
        foreach ((string name, string? value) in previous)
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string BuildsRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(directory)))
        {
            if (File.Exists(Path.Combine(directory, "Hexalith.Builds.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Could not locate the Builds repository.");
    }

    private static async Task<(CompositionEngineOptions Options, CompositionRunPlan Plan, string Diagnostics)> PreparePartialStartupAsync(PlatformManifestTestWorkspace workspace, string manifest, ModuleTestSelection selected)
    {
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            ContinuousIntegrationOverride = true,
            StopTimeout = TimeSpan.FromSeconds(30),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
        };
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root, CompositionRunPlanFactory.NewRunId()) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(true);
        CompositionRunState state = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selected.ManifestHash, selected.RequestedAtUtc) with
        {
            AttemptId = plan.AttemptId,
            EnvironmentId = plan.EnvironmentId,
            SelectedTest = selected,
            Status = CompositionRunStatus.Starting,
            ExecutionContext = Context(),
            EffectiveDeadlineSeconds = 600,
            StartupDeadlineUtc = selected.RequestedAtUtc.AddSeconds(600),
        };
        await new CompositionRunStateStore(options.StateDirectory).WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string diagnostics = Path.Combine(options.StateDirectory, "diagnostics", plan.RunId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment active = new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), 14400, now, 14400, now.AddHours(4));
        _ = await CompositionAttachmentStore.UpdateAsync<int>(diagnostics, plan.RunId, _ => (0, [active]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        return (options, plan, diagnostics);
    }

    private static Task<CompositionStartResult> FailPartialStartupAsync(CompositionEngine engine, string runId, Process appHost, ModuleTestSelection selected, bool cancelled)
    {
        System.Reflection.MethodInfo fail = typeof(CompositionEngine).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(method => method.Name == "FailAndTearDownAsync" && method.GetParameters().Length == 8);
        ToolDiagnostic cause = new(cancelled ? "HXC130" : "HXR020", ToolPhase.Topology, cancelled ? ToolFailureCategory.Cancelled : ToolFailureCategory.TopologyOrLifecycle, "Controlled partial startup termination.", "apphost");
        object[] arguments = [runId, appHost, selected.ManifestHash, cancelled ? CompositionRunStatus.Cancelled : CompositionRunStatus.Failed, cause, PartialStartupExit(cancelled), selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null)];
        return (Task<CompositionStartResult>)fail.Invoke(engine, arguments)!;
    }

    private static async Task AssertWithdrawalRefusedAsync(CompositionStartResult refused, bool cancelled, Process appHost, string diagnostics, string runId)
    {
        refused.Result.Status.ShouldBe(cancelled ? "cancelled" : "failed");
        refused.Result.Outcome.ExitCode.ShouldBe(PartialStartupExit(cancelled));
        refused.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
        refused.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXR023");
        CompositionRunResources leftovers = refused.Result.RemainingResources.ShouldNotBeNull();
        leftovers.IsEmpty.ShouldBeFalse();
        appHost.HasExited.ShouldBeFalse();
        CompositionAttachment held = (await CompositionAttachmentStore.ReadAsync(diagnostics, runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        held.EndedAtUtc.ShouldBeNull();
        held.Outcome.ShouldBeNull();
        File.Exists(Path.Combine(diagnostics, "attachment-withdrawals.json")).ShouldBeFalse();
        refused.StoppingTimestamp.ShouldNotBeNull().ShouldBeInRange(1, Stopwatch.GetTimestamp());
    }

    private static ToolExitCode PartialStartupExit(bool cancelled) => cancelled ? ToolExitCode.Cancelled : ToolExitCode.TopologyOrLifecycle;

    private static JsonElement Context() => JsonSerializer.SerializeToElement(new { kind = "ci", repository = "o/r", workflowRunId = "101", workflowAttempt = 2, jobId = "ci", runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true });

    /// <summary>One executing control's scenario and live observations; the production fixtures remain shared.</summary>
    /// <param name="Workspace">The existing module fixture.</param>
    /// <param name="Manifest">The declared selected suite.</param>
    /// <param name="Selected">The existing test selection.</param>
    /// <param name="Scenario">The first result or partial-startup path.</param>
    /// <param name="Cancellation">The owner cancellation source.</param>
    /// <param name="AppHost">The actual composed resource process.</param>
    private sealed record CiOwnerExecution(
        PlatformManifestTestWorkspace Workspace,
        string Manifest,
        ModuleTestSelection Selected,
        string Scenario,
        CancellationTokenSource Cancellation,
        Process AppHost)
    {
        /// <summary>Gets a value indicating whether startup stops before returning a session.</summary>
        public bool Partial => Scenario.StartsWith("partial-startup", StringComparison.Ordinal);

        /// <summary>Gets a value indicating whether cancellation is the first outcome.</summary>
        public bool Cancelled => Scenario.EndsWith("cancelled", StringComparison.Ordinal);

        /// <summary>Gets the immutable first terminal status.</summary>
        public string FirstStatus => Scenario switch { "passed" => "completed", "cancelled" or "partial-startup-cancelled" => "cancelled", _ => "failed" };

        /// <summary>Gets the immutable first exit category.</summary>
        public ToolExitCode FirstExit => Scenario switch
        {
            "passed" => ToolExitCode.Success,
            "cancelled" or "partial-startup-cancelled" => ToolExitCode.Cancelled,
            "partial-startup-failed" => ToolExitCode.TopologyOrLifecycle,
            _ => ToolExitCode.ProductOrTest,
        };

        /// <summary>Gets the actual native executor marker.</summary>
        public string NativeMarker => Path.Combine(Workspace.Root, "native-started");

        /// <summary>Gets the first composed-resource stop observation.</summary>
        public string Observation => Path.Combine(Workspace.Root, "first-resource-stop.json");

        /// <summary>Gets the observed real process handles.</summary>
        public List<Process> Observers { get; } = [];

        /// <summary>Gets or sets the exact issued plan.</summary>
        public CompositionRunPlan? Plan { get; set; }

        /// <summary>Gets or sets the accepted long-lived hold.</summary>
        public CompositionAttachment? Attachment { get; set; }

        /// <summary>Gets the existing source-mode fixture options with simulated disposable CI identity.</summary>
        public CompositionEngineOptions BaseOptions { get; } = LocalRunLifecycleFixture.Options(Workspace.Root, Selected) with
        {
            WorkspaceRoot = Path.Combine(Workspace.Root, "workspace"),
            ContinuousIntegrationOverride = true,
            RetainOnFailure = true,
            StopTimeout = TimeSpan.FromSeconds(30),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, Workspace.Root, "sample", [new("sample", "source", Workspace.Root)], false),
        };

        /// <summary>Gets the production command's executing startup callback.</summary>
        public CompositionEngineOptions Options => BaseOptions with
        {
            SelectedPlatformStartOverride = (_, selected, token) => StartAsync(selected, token),
        };

        private async Task<CompositionStartResult> StartAsync(ModuleTestSelection selected, CancellationToken token)
        {
            CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(Workspace.Root, CompositionRunPlanFactory.NewRunId()) with { SelectedTest = selected }, Manifest, Workspace.Root).ConfigureAwait(false);
            Plan = plan;
            CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
            await RecordExecutingOwnerAsync(plan, selected, ready, token).ConfigureAwait(false);
            Attachment = await AdmitExecutingAttachmentAsync(plan, selected, token).ConfigureAwait(false);
            await StartCiStopObserverAsync(plan, token).ConfigureAwait(false);
            if (Partial)
            {
                return await FailCiStartupAsync(plan, selected).ConfigureAwait(false);
            }

            await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
            return new CompositionStartResult(new("ready", ToolOutcome.Passed(), []), plan.RunId, new CompositionRunSession(plan, ready, "private-ci-fixture-key", AppHost));
        }

        private Task RecordExecutingOwnerAsync(CompositionRunPlan plan, ModuleTestSelection selected, CompositionReadiness ready, CancellationToken token)
        {
            CompositionRunState state = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selected.ManifestHash, selected.RequestedAtUtc) with
            {
                AttemptId = plan.AttemptId,
                EnvironmentId = plan.EnvironmentId,
                SelectedTest = selected,
                Status = Partial ? CompositionRunStatus.Starting : CompositionRunStatus.Ready,
                ExecutionContext = Context(),
                ReadinessResults = [.. ready.Resources],
                EffectiveDeadlineSeconds = 600,
                StartupDeadlineUtc = selected.RequestedAtUtc.AddSeconds(600),
            };
            return new CompositionRunStateStore(BaseOptions.StateDirectory).WriteAsync(state, token);
        }

        private async Task<CompositionAttachment> AdmitExecutingAttachmentAsync(CompositionRunPlan plan, ModuleTestSelection selected, CancellationToken token)
        {
            string directory = Path.Combine(BaseOptions.StateDirectory, "diagnostics", plan.RunId);
            if (Partial)
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                CompositionAttachment accepted = new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), 14400, now, 14400, now.AddHours(4));
                int recorded = await CompositionAttachmentStore.UpdateAsync<int>(directory, plan.RunId, _ => (0, [accepted]), token).ConfigureAwait(false);
                recorded.ShouldBe(0);
                return accepted;
            }

            CompositionAttachRequest request = new(plan.RunId, "source", selected.ManifestHash) { AttacherContext = Context(), HoldSeconds = "14400" };
            ToolCommandResult admission = await CompositionAttachAdmission.AdmitAsync(
                new CompositionRunStateStore(BaseOptions.StateDirectory), request, DateTimeOffset.UtcNow, token).ConfigureAwait(false);
            return admission.Attachment.ShouldNotBeNull(JsonSerializer.Serialize(admission));
        }

        private async Task StartCiStopObserverAsync(CompositionRunPlan plan, CancellationToken token)
        {
            const string observer = """
                import json, os, pathlib, sys
                print('ready', flush=True)
                sys.stdin.readline()
                directory = pathlib.Path(sys.argv[1])
                report = json.loads((directory / 'attachment-withdrawals.json').read_text())
                terminal = json.loads((directory / 'selected-test.json').read_text())
                request = json.loads((directory / 'cleanup-request.json').read_text())
                owner = json.loads((directory.parent.parent / (directory.name + '.json')).read_text())
                pathlib.Path(sys.argv[2]).write_text(json.dumps({'report': report, 'terminal': terminal, 'request': request, 'ownerStatus': owner['status'], 'processId': os.getpid()}))
                """;
            string directory = Path.Combine(BaseOptions.StateDirectory, "diagnostics", plan.RunId);
            AppHost.StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", observer, directory, Observation], Workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId });
            AppHost.Start().ShouldBeTrue();
            Observers.Add(Process.GetProcessById(AppHost.Id));
            (await AppHost.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)).ShouldBe("ready");
        }

        private async Task<CompositionStartResult> FailCiStartupAsync(CompositionRunPlan plan, ModuleTestSelection selected)
        {
            if (Cancelled)
            {
                await Cancellation.CancelAsync().ConfigureAwait(false);
            }

            CompositionEngine engine = new(BaseOptions with { SelectedTest = selected });
            System.Reflection.MethodInfo fail = typeof(CompositionEngine).GetMethods(
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Single(method => method.Name == "FailAndTearDownAsync" && method.GetParameters().Length == 8);
            ToolDiagnostic cause = new(Cancelled ? "HXC130" : "HXR020", ToolPhase.Topology, Cancelled ? ToolFailureCategory.Cancelled : ToolFailureCategory.TopologyOrLifecycle, "Controlled partial startup termination.", "apphost");
            object[] arguments = [plan.RunId, AppHost, selected.ManifestHash, Cancelled ? CompositionRunStatus.Cancelled : CompositionRunStatus.Failed, cause, FirstExit, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null)];
            return await ((Task<CompositionStartResult>)fail.Invoke(engine, arguments)!).ConfigureAwait(false);
        }
    }
}
