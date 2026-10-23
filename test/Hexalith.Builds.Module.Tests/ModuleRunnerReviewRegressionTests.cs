// <copyright file="ModuleRunnerReviewRegressionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Offline regressions for the reviewed selected-runner boundaries.</summary>
public sealed class ModuleRunnerReviewRegressionTests
{
    /// <summary>The actual readiness-deadline tail shares its stopping bound through observation failures and inert child cleanup.</summary>
    /// <param name="scenario">The selected observation outcome, or legacy compatibility case.</param>
    /// <returns>The first-failure, shared-clock and actual process-exit assertions.</returns>
    [Theory]
    [InlineData("observed")]
    [InlineData("io")]
    [InlineData("timeout")]
    [InlineData("legacy")]
    public async Task ReadinessDeadlineObservationSharesStoppingBudgetAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, stop: 1);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = scenario == "legacy" ? null : selected, StopTimeout = TimeSpan.FromMilliseconds(100), DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        string runId = Guid.NewGuid().ToString("N");
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = options.SelectedTest, RunId = runId, Workspace = Path.Combine(options.WorkspaceRoot, runId) };
        CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
        await CompositionDocumentStore.WriteAsync(CompositionReadinessProgress.PathFor(plan.Workspace), new CompositionReadinessProgress(CompositionReadinessProgress.SupportedSchema, plan.RunId, [new("inert", "process", "Unhealthy") { Detail = "not-ready" }]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using Process child = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId }) };
        child.Start().ShouldBeTrue();
        using Process observer = Process.GetProcessById(child.Id);
        try
        {
            long observationStarted = 0;
            CancellationToken observedToken = default;
            Func<CancellationToken, Task> observe = async token =>
            {
                observationStarted = Stopwatch.GetTimestamp();
                observedToken = token;
                token.CanBeCanceled.ShouldBeTrue();
                if (scenario == "io")
                {
                    throw new IOException("Offline observation failure.");
                }

                if (scenario == "timeout")
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(true);
                }
            };
            System.Reflection.MethodInfo wait = typeof(CompositionEngine).GetMethod("WaitForReadinessAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).ShouldNotBeNull();
            Stopwatch elapsed = Stopwatch.StartNew();
            Task<CompositionStartResult> task = (Task<CompositionStartResult>)wait.Invoke(engine, [plan, child, "private-signing-key-value", selected.ManifestHash, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null), DateTimeOffset.UtcNow.AddSeconds(-1), observe, TestContext.Current.CancellationToken])!;
            CompositionStartResult result = await task.ConfigureAwait(true);
            result.Result.Status.ShouldBe("failed");
            result.Result.Outcome.RuleId.ShouldBe("HXR021");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            result.Session.ShouldBeNull();
            if (scenario is "observed" or "legacy")
            {
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("not-ready", StringComparison.Ordinal));
            }

            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observer.HasExited.ShouldBeTrue();
            if (scenario == "legacy")
            {
                result.StoppingTimestamp.ShouldBeNull();
                observationStarted.ShouldBe(0);
            }
            else
            {
                long stoppingAt = result.StoppingTimestamp.ShouldNotBeNull();
                stoppingAt.ShouldBeLessThanOrEqualTo(observationStarted);
                if (scenario == "timeout")
                {
                    observedToken.IsCancellationRequested.ShouldBeTrue();
                    Stopwatch.GetElapsedTime(stoppingAt).ShouldBeLessThan(TimeSpan.FromSeconds(1.5));
                }

                if (scenario is "io" or "timeout")
                {
                    result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
                }
            }
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>Selected cancellation and timeout start the shared stopping clock before killing the inert child.</summary>
    /// <param name="ownerCancels">Whether cancellation or the execution timeout stops the process.</param>
    /// <returns>The bounded selected shutdown assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedProcessStoppingUsesInitialSignalAsync(bool ownerCancels)
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (ownerCancels)
        {
            owner.CancelAfter(TimeSpan.FromMilliseconds(100));
        }

        long observed = 0;
        long started = Stopwatch.GetTimestamp();
        Task<CompositionProcessResult> RunAsync() => CompositionProcess.RunSelectedAsync(CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, null), ownerCancels ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100), timestamp => Interlocked.CompareExchange(ref observed, timestamp, 0), owner.Token);
        if (ownerCancels)
        {
            _ = await Should.ThrowAsync<OperationCanceledException>(RunAsync).ConfigureAwait(true);
        }
        else
        {
            (await RunAsync().ConfigureAwait(true)).TimedOut.ShouldBeTrue();
        }

        observed.ShouldBeGreaterThanOrEqualTo(started);
        Stopwatch.GetElapsedTime(observed).ShouldBeLessThan(TimeSpan.FromSeconds(1));
        Stopwatch.GetElapsedTime(started).ShouldBeLessThan(TimeSpan.FromSeconds(2));
    }

    /// <summary>Post-startup bookkeeping consumes the startup's existing stopping allowance.</summary>
    /// <returns>The exhausted-budget refusal assertion.</returns>
    [Fact]
    public async Task FailedStartupDoesNotRenewStoppingBudgetAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, stop: 1);
        CompositionEngineOptions options = Options(workspace.Root);
        options = options with
        {
            SelectedPlatformStartOverride = async (_, selection, token) =>
            {
                ToolOutcome failure = ToolOutcome.Passed().Fail(ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "HXR021", ToolExitCode.TopologyOrLifecycle);
                ModuleTestRunRecord record = new(CompositionTestFiles.RunId, CompositionTestFiles.RunId, selection, null, null, null, null, null, false, [], "failed", failure, DateTimeOffset.UtcNow, "unknown", new CompositionRunResources([], [], Unverified: true));
                await CompositionDocumentStore.WriteAsync(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "selected-test.json"), record, token).ConfigureAwait(true);
                return new CompositionStartResult(new ToolCommandResult("failed", failure, []), CompositionTestFiles.RunId, null) { StoppingTimestamp = Stopwatch.GetTimestamp() - (2 * Stopwatch.Frequency) };
            },
        };
        StringWriter output = new();
        await using ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ExecuteAsync(workspace.Save(), options, output).ConfigureAwait(true);
        exit.ShouldBe(3, output.ToString());
        output.ToString().ShouldContain("HXR021");
        output.ToString().ShouldContain("HXT008");
    }

    /// <summary>Bounds root mapping, regular mapping and startup by the original startup deadline before any native child can run.</summary>
    /// <param name="phase">The deliberately stalled phase.</param>
    /// <returns>The finite refusal assertion.</returns>
    [Theory]
    [InlineData("early-mapping")]
    [InlineData("mapping")]
    [InlineData("startup")]
    public async Task SetupUsesInitialFiniteLifetimeAsync(string phase)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, lifetime: 1);
        workspace.Change("modules.0.lifecycle.startup", "{\"override\":{\"timeoutSeconds\":1,\"justification\":\"Explicit finite fixture budget\"}}");
        int checks = 0;
        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(1),
            SourceMapping = null,
            RootManifestCheckOverride = (_, token) =>
            {
                token.CanBeCanceled.ShouldBeTrue();
                checks++;
                return Task.FromResult(phase != "early-mapping");
            },
            MappingResolverOverride = async (_, _, token) =>
            {
                if (phase != "startup")
                {
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(true);
                }

                return Mapping(workspace.Root);
            },
            SelectedPlatformStartOverride = async (_, _, token) =>
            {
                starts++;
                await Task.Delay(Timeout.Infinite, token).ConfigureAwait(true);
                throw new InvalidOperationException("An expired startup cannot return a native session.");
            },
        };
        StringWriter output = new();
        await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        int exit = await ExecuteAsync(workspace.Save(), options, output).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, output.ToString());
        output.ToString().ShouldContain(phase == "early-mapping" ? "HXR021" : "HXR025");
        checks.ShouldBe(1);
        starts.ShouldBe(phase == "startup" ? 1 : 0);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Inspects cancellation output and actual XML encodings before any redaction or retention.</summary>
    /// <param name="scenario">The child report behavior.</param>
    /// <returns>The report-retention assertion.</returns>
    [Theory]
    [InlineData("token-hang")]
    [InlineData("utf16-token")]
    [InlineData("utf16-malformed")]
    [InlineData("utf32-comment")]
    [InlineData("utf16-pi")]
    [InlineData("utf16-failed")]
    [InlineData("utf16-password")]
    [InlineData("malformed")]
    [InlineData("utf16-safe")]
    public async Task EveryExitInspectsEncodedReportsAsync(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        const string report = "<TestRun><ResultSummary outcome='Completed'><Counters total='1' passed='1' failed='0'/></ResultSummary>";
        string script = "import os, pathlib, time\np=pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH'])\n";
        string suffix = scenario switch
        {
            "utf16-password" => "<Output>password=hunter22</Output></TestRun>",
            "malformed" or "utf16-malformed" => "<broken>",
            _ => "</TestRun>",
        };
        if (scenario is "token-hang" or "utf16-token" or "utf16-malformed")
        {
            script += "payload=" + System.Text.Json.JsonSerializer.Serialize(report) + "+'<Output>'+os.environ['HEXALITH_G4_ACCESS_TOKEN']+'</Output>'\n";
        }
        else if (scenario == "utf16-pi")
        {
            script += "payload=" + System.Text.Json.JsonSerializer.Serialize(report) + "+'<?observation '+os.environ['HEXALITH_G4_ACCESS_TOKEN']+'?>'\n";
        }
        else if (scenario == "utf32-comment")
        {
            script += "payload=" + System.Text.Json.JsonSerializer.Serialize(report) + "+'<!--'+os.environ['HEXALITH_G4_ACCESS_TOKEN']+'-->'\n";
        }
        else
        {
            script += "payload=" + System.Text.Json.JsonSerializer.Serialize(report) + "\n";
        }

        string encoding = scenario switch
        {
            _ when scenario.StartsWith("utf16", StringComparison.Ordinal) => "utf-16",
            _ when scenario.StartsWith("utf32", StringComparison.Ordinal) => "utf-32",
            _ => "utf-8",
        };
        script += "p.write_text(payload+" + System.Text.Json.JsonSerializer.Serialize(suffix) + ", encoding='" + encoding + "')\n";
        script += "pathlib.Path('child-started').write_text('started')\n";
        if (scenario == "token-hang")
        {
            script += "time.sleep(60)\n";
        }
        else if (scenario == "utf16-failed")
        {
            script += "raise SystemExit(7)\n";
        }

        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), script, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        using Process appHost = new();
        CompositionRunSession session = await SessionAsync(workspace.Root, manifest, selected, appHost).ConfigureAwait(true);
        using CancellationTokenSource owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<NativeTestExecutionResult> execution = NativeTestExecutor.ExecuteAsync(session, new PersistedProfileNativeTests("runner.py", "python3"), manifest, owner.Token, selection: selected);
        if (scenario == "token-hang")
        {
            await Task.WhenAll(execution, CancelAfterChildReportAsync(Path.Combine(workspace.Root, "child-started"), owner)).ConfigureAwait(true);
        }

        NativeTestExecutionResult result = await execution.ConfigureAwait(true);
        File.Exists(Path.Combine(workspace.Root, "child-started")).ShouldBeTrue();
        if (scenario is "utf16-safe" or "utf16-failed")
        {
            result.Result.Outcome.ExitCode.ShouldBe(scenario == "utf16-safe" ? ToolExitCode.Success : ToolExitCode.ProductOrTest);
            _ = result.ReportBytes.ShouldNotBeNull();
        }
        else
        {
            result.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == (scenario == "malformed" ? "HXT002" : "HXT008"));
            result.ReportBytes.ShouldBeNull();
        }

        File.Exists(Path.Combine(session.Plan.Workspace, "native-tests/native.trx")).ShouldBeFalse();
    }

    /// <summary>Rejects unsafe, unbounded or special report entries without following or changing foreign targets.</summary>
    /// <param name="scenario">The unsafe report entry.</param>
    /// <returns>The refusal assertion.</returns>
    [Theory]
    [InlineData("link")]
    [InlineData("parent-link")]
    [InlineData("oversize")]
    [InlineData("fifo")]
    public async Task UnsafeReportOutputNeverTouchesForeignTargetsAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string foreign = Path.Combine(workspace.Root, "foreign");
        _ = Directory.CreateDirectory(foreign);
        string sentinel = Path.Combine(foreign, "native.trx");
        await File.WriteAllTextAsync(sentinel, "unchanged foreign sentinel", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string body = scenario switch
        {
            "link" => "p.symlink_to(" + System.Text.Json.JsonSerializer.Serialize(sentinel) + ")",
            "parent-link" => "p.parent.rmdir(); p.parent.symlink_to(" + System.Text.Json.JsonSerializer.Serialize(foreign) + ", target_is_directory=True)",
            "oversize" => "p.write_bytes(b'x' * " + (SelectedNativeTestReport.MaximumBytes + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ")",
            _ => "os.mkfifo(p)",
        };
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "import os, pathlib\np=pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH'])\n" + body + "\npathlib.Path('child-started').write_text('started')\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        using Process appHost = new();
        CompositionRunSession session = await SessionAsync(workspace.Root, manifest, selected, appHost).ConfigureAwait(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selected).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        result.ReportBytes.ShouldBeNull();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
        File.Exists(Path.Combine(workspace.Root, "child-started")).ShouldBeTrue();
        string rawReport = Path.Combine(session.Plan.Workspace, "native-tests", "native.trx");
        if (scenario == "parent-link")
        {
            new DirectoryInfo(Path.GetDirectoryName(rawReport)!).LinkTarget.ShouldBe(foreign);
        }
        else
        {
            Path.Exists(rawReport).ShouldBeFalse();
        }

        (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("unchanged foreign sentinel");
    }

    /// <summary>Exact owned down remains usable after the declaration disappears or becomes corrupt.</summary>
    /// <param name="scenario">The declaration or ownership change.</param>
    /// <returns>The exact cleanup assertion.</returns>
    [Theory]
    [InlineData("deleted")]
    [InlineData("corrupt")]
    [InlineData("foreign")]
    [InlineData("malformed-id")]
    public async Task ExactDownDoesNotRequireManifestRecognitionAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selected, DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        string runId = scenario == "malformed-id" ? "wrong" : CompositionTestFiles.RunId;
        CompositionRunState state = State(options, selected);
        if (scenario == "foreign")
        {
            state = state with { RunId = new string('f', 32) };
            await CompositionDocumentStore.WriteAsync(engine.StateStore.PathFor(CompositionTestFiles.RunId), state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario == "deleted")
        {
            File.Delete(manifest);
        }
        else
        {
            await File.WriteAllTextAsync(manifest, "{invalid JSON", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        StringWriter output = new();
        await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, manifest, null, null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "unused-child", runId, options).ConfigureAwait(true);
        exit.ShouldBe(scenario is "foreign" or "malformed-id" ? 1 : 0, output.ToString());
    }

    /// <summary>Recording failures never prevent cleanup of an actual owned inert process.</summary>
    /// <param name="scenario">The failed bookkeeping operation.</param>
    /// <returns>The process and bounded result assertions.</returns>
    [Theory]
    [InlineData("lock")]
    [InlineData("diagnostics")]
    [InlineData("startup-diagnostics")]
    public async Task FailedBookkeepingStillStopsOwnedProcessAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, stop: 1);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selected, StopTimeout = TimeSpan.FromMilliseconds(100), DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        using Process child = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = CompositionTestFiles.RunId }) };
        child.Start().ShouldBeTrue();
        using Process observer = Process.GetProcessById(child.Id);
        try
        {
            CompositionRunState state = State(options, selected) with { AppHostProcessId = child.Id, AppHostStartedAt = new DateTimeOffset(child.StartTime.ToUniversalTime(), TimeSpan.Zero) };
            CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
            await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string diagnostics = Path.Combine(options.StateDirectory, "diagnostics");
            string directory = Path.Combine(diagnostics, state.RunId);
            if (scenario == "lock")
            {
                CompositionWorkspace.CreatePrivateDirectory(directory);
            }
            else
            {
                await File.WriteAllTextAsync(diagnostics, "diagnostics path is unavailable", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            Stream held = scenario == "lock" ? new FileStream(Path.Combine(directory, "outcome.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None) : Stream.Null;
            await using ConfiguredAsyncDisposable heldLifetime = held.ConfigureAwait(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            if (scenario == "startup-diagnostics")
            {
                System.Reflection.MethodInfo failure = typeof(CompositionEngine).GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single(method => method.Name == "FailAndTearDownAsync" && method.GetParameters().Length == 8);
                ToolDiagnostic cause = new("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "startup failed", "readiness");
                Task<CompositionStartResult> task = (Task<CompositionStartResult>)failure.Invoke(engine, [state.RunId, child, selected.ManifestHash, CompositionRunStatus.Failed, cause, ToolExitCode.TopologyOrLifecycle, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null)])!;
                CompositionStartResult result = await task.ConfigureAwait(true);
                _ = result.StoppingTimestamp.ShouldNotBeNull();
                result.Result.Outcome.RuleId.ShouldBe("HXR021");
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
            }
            else
            {
                CompositionDownResult result = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                result.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
            }

            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observer.HasExited.ShouldBeTrue();
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>A late withdrawal cannot contradict an already committed terminal result.</summary>
    /// <param name="fails">Whether the child failed before cleanup.</param>
    /// <returns>The returned/durable/embedded consistency assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDuringCleanupPreservesCommittedOutcomeAsync(bool fails)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "\npathlib.Path('child-started').write_text('started')\nraise SystemExit(" + (fails ? "7" : "0") + ")\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using Process appHost = new();
        using CancellationTokenSource owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            ContinuousIntegrationOverride = true,
            SelectedPlatformStartOverride = async (_, selected, _) => new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), CompositionTestFiles.RunId, await SessionAsync(workspace.Root, manifest, selected, appHost).ConfigureAwait(true)),
            PlatformDownOverride = async (_, _) =>
            {
                await owner.CancelAsync().ConfigureAwait(true);
                return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], []));
            },
        };
        StringWriter output = new();
        await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ExecuteAsync(manifest, options, output, owner.Token, "evidence/result.json").ConfigureAwait(true);
        File.Exists(Path.Combine(workspace.Root, "child-started")).ShouldBeTrue();
        exit.ShouldBe(fails ? 4 : 0, output.ToString());
        byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(workspace.Root, "evidence/result.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleRunEvidenceArtifactValidator.TryValidate(bytes, out _).ShouldBeTrue(Encoding.UTF8.GetString(bytes));
        ModuleTestRunRecord record = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        record.TerminalStatus.ShouldBe(fails ? "failed" : "completed");
        record.TerminalOutcome.ExitCode.ShouldBe(fails ? ToolExitCode.ProductOrTest : ToolExitCode.Success);
        record.ReportHash.ShouldNotBeNullOrWhiteSpace();
        ModuleRunTestCounts counts = record.TestCounts.ShouldNotBeNull();
        counts.Reported.ShouldBeTrue();
        counts.Total.ShouldBe(1);
        counts.Passed.ShouldBe(1);
        counts.Failed.ShouldBe(0);
    }

    /// <summary>Selected evidence binds inputs, counts, hashes and terminal identities coherently.</summary>
    /// <param name="mutation">The conflicting field.</param>
    [Theory]
    [InlineData("run")]
    [InlineData("owner")]
    [InlineData("withdrawn")]
    [InlineData("profile")]
    [InlineData("manifest")]
    [InlineData("counts")]
    [InlineData("report")]
    [InlineData("artifact")]
    public void ConflictingSelectedEvidenceIsRejected(string mutation)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        ModuleRunTestCounts counts = new(true, 1, 1, 0, 0);
        const string reportPath = "evidence/native.trx";
        string hash = new('A', 64);
        ModuleTestRunRecord record = new(CompositionTestFiles.RunId, CompositionTestFiles.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "removed", new CompositionRunResources([], [])) { ReportHash = hash, TestCounts = counts };
        ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(ModuleInvocationCommand.Test, manifest, null, "verify", null, new ToolCommandResult("completed", ToolOutcome.Passed(), []), selected.RequestedAtUtc, DateTimeOffset.UtcNow, record.RunId, testCounts: counts, artifactHashes: new Dictionary<string, string> { [reportPath] = hash }) with { SelectedTestRun = record };
        evidence = evidence with { Invocation = evidence.Invocation with { Profile = "verify" } };
        byte[] valid = ModuleRunEvidenceWriter.SerializeCanonical(evidence);
        ModuleRunEvidenceArtifactValidator.TryValidate(valid, out _).ShouldBeTrue();
        JsonObject root = JsonNode.Parse(valid).ShouldNotBeNull().AsObject();
        JsonNode run = root["selectedTestRun"].ShouldNotBeNull();
        switch (mutation)
        {
            case "run": run["runId"] = new string('f', 32); break;
            case "owner": run["ownerId"] = new string('f', 32); break;
            case "withdrawn": run["terminalStatus"] = "withdrawn"; break;
            case "profile": root["invocation"].ShouldNotBeNull()["profile"] = "other"; break;
            case "manifest": root["invocation"].ShouldNotBeNull()["manifestHash"] = new string('B', 64); break;
            case "counts": root["testCounts"].ShouldNotBeNull()["total"] = 2; root["testCounts"].ShouldNotBeNull()["passed"] = 2; break;
            case "report": run["reportHash"] = new string('B', 64); break;
            default: root["artifactHashes"].ShouldNotBeNull()[reportPath] = new string('B', 64); break;
        }

        ModuleRunEvidenceArtifactValidator.TryValidate(Encoding.UTF8.GetBytes(root.ToJsonString() + "\n"), out _).ShouldBeFalse();
    }

    /// <summary>Counter component overflow cannot wrap around to a passing TRX report.</summary>
    /// <param name="components">The overflowing counter attributes.</param>
    [Theory]
    [InlineData("failed='2147483647' error='2147483647' timeout='2'")]
    [InlineData("failed='0' notExecuted='2147483647' inconclusive='2147483647'")]
    public void OverflowingCountersAreRejected(string components)
    {
        byte[] bytes = Encoding.UTF8.GetBytes("<TestRun><ResultSummary><Counters total='1' passed='1' " + components + "/></ResultSummary></TestRun>");
        NativeTestReportLoader.LoadBytes("native.trx", bytes).IsValid.ShouldBeFalse();
    }

    /// <summary>Sub-second lifetimes remain precise and mixed-case dependency roots stay excluded.</summary>
    [Fact]
    public void PreciseDeadlineAndCaseInsensitiveOwnershipAreEnforced()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        DateTimeOffset requested = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000).AddTicks(7_500_000);
        ModuleTestSelection selected = Select(manifest, requested);
        (selected.ExecutionDeadlineUtc - requested).ShouldBe(TimeSpan.FromSeconds(30));
        (selected.CredentialExpiryUtc - selected.ExecutionDeadlineUtc).ShouldBe(TimeSpan.FromMilliseconds(250));
        string dependency = Path.Combine(workspace.Root, "ReFeReNcEs");
        _ = Directory.CreateDirectory(dependency);
        File.WriteAllText(Path.Combine(dependency, "runner.py"), "pass\n");
        ModuleTestSelection.IsOwnedEntry(workspace.Root, "ReFeReNcEs/runner.py").ShouldBeFalse();
    }

    private static Task<int> ExecuteAsync(string manifest, CompositionEngineOptions options, TextWriter writer, CancellationToken? owner = null, string? evidence = null) => ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, evidence, ToolOutputFormat.Json, writer, owner ?? TestContext.Current.CancellationToken, "unused-child", compositionOptions: options);

    private static SourceMapping Mapping(string root) => new(WorkspaceMode.Source, root, "sample", [new SourceMappingEntry("sample", "source", root)], false);

    private static CompositionEngineOptions Options(string root) => new("unused-host", "unused-child") { SourceMapping = Mapping(root), WorkspaceRoot = Path.Combine(root, "workspace"), StateDirectory = Path.Combine(root, "state"), ContinuousIntegrationOverride = false };

    private static CompositionRunState State(CompositionEngineOptions options, ModuleTestSelection selected) => new(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Ready, selected.ManifestHash, Path.Combine(options.WorkspaceRoot, CompositionTestFiles.RunId), null, null, null, DateTimeOffset.UtcNow) { SelectedTest = selected };

    private static ModuleTestSelection Select(string manifest, DateTimeOffset? request = null) => ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, request ?? DateTimeOffset.UtcNow).ShouldNotBeNull();

    private static async Task CancelAfterChildReportAsync(string marker, CancellationTokenSource owner)
    {
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (!File.Exists(marker))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), startup.Token).ConfigureAwait(true);
            }
        }
        finally
        {
            await owner.CancelAsync().ConfigureAwait(true);
        }
    }

    private static async Task<CompositionRunSession> SessionAsync(string root, string manifest, ModuleTestSelection selected, Process appHost)
    {
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { SelectedTest = selected, SourceMapping = Mapping(root), Modules = [], UiMarkers = [] };
        plan = await TestEnvironmentProvisioningFixture.PrepareAsync(plan, manifest, root).ConfigureAwait(true);
        CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), selected.ModuleId);
        return new CompositionRunSession(plan, readiness, "private-signing-key-value", appHost);
    }

    private static void Declare(PlatformManifestTestWorkspace workspace, int lifetime = 30, int stop = 2)
    {
        workspace.Document["modules"].ShouldNotBeNull()[0].ShouldNotBeNull()["tests"] = JsonNode.Parse("[{\"name\":\"verify\",\"executor\":\"python3\",\"entry\":\"runner.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":" + lifetime.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"stopTimeoutSeconds\":" + stop.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"mode\":\"source\"}]");
        File.WriteAllText(Path.Combine(workspace.Root, "runner.py"), "import os, pathlib\npathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text(\"<TestRun><ResultSummary outcome='Completed'><Counters total='1' passed='1' failed='0'/></ResultSummary></TestRun>\")\n");
    }
}
