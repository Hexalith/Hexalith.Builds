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
        (await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Remaining.IsEmpty.ShouldBeTrue();
        (await CompositionAttachmentStore.ReadAsync(diagnostics, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().ShouldBe(ended);
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
            return new CompositionStartResult(new("ready", ToolOutcome.Passed(), []), plan.RunId, new CompositionRunSession(plan, ready, "private-ci-fixture-key", AppHost) { LocalExecution = false });
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
                pathlib.Path(sys.argv[2]).write_text(json.dumps({'report': report, 'terminal': terminal, 'request': request, 'processId': os.getpid()}))
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
