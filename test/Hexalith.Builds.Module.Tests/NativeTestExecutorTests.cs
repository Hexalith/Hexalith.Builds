// <copyright file="NativeTestExecutorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the runner-owned native test step never hides the native exit status or report semantics.
/// </summary>
public sealed class NativeTestExecutorTests
{
    private const string _markerRunner = "import pathlib\npathlib.Path(__file__).with_name('child-executed').touch()\n";

    private static readonly NativeTestReportLoadResult _passingReport = new(new NativeTestReport("native.trx", new string('A', 64), 2, 2, 0, 0), null);

    private static readonly byte[] _reportBytes = Encoding.UTF8.GetBytes("<TestRun><ResultSummary outcome=\"Completed\" /></TestRun>");

    /// <summary>Native descendant stopping verifies detached work without stopping owner services or peer executions.</summary>
    /// <param name="discoveryVerified">Whether process ownership can be completely observed.</param>
    /// <returns>The real detached-child stopping, uncertainty and peer-isolation assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewDetachedNativeDescendantsAreVerifiedAsync(bool discoveryVerified)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The detached-child regression observes Linux process state.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        const string runner = "import os, pathlib, subprocess, sys\n"
            + "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)\n"
            + "pathlib.Path(__file__).with_name('detached.pid').write_text(str(child.pid))\n"
            + "pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n";
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, runner).ConfigureAwait(true);
        using Process owner = Process.Start(CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [NativeTestHandoff.RunId] = plan.RunId }))!;
        using Process peer = Process.Start(CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [NativeTestHandoff.RunId] = CompositionRunPlanFactory.NewRunId(), [NativeTestHandoff._nativeExecutionId] = CompositionRunPlanFactory.NewRunId() }))!;
        using Process appHost = new();
        int? detachedPid = null;
        try
        {
            CompositionRunResourceScanner? scanner = discoveryVerified ? null : new CompositionRunResourceScanner(string.Empty) { ProcessDiscoveryOverride = (_, _) => ([], false) };
            NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, HealthyReadiness(plan), "private-detached-key", appHost), new("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection, nativeProcessScanner: scanner).ConfigureAwait(true);
            detachedPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "detached.pid"), TestContext.Current.CancellationToken).ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            result.StopObserved.ShouldBe(discoveryVerified);
            CompositionOwnerTermination termination = result.OwnerTermination.ShouldNotBeNull();
            termination.OwnedWork.Single().Status.ShouldBe(discoveryVerified ? "terminated" : "failed");
            termination.EnvironmentAccess.Single().Status.ShouldBe("pending");
            bool detachedAlive;
            try
            {
                string stat = await File.ReadAllTextAsync("/proc/" + detachedPid + "/stat", TestContext.Current.CancellationToken).ConfigureAwait(true);
                detachedAlive = !stat.Contains(") Z ", StringComparison.Ordinal);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                detachedAlive = false;
            }

            detachedAlive.ShouldBe(!discoveryVerified);
            if (!discoveryVerified)
            {
                termination.Failures.ShouldContain("native:" + plan.RunId);
                termination.LeftoverWorkIds.ShouldContain("process:" + result.ProcessIds.Single().ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            owner.HasExited.ShouldBeFalse("Retained owner work has no native execution tag.");
            peer.HasExited.ShouldBeFalse("A different native execution tag cannot be stopped.");
            result.StoppingTimestamp.ShouldNotBeNull().ShouldBeLessThanOrEqualTo(System.Diagnostics.Stopwatch.GetTimestamp());
        }
        finally
        {
            CompositionProcess.Kill(owner);
            CompositionProcess.Kill(peer);
            string pidPath = Path.Combine(workspace.Root, "detached.pid");
            if (detachedPid is null && File.Exists(pidPath))
            {
                detachedPid = int.Parse(await File.ReadAllTextAsync(pidPath, TestContext.Current.CancellationToken).ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
            }

            if (detachedPid is { } processId)
            {
                try
                {
                    using Process child = Process.GetProcessById(processId);
                    CompositionProcess.Kill(child);
                }
                catch (ArgumentException)
                {
                    // Verified stopping already removed the detached child.
                }
            }
        }
    }

    /// <summary>Pre-existing protected processes are excluded, but new unreadable native work remains unverified.</summary>
    /// <param name="preexisting">Whether the protected process existed before the private execution snapshot.</param>
    /// <returns>The actual Linux ownership uncertainty assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewProtectedProcessesPreserveNativeDiscoveryHonestyAsync(bool preexisting)
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The protected-process regression requires Linux process environment permissions.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string nativeId = CompositionRunPlanFactory.NewRunId();
        CompositionRunResourceScanner? scanner = preexisting ? null : CompositionRunResourceScanner.CreateNativeScanner();
        string ready = Path.Combine(workspace.Root, "protected-ready");
        string script = "import ctypes, pathlib, time\n"
            + "result = ctypes.CDLL(None).prctl(4, 0, 0, 0, 0)\n"
            + "ready = pathlib.Path(" + JsonSerializer.Serialize(ready) + ")\n"
            + "temporary = ready.with_suffix('.tmp')\n"
            + "temporary.write_text(str(result))\n"
            + "temporary.replace(ready)\n"
            + "time.sleep(60)\n";
        Dictionary<string, string> environment = preexisting
            ? new() { [NativeTestHandoff.RunId] = CompositionRunPlanFactory.NewRunId() }
            : new() { [NativeTestHandoff._nativeExecutionId] = nativeId };
        using Process process = Process.Start(CompositionProcess.CreateStartInfo("python3", ["-c", script], workspace.Root, environment))!;
        try
        {
            using CancellationTokenSource wait = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            wait.CancelAfter(TimeSpan.FromSeconds(30));
            while (!File.Exists(ready) && !process.HasExited)
            {
                await Task.Delay(20, wait.Token).ConfigureAwait(true);
            }

            File.Exists(ready).ShouldBeTrue();
            (await File.ReadAllTextAsync(ready, wait.Token).ConfigureAwait(true)).ShouldBe("0");
            scanner ??= CompositionRunResourceScanner.CreateNativeScanner();
            (IReadOnlyList<int> processes, bool verified) = scanner.FindTaggedProcesses(nativeId, wait.Token);
            verified.ShouldBe(preexisting);
            processes.ShouldBeEmpty("The protected environment is unreadable; new native work cannot be declared absent.");
            process.HasExited.ShouldBeFalse();
        }
        finally
        {
            CompositionProcess.Kill(process);
        }
    }

    /// <summary>A cancelled scan preserves already discovered native identities and stops no work beyond its allowance.</summary>
    /// <returns>The actual partial-discovery and unchanged child assertions.</returns>
    [Fact]
    public async Task ReviewCancelledNativeScanPreservesKnownDescendantsAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The partial native scan regression observes Linux process environments.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string identity = CompositionRunPlanFactory.NewRunId();
        using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using Process child = Process.Start(CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [NativeTestHandoff._nativeExecutionId] = identity }))!;
        try
        {
            CompositionRunResourceScanner scanner = new(string.Empty)
            {
                ProcessEnvironmentVariable = NativeTestHandoff._nativeExecutionId,
                ProcessMatchedOverride = _ => stop.Cancel(),
            };
            (IReadOnlyList<int> remaining, bool verified) = await scanner.RemoveTaggedProcessesAsync(identity, stop.Token).ConfigureAwait(true);
            verified.ShouldBeFalse();
            remaining.ShouldContain(child.Id);
            child.HasExited.ShouldBeFalse();
        }
        finally
        {
            CompositionProcess.Kill(child);
        }
    }

    /// <summary>First-outcome recording cannot renew the original descendant-stopping allowance.</summary>
    /// <returns>The surviving detached child and failed native termination after stopping has expired.</returns>
    [Fact]
    public async Task NativeStoppingDoesNotRenewAfterOutcomeRecordingAsync()
    {
        if (!OperatingSystem.IsLinux())
        {
            Assert.Skip("The detached-child allowance regression observes Linux process state.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        const string runner = "import pathlib, subprocess, sys\n"
            + "child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(60)'], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True)\n"
            + "pathlib.Path(__file__).with_name('detached.pid').write_text(str(child.pid))\n"
            + "sys.exit(7)\n";
        _ = await PrepareSelectedRunAsync(workspace, runner).ConfigureAwait(true);
        workspace.Change("modules.0.tests.0.stopTimeoutSeconds", "1");
        string manifest = workspace.Save();
        ModuleTestSelection selection = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        SourceMapping mapping = new(WorkspaceMode.Source, workspace.Root, "sample", [new SourceMappingEntry("sample", "source", workspace.Root)], false);
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root, CompositionRunPlanFactory.NewRunId()) with { SelectedTest = selection, SourceMapping = mapping, Modules = [], UiMarkers = [] }, manifest, workspace.Root).ConfigureAwait(true);
        using Process appHost = new();
        try
        {
            bool delayed = false;
            async Task RecordOutcomeAsync(NativeTestExecutionResult first, CancellationToken token)
            {
                if (!delayed)
                {
                    first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.ProductOrTest);
                    delayed = true;
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), token).ConfigureAwait(false);
                }
            }

            NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, HealthyReadiness(plan), "private-deadline-key", appHost), new("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection, onOutcome: RecordOutcomeAsync).ConfigureAwait(true);
            int detachedPid = int.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "detached.pid"), TestContext.Current.CancellationToken).ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
            using Process child = Process.GetProcessById(detachedPid);
            child.HasExited.ShouldBeFalse("Expired stopping cannot be renewed by first-outcome recording.");
            result.StopObserved.ShouldBeFalse();
            result.OwnerTermination.ShouldNotBeNull().OwnedWork.Single().Status.ShouldBe("failed");
        }
        finally
        {
            string pidPath = Path.Combine(workspace.Root, "detached.pid");
            if (File.Exists(pidPath))
            {
                int processId = int.Parse(await File.ReadAllTextAsync(pidPath, CancellationToken.None).ConfigureAwait(true), System.Globalization.CultureInfo.InvariantCulture);
                try
                {
                    using Process child = Process.GetProcessById(processId);
                    CompositionProcess.Kill(child);
                }
                catch (ArgumentException)
                {
                    // The test teardown never renews the runner's stopping allowance.
                }
            }
        }
    }

    /// <summary>A causal native failure is published before detached-work stopping or discovery can delay it.</summary>
    /// <param name="recordingFails">Whether first-outcome publication throws before descendant verification.</param>
    /// <returns>The outcome-before-stopping and subsequently verified termination assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewCausalOutcomePrecedesDescendantStoppingAsync(bool recordingFails)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, "import sys\nsys.exit(7)\n").ConfigureAwait(true);
        bool observed = false;
        bool observedBeforeDiscovery = false;
        CompositionRunResourceScanner scanner = new(string.Empty)
        {
            ProcessDiscoveryOverride = (_, _) =>
            {
                observedBeforeDiscovery = observed;
                return ([], true);
            },
        };
        using Process appHost = new();
        CompositionRunSession session = new(plan, HealthyReadiness(plan), "private-causal-key", appHost);
        IOException recordingFailure = new("First outcome recording failed.");
        Task RecordOutcomeAsync(NativeTestExecutionResult first, CancellationToken metadataToken)
        {
            metadataToken.ThrowIfCancellationRequested();
            if (!observed)
            {
                first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.ProductOrTest);
                first.StopObserved.ShouldBeFalse();
                first.OwnerTermination.ShouldNotBeNull().OwnedWork.Single().Status.ShouldBe("pending");
                observed = true;
                if (recordingFails)
                {
                    return Task.FromException(recordingFailure);
                }
            }

            return Task.CompletedTask;
        }

        string archive = Path.Combine(workspace.Root, "retained");
        Task<NativeTestExecutionResult> ExecuteAsync() => NativeTestExecutor.ExecuteAsync(session, new("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection, archiveDirectory: archive, onOutcome: RecordOutcomeAsync, nativeProcessScanner: scanner);
        if (recordingFails)
        {
            IOException failure = await Should.ThrowAsync<IOException>(ExecuteAsync).ConfigureAwait(true);
            failure.ShouldBeSameAs(recordingFailure);
        }
        else
        {
            (await ExecuteAsync().ConfigureAwait(true)).StopObserved.ShouldBeTrue();
        }

        observed.ShouldBeTrue();
        observedBeforeDiscovery.ShouldBeTrue();
        session.NativeTermination.ShouldNotBeNull().OwnedWork.Single().Status.ShouldBe("terminated");
        session.FirstObservedOutcome.ShouldNotBeNull().OwnerTermination.ShouldNotBeNull().OwnedWork.Single().Status.ShouldBe("terminated");
        CompositionOwnerTermination durable = (await CompositionDocumentStore.ReadIfExistsAsync<CompositionOwnerTermination>(Path.Combine(archive, "native-termination.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionDocumentStore.Serialize(durable).ShouldBe(CompositionDocumentStore.Serialize(session.NativeTermination));
    }

    /// <summary>Launch and completion keep the first pending-access observation while later expiry facts advance.</summary>
    [Fact]
    public void ReviewNativeAccessKeepsFirstPendingObservation()
    {
        DateTimeOffset launchedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        NativeTestExecutionResult launch = new(new("running", ToolOutcome.Passed(), []), null, null)
        {
            Launched = true,
            ProcessIds = [12345],
            ObservedAtUtc = launchedAt,
            CredentialExpiresAtUtc = launchedAt.AddSeconds(60),
        };
        CompositionOwnerTermination running = CompositionOwnerTermination.ObserveNative(CompositionTestFiles.RunId, null, launch, running: true);
        NativeTestExecutionResult completion = launch with { StopObserved = true, ObservedAtUtc = launchedAt.AddSeconds(1), CredentialExpiresAtUtc = launchedAt.AddSeconds(90) };
        CompositionOwnerTermination finished = CompositionOwnerTermination.ObserveNative(CompositionTestFiles.RunId, running, completion);
        finished.EnvironmentAccess.Single().Status.ShouldBe("pending");
        finished.EnvironmentAccess.Single().ObservedAtUtc.ShouldBe(launchedAt);
        finished.CredentialExpiresAtUtc.ShouldBe(completion.CredentialExpiresAtUtc);
        finished.NativeProcessIds.ShouldBe([12345]);
        finished.OwnedWork.Single().Status.ShouldBe("terminated");
        finished.OwnedWork.Single().ObservedAtUtc.ShouldBe(completion.ObservedAtUtc.ShouldNotBeNull());
        CompositionOwnerTermination repeated = CompositionOwnerTermination.ObserveNative(CompositionTestFiles.RunId, finished, completion with { ObservedAtUtc = launchedAt.AddSeconds(2) });
        CompositionDocumentStore.Serialize(repeated).ShouldBe(CompositionDocumentStore.Serialize(finished));
    }

    /// <summary>A native ledger that cannot be durably written is reported instead of being silently dropped.</summary>
    /// <returns>The launch-metadata retention failure assertions.</returns>
    [Fact]
    public async Task ReviewUnwritableNativeLedgerReportsLaunchMetadataFailureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The native process double uses POSIX shell scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        _ = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executor", "\"vstest\"");
        workspace.Change("modules.0.tests.0.entry", "\"runner.csproj\"");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(true);
        using Process appHost = new();
        CompositionRunSession session = new(plan, HealthyReadiness(plan), "private-ledger-key", appHost);
        string archive = Path.Combine(workspace.Root, "retained");
        CompositionWorkspace.CreatePrivateDirectory(archive);
        string ledger = Path.Combine(archive, "native-termination.json");
        string child = Path.Combine(workspace.Root, "native-dotnet");
        string script = "while [ ! -d '" + ledger + "' ]; do rm -f '" + ledger + "'; mkdir '" + ledger + "' 2>/dev/null; done\n"
            + "printf '<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>' > \"$HEXALITH_G4_TEST_REPORT_PATH\"\n";
        CompositionTestFiles.WriteScript(child, script);
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new("runner.csproj", "vstest"), manifest, TestContext.Current.CancellationToken, child, selected, archive).ConfigureAwait(true);
        result.Launched.ShouldBeTrue();
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008" && diagnostic.Message == "Native launch metadata could not be durably retained.");
    }

    /// <summary>Prepared-only access stays unissued; a later failed launch preserves actual earlier execution.</summary>
    /// <param name="earlierExecution">Whether a successful nonfinal entry already launched.</param>
    /// <returns>The persisted cumulative work/access assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReviewFailedLaunchPreservesActualBatchHistoryAsync(bool earlierExecution)
    {
        using PlatformManifestTestWorkspace workspace = new();
        _ = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executor", "\"vstest\"");
        workspace.Change("modules.0.tests.0.entry", "\"runner.csproj\"");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(true);
        using Process appHost = new();
        CompositionRunSession session = new(plan, HealthyReadiness(plan), "private-batch-key", appHost);
        string archive = Path.Combine(workspace.Root, "retained");
        CompositionWorkspace.CreatePrivateDirectory(archive);
        IReadOnlyList<int> originalProcesses = [];
        DateTimeOffset? originalExpiry = null;
        DateTimeOffset? originalObservation = null;
        if (earlierExecution)
        {
            string child = Path.Combine(workspace.Root, "native-dotnet");
            CompositionTestFiles.WriteScript(child, "printf '<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>' > \"$HEXALITH_G4_TEST_REPORT_PATH\"\n");
            NativeTestExecutionResult first = await NativeTestExecutor.ExecuteAsync(session, new("runner.csproj", "vstest"), manifest, TestContext.Current.CancellationToken, child, selected, archive).ConfigureAwait(true);
            first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            first.Launched.ShouldBeTrue();
            originalProcesses = first.ProcessIds;
            originalExpiry = first.CredentialExpiresAtUtc;
            originalObservation = first.OwnerTermination.ShouldNotBeNull().OwnedWork.Single().ObservedAtUtc;
            session.Plan = session.Plan with { ExecutionTiming = null };
        }

        NativeTestExecutionResult failed = await NativeTestExecutor.ExecuteAsync(session, new("runner.csproj", "vstest"), manifest, TestContext.Current.CancellationToken, Path.Combine(workspace.Root, "missing-dotnet"), selected, archive).ConfigureAwait(true);
        failed.Launched.ShouldBeFalse();
        failed.CredentialExpiresAtUtc.ShouldBeNull();
        CompositionOwnerTermination ledger = (await CompositionDocumentStore.ReadIfExistsAsync<CompositionOwnerTermination>(Path.Combine(archive, "native-termination.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ledger.OwnedWork.Single().Status.ShouldBe(earlierExecution ? "terminated" : "not-applicable");
        ledger.EnvironmentAccess.Single().Status.ShouldBe(earlierExecution ? "pending" : "not-applicable");
        ledger.CredentialExpiresAtUtc.ShouldBe(originalExpiry);
        ledger.NativeProcessIds.ShouldBe(originalProcesses);
        if (earlierExecution)
        {
            ledger.OwnedWork.Single().ObservedAtUtc.ShouldBe(originalObservation.ShouldNotBeNull());
        }
    }

    /// <summary>Checks each platform uses its own native report switch and no shared option.</summary>
    [Fact]
    public void ArgumentsSelectTheNativeReportOfEachPlatform()
    {
        NativeTestExecutor.CreateArguments("vstest", "/repo/Tests.csproj", "/run/native-tests")
            .ShouldBe(["test", "/repo/Tests.csproj", "--logger", "trx;LogFileName=native.trx", "--results-directory", "/run/native-tests"]);
        NativeTestExecutor.CreateArguments("mtp", "/repo/Tests.csproj", "/run/native-tests")
            .ShouldBe(["test", "--project", "/repo/Tests.csproj", "--results-directory", "/run/native-tests", "--report-xunit-trx", "--report-xunit-trx-filename", "native.trx"]);
        _ = Should.Throw<ArgumentOutOfRangeException>(() => NativeTestExecutor.CreateArguments("nunit", "/repo/Tests.csproj", "/run"));
    }

    /// <summary>Checks a passing native report and zero exit bind counts and bytes.</summary>
    [Fact]
    public void PassingReportAndExitBindCounts()
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), _passingReport, _reportBytes, ["run-token"]);

        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        result.Report.ShouldNotBeNull().Passed.ShouldBe(2);
        result.ReportBytes.ShouldBe(_reportBytes);
    }

    /// <summary>Checks a clean report cannot override a nonzero native exit status.</summary>
    [Fact]
    public void NonzeroExitFailsEvenWithPassingReport()
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 2, string.Empty, false), _passingReport, _reportBytes, ["run-token"]);

        AssertNotPassing(result, "HXT007", ToolExitCode.ProductOrTest);
    }

    /// <summary>Checks native report failures keep their stable loader rule.</summary>
    /// <param name="ruleId">The loader rule.</param>
    [Theory]
    [InlineData("HXT001")]
    [InlineData("HXT002")]
    [InlineData("HXT003")]
    [InlineData("HXT004")]
    [InlineData("HXT005")]
    [InlineData("HXT006")]
    public void InvalidReportKeepsLoaderRule(string ruleId)
    {
        NativeTestReportLoadResult invalid = new(null, new ToolDiagnostic(ruleId, ToolPhase.Test, ToolFailureCategory.ProductOrTest, "invalid", "report"));

        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), invalid, _reportBytes, ["run-token"]);

        AssertNotPassing(result, ruleId, ToolExitCode.ProductOrTest);
    }

    /// <summary>Checks a timed-out or unstarted native process never passes.</summary>
    [Fact]
    public void TimeoutAndUnavailableProcessNeverPass()
    {
        AssertNotPassing(
            NativeTestExecutor.Evaluate(new CompositionProcessResult(true, -1, string.Empty, true), _passingReport, _reportBytes, ["run-token"]),
            "HXT007",
            ToolExitCode.ProductOrTest);
        AssertNotPassing(
            NativeTestExecutor.Evaluate(new CompositionProcessResult(false, -1, string.Empty, false), _passingReport, _reportBytes, ["run-token"]),
            "HXR030",
            ToolExitCode.PrerequisiteUnavailable);
    }

    /// <summary>Checks a report carrying handoff or credential material is rejected and not retained.</summary>
    /// <param name="content">The seeded report content.</param>
    [Theory]
    [InlineData("<TestRun>run-token-value</TestRun>")]
    [InlineData("<TestRun><StdOut>Authorization: Bearer abcdefghijklmnop</StdOut></TestRun>")]
    [InlineData("<TestRun><StdOut>password=hunter22</StdOut></TestRun>")]
    public void SecretBearingReportIsRejectedAndDropped(string content)
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(
            new CompositionProcessResult(true, 0, string.Empty, false),
            _passingReport,
            Encoding.UTF8.GetBytes(content),
            ["run-token-value"]);

        AssertNotPassing(result, "HXT008", ToolExitCode.EvidenceSchemaOrPolicy);
    }

    /// <summary>Checks ordinary native report metadata is not mistaken for credential material.</summary>
    [Fact]
    public void ReportMetadataWithoutCredentialsIsRetained()
    {
        byte[] report = Encoding.UTF8.GetBytes(
            "<TestRun id=\"1\" runUser=\"builder\" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
            + "<TestDefinitions><UnitTest name=\"AuthenticatedCommandCompletesAsync\" storage=\"/repo/artifacts/Native.dll\">"
            + "<TestMethod codeBase=\"/repo/artifacts/Native.dll\" className=\"Native.RunHandoffTests\" name=\"AuthenticatedCommandCompletesAsync\" />"
            + "</UnitTest></TestDefinitions><ResultSummary outcome=\"Completed\"><Counters total=\"2\" passed=\"2\" failed=\"0\" /></ResultSummary></TestRun>");

        NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), _passingReport, report, ["run-token"])
            .Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    /// <summary>Checks the handoff exposes endpoints and identity without the signing key.</summary>
    [Fact]
    public void HandoffCarriesRunScopedEndpointsAndIdentity()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), new Uri("http://127.0.0.1:20013/"), []);

            IReadOnlyDictionary<string, string> handoff = NativeTestHandoff.Create(plan, readiness, "access-token");

            handoff[NativeTestHandoff.RunId].ShouldBe(plan.RunId);
            handoff[NativeTestHandoff.EventStoreUrl].ShouldBe("http://127.0.0.1:20012/");
            handoff.ContainsKey(NativeTestHandoff.PeerEventStoreUrl).ShouldBeFalse();
            NativeTestHandoff.Create(plan with { Ports = plan.Ports with { SecondEventStoreHttp = 20027 } }, readiness, "access-token")[NativeTestHandoff.PeerEventStoreUrl]
                .ShouldBe("http://127.0.0.1:20027/");
            handoff[NativeTestHandoff.UiUrl].ShouldBe("http://127.0.0.1:20013/");
            handoff[NativeTestHandoff.Tenant].ShouldBe(plan.TenantNamespace);
            handoff[NativeTestHandoff.ResourceNamespace].ShouldBe(plan.ResourceNamespace);
            handoff[NativeTestHandoff.Domains].ShouldBe("p0-inventory,p0-orders");
            handoff[NativeTestHandoff.AccessToken].ShouldBe("access-token");
            handoff.Keys.ShouldNotContain(CompositionEnvironment.SigningKey);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks the retained report sits beside the requested evidence artifact.</summary>
    [Fact]
    public void RetainedReportPathFollowsEvidencePath()
    {
        NativeTestExecutor.RetainedReportPath("evidence/g4/full.json", "vstest").ShouldBe("evidence/g4/full.vstest.trx");
        NativeTestExecutor.RetainedReportPath("full.json", "mtp").ShouldBe("full.mtp.trx");
    }

    /// <summary>Prepared scopes reject invalid identities before launch, while the valid control actually executes.</summary>
    /// <param name="mutation">The independently invalid owner scope.</param>
    /// <returns>The refusal and never-executed entry assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture mutation is unknown.</exception>
    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-run")]
    [InlineData("foreign-module")]
    [InlineData("wildcard-domain")]
    [InlineData("wildcard-tenant")]
    [InlineData("absent-synthetic-tenant")]
    public async Task PreparedScopeGatesChildExecutionAsync(string mutation)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string marker = Path.Combine(workspace.Root, "child-executed");
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, _markerRunner).ConfigureAwait(true);
        CompositionNativeTestScope scope = new(plan.RunId, "sample", new string('a', 64), ["sample"], [plan.TenantNamespace]);
        scope = mutation switch
        {
            "valid" => scope,
            "foreign-run" => scope with { RunId = "foreign-run" },
            "foreign-module" => scope with { ModuleId = "foreign-module" },
            "wildcard-domain" => scope with { Domains = ["sample*"] },
            "wildcard-tenant" => scope with { Tenants = [plan.TenantNamespace, "*"] },
            "absent-synthetic-tenant" => scope with { Tenants = ["other-tenant"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        CompositionReadiness readiness = HealthyReadiness(plan) with { NativeTestScope = scope };
        using Process process = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);
        if (mutation == "valid")
        {
            File.Exists(marker).ShouldBeTrue();

            // The valid scope launches the marker-only child; its intentionally missing TRX remains a test failure.
            result.Result.Outcome.RuleId.ShouldBe("HXT002");
        }
        else
        {
            AssertNotPassing(result, "HXT009", ToolExitCode.ProductOrTest);
            File.Exists(marker).ShouldBeFalse();
        }
    }

    /// <summary>A prepared scope that differs from the fallback identity is what the launched child actually receives.</summary>
    /// <returns>The decoded token claim and domain handoff assertions.</returns>
    [Fact]
    public async Task PreparedScopeBindsNativeTokenAndDomainHandoffAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string observedPath = Path.Combine(workspace.Root, "child-identity.json");
        const string identityRunner = "import base64, json, os, pathlib\n"
            + "claims = json.loads(base64.urlsafe_b64decode(os.environ['HEXALITH_G4_ACCESS_TOKEN'].split('.')[1] + '=='))\n"
            + "observed = {key: claims.get(key) for key in ['sub', 'tenants', 'domains', 'eventstore:surface']}\n"
            + "observed['HEXALITH_G4_DOMAINS'] = os.environ['HEXALITH_G4_DOMAINS']\n"
            + "pathlib.Path(__file__).with_name('child-identity.json').write_text(json.dumps(observed))\n";
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, identityRunner).ConfigureAwait(true);

        // Unsorted domains and an extra tenant differ from the fallback identity (["sample"], [synthetic tenant]).
        CompositionNativeTestScope scope = new(plan.RunId, "sample", new string('a', 64), ["tenants", "party"], [plan.TenantNamespace, "system"]);
        CompositionReadiness readiness = HealthyReadiness(plan) with { NativeTestScope = scope };
        using Process process = new();

        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);

        File.Exists(observedPath).ShouldBeTrue("The scoped child must launch and record its decoded identity.");
        using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(observedPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement identity = observed.RootElement;
        identity.GetProperty("sub").GetString().ShouldBe("g4-native-tests");
        identity.GetProperty("domains").GetString().ShouldNotBeNull().Split(' ').ShouldBe(["party", "tenants"], ignoreOrder: true);
        identity.GetProperty("tenants").GetString().ShouldNotBeNull().Split(' ').ShouldBe([plan.TenantNamespace, "system"], ignoreOrder: true);
        identity.GetProperty("eventstore:surface").GetString().ShouldBe("agent");
        identity.GetProperty("HEXALITH_G4_DOMAINS").GetString().ShouldBe("party,tenants");

        // The identity-only child writes no TRX, so the launched run still fails on its missing report.
        result.Result.Outcome.RuleId.ShouldBe("HXT002");
        result.Launched.ShouldBeTrue();
        result.StopObserved.ShouldBeTrue();
        result.ProcessIds.Count.ShouldBe(1);
        _ = result.CredentialExpiresAtUtc.ShouldNotBeNull();
        _ = result.ObservedAtUtc.ShouldNotBeNull();
    }

    /// <summary>An unsafe or inconsistent endpoint-credential channel refuses at the executor before the child launches.</summary>
    /// <param name="fault">The private-channel mutation, or valid for the launched control.</param>
    /// <returns>The refusal and never-executed entry assertions.</returns>
    [Theory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("group-readable")]
    [InlineData("foreign-run")]
    [InlineData("endpoints-without-path")]
    public async Task EndpointCredentialChannelGatesChildExecutionAsync(string fault)
    {
        fault.ShouldBeOneOf("valid", "missing", "group-readable", "foreign-run", "endpoints-without-path");
        using PlatformManifestTestWorkspace workspace = new();
        string marker = Path.Combine(workspace.Root, "child-executed");
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, _markerRunner).ConfigureAwait(true);
        Uri invocation = new("http://127.0.0.1:20012/v1.0/invoke/sample/method/api/v1/");
        string credentialPath = Path.Combine(plan.Workspace, NativeTestHandoff.EndpointCredentialsFileName);
        CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
        if (fault != "missing")
        {
            JsonObject channel = new()
            {
                ["schema"] = NativeTestHandoff.EndpointCredentialsSchema,
                ["runId"] = fault == "foreign-run" ? "foreign-run" : plan.RunId,
                ["syntheticTenantId"] = plan.TenantNamespace,
                ["dataIsolationId"] = plan.ResourceNamespace,
                ["endpoints"] = new JsonArray(new JsonObject
                {
                    ["resourceId"] = "owned-resource",
                    ["moduleId"] = "sample",
                    ["endpointName"] = "dapr-invocation",
                    ["uri"] = invocation.AbsoluteUri,
                    ["headerName"] = "dapr-api-token",
                    ["value"] = "fixture-endpoint-key",
                }),
            };
            await File.WriteAllTextAsync(credentialPath, channel.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(credentialPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | (fault == "group-readable" ? UnixFileMode.GroupRead : UnixFileMode.None));
            }
        }

        CompositionReadiness readiness = HealthyReadiness(plan);
        readiness = readiness with
        {
            NativeEndpointCredentialsPath = fault == "endpoints-without-path" ? null : credentialPath,
            NativeHttpEndpoints = [new("owned-resource", "sample", "dapr-invocation", invocation, "owned-sidecar")],
            Resources = [.. readiness.Resources.Select(resource => resource with { Endpoints = [.. resource.Endpoints, new("dapr-invocation", "http", invocation)] })],
        };
        using Process process = new();

        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);

        // Windows has no group mode bit to refuse, so its group-readable fixture is the valid control.
        if (fault == "valid" || (fault == "group-readable" && OperatingSystem.IsWindows()))
        {
            File.Exists(marker).ShouldBeTrue();

            // The valid channel launches the marker-only child; its intentionally missing TRX remains a test failure.
            result.Result.Outcome.RuleId.ShouldBe("HXT002");
        }
        else
        {
            AssertNotPassing(result, "HXT008", ToolExitCode.EvidenceSchemaOrPolicy);
            File.Exists(marker).ShouldBeFalse();
        }
    }

    /// <summary>Malformed public routing observations return the existing pre-child refusal instead of escaping the executor.</summary>
    /// <param name="fault">The malformed proof, or the launched control.</param>
    /// <returns>The refusal, never-launched child and removable owned workspace assertions.</returns>
    [Theory]
    [InlineData("valid")]
    [InlineData("not-required")]
    [InlineData("metadata-missing-status")]
    [InlineData("metadata-wrong-kind")]
    [InlineData("metadata-overflow")]
    [InlineData("closure-missing-hosts")]
    [InlineData("closure-wrong-kind")]
    [InlineData("preparation-missing-flag")]
    [InlineData("preparation-wrong-kind")]
    [InlineData("malformed-json")]
    public async Task RoutingProofShapeGatesChildExecutionAsync(string fault)
    {
        string ownedRoot;
        using (PlatformManifestTestWorkspace workspace = new())
        {
            ownedRoot = workspace.Root;
            string marker = Path.Combine(workspace.Root, "child-executed");
            (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, _markerRunner).ConfigureAwait(true);
            string directory = await WriteRoutingProofShapeAsync(plan, fault).ConfigureAwait(true);
            using Process process = new();
            CompositionReadiness readiness = HealthyReadiness(plan) with { RoutingCatalogObservationsRequired = fault != "not-required" };
            if (fault == "not-required")
            {
                File.Delete(Path.Combine(directory, "metadata-http.json"));
            }

            NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);
            if (fault is "valid" or "not-required")
            {
                File.Exists(marker).ShouldBeTrue();
                result.Result.Outcome.RuleId.ShouldBe("HXT002", "The marker-only control launches without writing a native report.");
            }
            else
            {
                AssertNotPassing(result, "HXT009", ToolExitCode.ProductOrTest);
                File.Exists(marker).ShouldBeFalse();
            }
        }

        Directory.Exists(ownedRoot).ShouldBeFalse("Malformed proof reads must release every owned file before cleanup.");
    }

    /// <summary>An attached run captures immutable owner routing proofs with exact owner, environment and attached identities.</summary>
    /// <param name="foreign">Whether to substitute another owner in the descriptor.</param>
    /// <returns>The immutable capture or explicit mismatch refusal.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedRoutingSnapshotsRequireExactOwnerAsync(bool foreign)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunPlan plan, ModuleTestSelection _, string _) = await PrepareSelectedRunAsync(workspace, string.Empty).ConfigureAwait(true);
        IReadOnlyDictionary<string, byte[]> originals = await WriteRequiredRoutingEvidenceAsync(plan).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string attachedRun = CompositionRunPlanFactory.NewRunId();
        AttachedSuiteIdentity attached = new(new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, attachedRun, 60, now, 60, now.AddSeconds(60)), CompositionRunPlanFactory.NewRunId(), AttachedSuiteIdentity.WorkspaceFor(plan.Workspace, attachedRun), Path.Combine(workspace.Root, "attached-diagnostics"));
        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(new { bindings = new { run = new { runId = attachedRun, ownerRunId = foreign ? CompositionRunPlanFactory.NewRunId() : plan.RunId, environmentId = plan.EnvironmentId, role = "attached" }, environment = new { catalog = new { instanceId = plan.EnvironmentId + "-catalog", commit = plan.SourceMapping!.PlatformIdentity!.CatalogCommit } } } });
        if (foreign)
        {
            _ = await Should.ThrowAsync<InvalidDataException>(() => SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, TestContext.Current.CancellationToken, true, [], attached)).ConfigureAwait(true);
        }
        else
        {
            IReadOnlyDictionary<string, byte[]> held = await SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, TestContext.Current.CancellationToken, true, [], attached).ConfigureAwait(true);
            held.Count.ShouldBe(originals.Count);
            foreach (KeyValuePair<string, byte[]> value in originals)
            {
                held[value.Key].ShouldBe(value.Value);
            }
        }
    }

    /// <summary>The selected executor archives its own original capture after the real child mutates and removes all input proofs.</summary>
    /// <returns>The passing execution and exact bytes in every archive destination.</returns>
    [Fact]
    public async Task NativeExecutionArchivesThePreChildRoutingSnapshotAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        const string runner = """
            import json, os, pathlib, shutil
            plan = json.loads(pathlib.Path(os.environ['HEXALITH_G4_PLAN_PATH']).read_text())
            root = pathlib.Path(plan['workspace'])
            catalog = root / 'platform-components' / (plan['environmentId'] + '-catalog')
            for proof in catalog.glob('*.json'):
                proof.write_text('{"changedByChild":true}')
            shutil.rmtree(catalog)
            for name in ['mapping.json', 'catalog-versions.json']:
                (root / 'source-mapping' / name).write_text('{"changedByChild":true}')
            (root / 'platform-components' / 'catalog-reader' / 'obj' / 'project.assets.json').unlink()
            pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>')
            """;
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, runner).ConfigureAwait(true);
        IReadOnlyDictionary<string, byte[]> originals = await WriteRequiredRoutingEvidenceAsync(plan).ConfigureAwait(true);
        string archive = Path.Combine(workspace.Root, "durable");
        using Process process = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(
            new CompositionRunSession(plan, HealthyReadiness(plan) with { RoutingCatalogObservationsRequired = true }, "private-signing-key-value", process),
            new PersistedProfileNativeTests("runner.py", "python3"),
            manifest,
            TestContext.Current.CancellationToken,
            selection: selection,
            archiveDirectory: archive).ConfigureAwait(true);

        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        result.RoutingCatalogSnapshot.ShouldNotBeNull().Count.ShouldBe(originals.Count);
        Directory.Exists(Path.Combine(plan.Workspace, "platform-components", plan.EnvironmentId + "-catalog")).ShouldBeFalse();
        string identity = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(selection.Name)));
        foreach (KeyValuePair<string, byte[]> file in originals)
        {
            foreach (string destination in new[] { archive, Path.Combine(archive, "entries", identity), Path.Combine(plan.Workspace, "native-tests", identity) })
            {
                (await File.ReadAllBytesAsync(Path.Combine(destination, "routing-catalog", file.Key), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(file.Value);
            }
        }
    }

    /// <summary>Opaque actual handoff values must not survive as decoded routing keys or string values before child launch.</summary>
    /// <param name="source">The handoff credential channel, or none for the launched control.</param>
    /// <param name="asKey">Whether the opaque value appears in a JSON property name.</param>
    /// <returns>The credential exclusion and child admission assertions.</returns>
    [Theory]
    [InlineData("none", false)]
    [InlineData("signing", false)]
    [InlineData("signing", true)]
    [InlineData("endpoint", false)]
    [InlineData("endpoint", true)]
    [InlineData("access-app", false)]
    [InlineData("access-app", true)]
    [InlineData("access-dapr", false)]
    [InlineData("access-dapr", true)]
    public async Task OpaqueHandoffCredentialsCannotEnterRoutingSnapshotsAsync(string source, bool asKey)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunPlan plan, ModuleTestSelection selection, string manifest) = await PrepareSelectedRunAsync(workspace, _markerRunner).ConfigureAwait(true);
        _ = await WriteRequiredRoutingEvidenceAsync(plan).ConfigureAwait(true);
        string signing = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string endpointToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string[] accessTokens = [.. Enumerable.Range(0, 6).Select(_ => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)))];
        CompositionReadiness readiness = await CreateOpaqueHandoffReadinessAsync(plan, endpointToken, accessTokens).ConfigureAwait(true);
        string opaque = source switch
        {
            "signing" => signing,
            "endpoint" => endpointToken,
            "access-app" => accessTokens[0],
            "access-dapr" => accessTokens[1],
            _ => "ordinary-public-observation",
        };
        string payload = asKey
            ? JsonSerializer.Serialize(new Dictionary<string, bool> { [opaque] = true })
            : JsonSerializer.Serialize(new { observation = "prefix-" + opaque + "-suffix" });
        using JsonDocument unrecognized = JsonDocument.Parse(payload);
        SelectedMetadataSafety.IsSafe(unrecognized.RootElement).ShouldBeTrue();
        await File.WriteAllTextAsync(Path.Combine(plan.Workspace, "platform-components", plan.EnvironmentId + "-catalog", "receipt.json"), payload, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string archive = Path.Combine(workspace.Root, "durable");
        using Process process = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, signing, process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection, archiveDirectory: archive).ConfigureAwait(true);
        if (source == "none")
        {
            File.Exists(Path.Combine(workspace.Root, "child-executed")).ShouldBeTrue();
            result.Result.Outcome.RuleId.ShouldBe("HXT002");
        }
        else
        {
            AssertNotPassing(result, "HXT009", ToolExitCode.ProductOrTest);
            result.RoutingCatalogSnapshot.ShouldBeNull();
            File.Exists(Path.Combine(workspace.Root, "child-executed")).ShouldBeFalse();
            Directory.Exists(Path.Combine(archive, "routing-catalog")).ShouldBeFalse();
        }
    }

    private static async Task<string> WriteRoutingProofShapeAsync(CompositionRunPlan plan, string fault)
    {
        string directory = Path.Combine(plan.Workspace, "platform-components", plan.EnvironmentId + "-catalog");
        _ = Directory.CreateDirectory(directory);
        foreach (string name in new[] { "catalog.json", "receipt.json", "activation.json", "owner.committed.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        // These offline fields qualify only the pre-child handoff contract, never an active owner cohort.
        JsonObject closure = new() { ["RunId"] = plan.RunId, ["hosts"] = new JsonArray(new JsonObject { ["ResourceId"] = "owner" }) };
        JsonObject preparation = new() { ["RunId"] = plan.RunId, ["routingCatalogMetadataObserved"] = true };
        JsonObject metadata = new() { ["method"] = "GET", ["path"] = "/api/v1/routing/catalog", ["status"] = 200 };
        MutateRoutingProofShape(fault, closure, preparation, metadata);
        await File.WriteAllTextAsync(Path.Combine(directory, "selected-closure.json"), closure.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "preparation.json"), preparation.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), fault == "malformed-json" ? "{" : metadata.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string assets = Path.Combine(plan.Workspace, "platform-components", "catalog-reader", "obj");
        _ = Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "project.assets.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);

        return directory;
    }

    private static void MutateRoutingProofShape(string fault, JsonObject closure, JsonObject preparation, JsonObject metadata)
    {
        if (fault == "metadata-missing-status")
        {
            _ = metadata.Remove("status");
        }

        if (fault == "metadata-wrong-kind")
        {
            metadata["status"] = "200";
        }

        if (fault == "metadata-overflow")
        {
            metadata["status"] = long.MaxValue;
        }

        if (fault == "closure-missing-hosts")
        {
            _ = closure.Remove("hosts");
        }

        if (fault == "closure-wrong-kind")
        {
            closure["hosts"] = new JsonObject();
        }

        if (fault == "preparation-missing-flag")
        {
            _ = preparation.Remove("routingCatalogMetadataObserved");
        }

        if (fault == "preparation-wrong-kind")
        {
            preparation["routingCatalogMetadataObserved"] = "true";
        }
    }

    private static async Task<CompositionReadiness> CreateOpaqueHandoffReadinessAsync(CompositionRunPlan plan, string endpointToken, string[] accessTokens)
    {
        Uri invocation = new("http://127.0.0.1:20012/v1.0/invoke/sample/method/api/v1/");
        string endpointPath = await WriteOpaqueEndpointCredentialsAsync(plan, endpointToken, invocation).ConfigureAwait(true);
        string accessPath = Path.Combine(plan.Workspace, "platform-components", "retention", "access.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(accessPath)!);
        string accessContent = JsonSerializer.Serialize(Enumerable.Range(0, 3).ToDictionary(index => "service-" + index, index => new { APP_API_TOKEN = accessTokens[index * 2], DAPR_API_TOKEN = accessTokens[(index * 2) + 1] }));
        await File.WriteAllTextAsync(accessPath, accessContent, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionReadiness readiness = HealthyReadiness(plan);
        return readiness with
        {
            RoutingCatalogObservationsRequired = true,
            NativeEndpointCredentialsPath = endpointPath,
            NativeHttpEndpoints = [new("owned-resource", "sample", "dapr-invocation", invocation, "owned-sidecar")],
            Resources = [.. readiness.Resources.Select(resource => resource with { Endpoints = [.. resource.Endpoints, new("dapr-invocation", "http", invocation)] })],
            TestObservation = new() { RunId = plan.RunId, ModuleId = "sample", ProfileHash = new string('a', 64), ProfileJson = "{}", AccessPath = accessPath },
        };
    }

    private static async Task<string> WriteOpaqueEndpointCredentialsAsync(CompositionRunPlan plan, string endpointToken, Uri invocation)
    {
        string endpointPath = Path.Combine(plan.Workspace, NativeTestHandoff.EndpointCredentialsFileName);
        string endpointContent = JsonSerializer.Serialize(new
        {
            schema = NativeTestHandoff.EndpointCredentialsSchema,
            runId = plan.RunId,
            syntheticTenantId = plan.TenantNamespace,
            dataIsolationId = plan.ResourceNamespace,
            endpoints = new[] { new { resourceId = "owned-resource", moduleId = "sample", endpointName = "dapr-invocation", uri = invocation.AbsoluteUri, headerName = "dapr-api-token", value = endpointToken } },
        });
        await File.WriteAllTextAsync(endpointPath, endpointContent, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(endpointPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return endpointPath;
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> WriteRequiredRoutingEvidenceAsync(CompositionRunPlan plan)
    {
        string directory = Path.Combine(plan.Workspace, "platform-components", plan.EnvironmentId + "-catalog");
        _ = Directory.CreateDirectory(directory);
        Dictionary<string, byte[]> originals = new(StringComparer.Ordinal);
        foreach (string name in new[] { "catalog.json", "receipt.json", "activation.json", "owner.committed.json" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes("{ \"heldOriginal\" : \"" + name + "\" }\n");
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            originals.Add(name, bytes);
        }

        foreach ((string name, byte[] bytes) in new[]
        {
            ("selected-closure.json", JsonSerializer.SerializeToUtf8Bytes(new { plan.RunId, hosts = new[] { new { ResourceId = "owner" } } })),
            ("preparation.json", JsonSerializer.SerializeToUtf8Bytes(new { plan.RunId, routingCatalogMetadataObserved = true })),
            ("metadata-http.json", JsonSerializer.SerializeToUtf8Bytes(new { method = "GET", path = "/api/v1/routing/catalog", status = 200 })),
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, name), bytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            originals.Add(name, bytes);
        }

        string assets = Path.Combine(plan.Workspace, "platform-components", "catalog-reader", "obj", "project.assets.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(assets)!);
        await File.WriteAllTextAsync(assets, "{\"heldOriginal\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        foreach ((string source, string name) in new[]
        {
            (Path.Combine(plan.Workspace, "source-mapping", "mapping.json"), "mapping.json"),
            (Path.Combine(plan.Workspace, "source-mapping", "catalog-versions.json"), "catalog-versions.json"),
            (assets, "reader-assets.json"),
        })
        {
            originals.Add(name, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        return originals;
    }

    private static async Task<(CompositionRunPlan Plan, ModuleTestSelection Selection, string Manifest)> PrepareSelectedRunAsync(PlatformManifestTestWorkspace workspace, string runner)
    {
        workspace.Document["modules"]![0]!["tests"] = JsonNode.Parse("""
            [{"name":"verify","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":60,"stopTimeoutSeconds":30,"mode":"source"}]
            """);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), runner, TestContext.Current.CancellationToken).ConfigureAwait(false);
        string manifest = workspace.Save();
        PlatformManifestValidationResult validated = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        validated.IsValid.ShouldBeTrue();
        ModuleTestSelection selection = ModuleTestSelection.Select(validated.Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        SourceMapping mapping = new(WorkspaceMode.Source, workspace.Root, "sample", [new SourceMappingEntry("sample", "source", workspace.Root)], false);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root, CompositionRunPlanFactory.NewRunId()) with { SelectedTest = selection, SourceMapping = mapping, Modules = [], UiMarkers = [] };
        plan = await TestEnvironmentProvisioningFixture.PrepareAsync(plan, manifest, workspace.Root).ConfigureAwait(false);
        return (plan, selection, manifest);
    }

    private static CompositionReadiness HealthyReadiness(CompositionRunPlan plan) =>
        TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned-resource", "process", "Healthy")]), "sample");

    private static void AssertNotPassing(NativeTestExecutionResult result, string ruleId, ToolExitCode exitCode)
    {
        result.Result.Outcome.ExitCode.ShouldBe(exitCode);
        result.Result.Outcome.RuleId.ShouldBe(ruleId);
        result.Result.Diagnostics.ShouldHaveSingleItem().RuleId.ShouldBe(ruleId);
        result.Report.ShouldBeNull();
        result.ReportBytes.ShouldBeNull();
    }
}
