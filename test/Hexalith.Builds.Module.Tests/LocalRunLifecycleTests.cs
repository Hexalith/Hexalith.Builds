// <copyright file="LocalRunLifecycleTests.cs" company="ITANEO">
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

/// <summary>Exercises first outcomes, local retention, exact cleanup, retry and cross-process races.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class LocalRunLifecycleTests
{
    /// <summary>Observed success cleans and failures retain survivors before optional archival.</summary>
    /// <param name="mode">The selected local mode.</param>
    /// <param name="fails">Whether the real native child fails.</param>
    /// <param name="archiveFails">Whether optional archival fails after the first observed pass.</param>
    /// <param name="cleanupFails">Whether deletion remains incomplete after the first observed pass.</param>
    /// <param name="evidenceFails">Whether the optional evidence destination is blocked.</param>
    /// <returns>The output, result and workspace assertions.</returns>
    [Theory]
    [InlineData("source", false, false, false, false)]
    [InlineData("source", true, false, false, false)]
    [InlineData("source", false, true, false, false)]
    [InlineData("source", false, false, true, false)]
    [InlineData("source", false, false, true, true)]
    [InlineData("source", false, false, false, true)]
    public async Task NativeOutcomeControlsLocalCleanupAsync(string mode, bool fails, bool archiveFails, bool cleanupFails, bool evidenceFails)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace, mode);
        string script = """
            import json, os, pathlib, sys
            descriptor = pathlib.Path(os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'])
            original = descriptor.read_text()
            (pathlib.Path(__file__).parent / 'original-descriptor.json').write_text(original)
            changed = json.loads(original)
            changed['bindings']['environment']['catalog']['commit'] = 'child-replaced-catalog'
            descriptor.write_text(json.dumps(changed))
            pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>')
            """ + (fails ? "\nsys.exit(1)" : string.Empty);
        if (archiveFails)
        {
            script += "\narchive = pathlib.Path(__file__).parent / 'state' / 'diagnostics' / os.environ['HEXALITH_G4_RUN_ID'] / 'entries'\narchive.parent.mkdir(parents=True, exist_ok=True)\narchive.write_text('optional archive unavailable')\n";
        }

        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), script, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using Process appHost = new();
        CompositionRunSession? session = null;
        int cleanups = 0;
        WorkspaceMode artifactMode = mode == "source" ? WorkspaceMode.Source : WorkspaceMode.Package;
        string nativeDotnet = Path.Combine(workspace.Root, "native-dotnet");
        CompositionTestFiles.WriteScript(nativeDotnet, "exec python3 '" + Path.Combine(workspace.Root, "runner.py") + "'");
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            SelectedTest = null,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            NativeDotnetCommand = nativeDotnet,
            SourceMapping = new SourceMapping(artifactMode, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            ContinuousIntegrationOverride = false,
            SelectedPlatformStartOverride = async (_, selection, token) =>
            {
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                session = new CompositionRunSession(plan, ready, "private-lifecycle-signing-key", appHost);
                await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = (_, _) =>
            {
                cleanups++;
                if (cleanupFails)
                {
                    ToolDiagnostic failedCleanup = new("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned deletion remains incomplete.", "down");
                    return Task.FromResult(new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(failedCleanup.Phase, failedCleanup.Category, failedCleanup.RuleId, ToolExitCode.TopologyOrLifecycle), [failedCleanup]), new CompositionRunResources([], [], true) { WorkspaceIds = ["workspace:" + session!.RunId] }));
                }

                Directory.Delete(session!.Plan.Workspace, true);
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], [])));
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        if (evidenceFails)
        {
            _ = Directory.CreateDirectory(Path.Combine(workspace.Root, "evidence", "result.json"));
        }

        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, evidenceFails ? "evidence/result.json" : null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options, mode: artifactMode).ConfigureAwait(true);
        int expectedExit = 0;
        if (fails)
        {
            expectedExit = 4;
        }

        exit.ShouldBe(expectedExit, output.ToString());
        cleanups.ShouldBe(fails ? 0 : 1);
        session.ShouldNotBeNull().FirstObservedOutcome.ShouldNotBeNull().MetadataRecordingTimestamp.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(session.StoppingTimestamp.ShouldNotBeNull());
        Directory.Exists(session.Plan.Workspace).ShouldBe(fails || cleanupFails);
        string resultPath = Path.Combine(options.StateDirectory, "diagnostics", session.RunId, "test-result.json");
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(output.ToString());
        JsonElement document = result.Document!.Value;
        document.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe(fails ? "failed" : "passed");
        document.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(fails ? "failure-retained" : "not-retained");
        string expectedCleanup = (fails, cleanupFails) switch
        {
            (true, _) => "not-requested",
            (_, true) => "incomplete",
            _ => "complete",
        };
        document.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe(expectedCleanup);
        using JsonDocument original = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "original-descriptor.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement originalBindings = original.RootElement.GetProperty("bindings");
        JsonElement retainedBindings = document.GetProperty("bindings");
        JsonElement.DeepEquals(retainedBindings.GetProperty("environment"), originalBindings.GetProperty("environment")).ShouldBeTrue();
        JsonElement.DeepEquals(retainedBindings.GetProperty("run"), originalBindings.GetProperty("run")).ShouldBeTrue();
        JsonElement.DeepEquals(retainedBindings.GetProperty("deadlines").GetProperty("startup"), originalBindings.GetProperty("deadlines").GetProperty("startup")).ShouldBeTrue();
        retainedBindings.GetProperty("deadlines").GetProperty("execution").GetProperty("originAt").GetString().ShouldBe(originalBindings.GetProperty("deadlines").GetProperty("execution").GetProperty("originAt").GetString());
        if (fails)
        {
            await AssertRetainedWorkspaceAsync(options, session.RunId, document).ConfigureAwait(true);
        }

        if (evidenceFails)
        {
            using JsonDocument rendered = JsonDocument.Parse(output.ToString());
            rendered.RootElement.GetProperty("testResultPath").GetString().ShouldBe(resultPath);
            string?[] rules = [.. rendered.RootElement.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("ruleId").GetString())];
            rules.ShouldContain("HXE160");
            if (cleanupFails)
            {
                rendered.RootElement.GetProperty("remainingResources").GetProperty("workspaceIds").EnumerateArray().Select(id => id.GetString()).ShouldContain("workspace:" + session.RunId);
                rules.ShouldContain("HXR023");
            }
            else
            {
                JsonElement remaining = rendered.RootElement.GetProperty("remainingResources");
                remaining.GetProperty("isEmpty").GetBoolean().ShouldBeTrue();
                remaining.GetProperty("unverified").GetBoolean().ShouldBeFalse();
                foreach (string collection in new[] { "containerIds", "processIds", "workspaceIds", "claimIds", "discoveryFailures" })
                {
                    remaining.GetProperty(collection).GetArrayLength().ShouldBe(0);
                }

                rules.ShouldNotContain("HXR023");
            }
        }

        string diagnostics = await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        diagnostics.ShouldNotContain("private-lifecycle-signing-key");
    }

    /// <summary>Package partial-startup results use the same outcome/retention state machine without inventing unavailable published runtime proof.</summary>
    /// <param name="fails">Whether the package owner failed.</param>
    /// <returns>The package contract, survivor and cleanup assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PackageOutcomesPreserveRetentionAndOwnedCleanupAsync(bool fails)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace, "package");
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using Process owned = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = state.RunId }) };
        owned.Start().ShouldBeTrue();
        try
        {
            ToolCommandResult outcome = fails ? Failure("HXT007") : new ToolCommandResult("completed", ToolOutcome.Passed(), []);
            ModuleTestRunRecord record = (await engine.RecordTestOutcomeAsync(state.RunId, [], outcome, fails ? "retained" : "unknown", new CompositionRunResources([], [owned.Id]), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            record.FailureRetained.ShouldBe(fails);
            owned.HasExited.ShouldBeFalse();
            string path = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "test-result.json");
            TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
            result.IsValid.ShouldBeTrue();
            result.Document!.Value.GetProperty("bindings").GetProperty("environment").GetProperty("mode").GetString().ShouldBe("package");
            result.Document.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(fails ? "failure-retained" : "not-retained");
            CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            owned.Refresh();
            owned.HasExited.ShouldBeTrue();
            TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document!.Value.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe(fails ? "failed" : "passed");
        }
        finally
        {
            CompositionProcess.Kill(owned);
        }
    }

    /// <summary>Later cancellation preserves failure cause/time and local retention.</summary>
    /// <param name="failureFirst">Whether failure precedes owner cancellation.</param>
    /// <returns>The first-outcome and resource assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationPreservesFirstOutcomeAndFailureRetentionAsync(bool failureFirst)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        ToolCommandResult failed = Failure("HXT007");
        ToolCommandResult cancelled = new("cancelled", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled), []);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], failureFirst ? failed : cancelled, failureFirst ? "retained" : "unknown", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ModuleTestRunRecord later = (await engine.RecordTestOutcomeAsync(state.RunId, [], cancelled, "unknown", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        later.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        later.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        later.FailureRetained.ShouldBe(failureFirst);
        Directory.Exists(state.Workspace).ShouldBeTrue();
        if (!failureFirst)
        {
            (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            Directory.Exists(state.Workspace).ShouldBeFalse();
        }

        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "test-result.json"), TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe(failureFirst ? "failed" : "cancelled");
        result.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(failureFirst ? "failure-retained" : "not-retained");
    }

    /// <summary>Failed discovery retains exact claims and retry metadata while peer ownership remains untouched.</summary>
    /// <param name="format">The public output format.</param>
    /// <returns>The incomplete output, peer and idempotent retry assertions.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task DiscoveryFailureReportsClaimsAndRetryPreservesPeerAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        engine.StateStore.Claims.Acquire(state.RunId, ["port-owner-fixture"]);
        string peerRunId = CompositionRunPlanFactory.NewRunId();
        engine.StateStore.Claims.Acquire(peerRunId, ["peer-data-fixture"]);
        string peerData = Path.Combine(workspace.Root, "peer-data.txt");
        await File.WriteAllTextAsync(peerData, "peer-content", TestContext.Current.CancellationToken).ConfigureAwait(true);
        using Process peer = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = peerRunId }) };
        peer.Start().ShouldBeTrue();
        try
        {
            CompositionTestFiles.WriteScript(options.DockerCommand, "exit 1");
            CompositionDownResult incomplete = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            incomplete.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
            incomplete.Remaining.Unverified.ShouldBeTrue();
            incomplete.Remaining.ClaimIds.ShouldNotBeEmpty();
            _ = (await engine.StateStore.TryReadAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            _ = await ModuleCommandApplication.InvokeAsync(["down", "--run-id", state.RunId, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
            output.ToString().ShouldContain("claim:");
            output.ToString().ShouldContain("unverified");
            string resultPath = Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "test-result.json");
            if (format == "json")
            {
                using JsonDocument rendered = JsonDocument.Parse(output.ToString());
                rendered.RootElement.GetProperty("testResultPath").GetString().ShouldBe(resultPath);
            }
            else
            {
                output.ToString().ShouldContain("test-result=" + resultPath);
                output.ToString().ShouldContain("leftover=claim:");
            }

            _ = CompositionTestFiles.CreateStubDocker(workspace.Root);
            CompositionDownResult retried = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            retried.Remaining.IsEmpty.ShouldBeTrue();
            (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            peer.Refresh();
            peer.HasExited.ShouldBeFalse();
            (await File.ReadAllTextAsync(peerData, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("peer-content");
            engine.StateStore.Claims.FindOwned(peerRunId).ShouldNotBeEmpty();
        }
        finally
        {
            CompositionProcess.Kill(peer);
        }
    }

    /// <summary>Unverifiable claim ownership cannot be mistaken for verified deletion or attributed to another owner.</summary>
    /// <returns>The preserved claim and retry assertions.</returns>
    [Fact]
    public async Task CorruptClaimDiscoveryStaysIncompleteUntilOwnershipIsRestoredAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        engine.StateStore.Claims.Acquire(state.RunId, ["owned-claim"]);
        string claim = Directory.GetFiles(Path.Combine(engine.StateStore.Directory, "claims"), "*.json").Single();
        byte[] original = await File.ReadAllBytesAsync(claim, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(claim, "{invalid credential=fixture-do-not-echo}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult incomplete = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        incomplete.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        incomplete.Remaining.Unverified.ShouldBeTrue();
        incomplete.Remaining.DiscoveryFailures.ShouldContain("claims");
        File.Exists(claim).ShouldBeTrue();
        _ = (await engine.StateStore.TryReadAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        await File.WriteAllBytesAsync(claim, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult retried = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        File.Exists(claim).ShouldBeFalse();
    }

    /// <summary>A cleanup timeout preserves the last exact resource observation and explicit uncertainty.</summary>
    /// <returns>The bounded known-leftover assertion.</returns>
    [Fact]
    public async Task CleanupDeadlinePreservesKnownContainerIdentityAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string container = new('a', 64);
        string docker = Path.Combine(workspace.Root, "docker");
        CompositionTestFiles.WriteScript(docker, $"""if [ "$1" = ps ]; then echo {container}; else sleep 10; fi""");
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(500));
        Stopwatch elapsed = Stopwatch.StartNew();
        CompositionRunResources remaining = await new CompositionRunResourceScanner(docker).RemoveAsync(CompositionTestFiles.RunId, deadline.Token).ConfigureAwait(true);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        remaining.ContainerIds.ShouldBe([container]);
        remaining.Unverified.ShouldBeTrue();
        remaining.DiscoveryFailures.ShouldContain("cleanup-verification");
    }

    /// <summary>A child-replaced readiness FIFO cannot delay terminal recording or replace trusted identity observations.</summary>
    /// <returns>The bounded durable recording check.</returns>
    [Fact]
    public async Task TerminalRecordingDoesNotReadChildReadinessFifoAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult fifo = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("mkfifo", [Path.Combine(state.Workspace, "readiness.json")], null, null), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fifo.ExitCode.ShouldBe(0);
        Stopwatch elapsed = Stopwatch.StartNew();
        ModuleTestRunRecord record = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        record.TerminalStatus.ShouldBe("failed");
        TestEnvironmentContractReader.ReadResult(Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId, "test-result.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Separate runner processes serialize terminal/cleanup commits without resurrecting removed resources.</summary>
    /// <returns>The durable first failure and final cleanup assertions.</returns>
    [Fact]
    public async Task ExplicitStopAcrossProcessesPreservesFirstFailureAsync()
    {
        const string variable = "HEXALITH_LIFECYCLE_RACE_ROOT";
        string? childRoot = Environment.GetEnvironmentVariable(variable);
        if (childRoot is not null)
        {
            CompositionRunStateStore store = new(Path.Combine(childRoot, "state"));
            CompositionRunState state = (await store.TryReadAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            CompositionEngine child = new(LocalRunLifecycleFixture.Options(childRoot, state.SelectedTest!) with { StopTimeout = TimeSpan.FromSeconds(state.SelectedTest!.StopTimeoutSeconds) });
            await File.WriteAllTextAsync(Path.Combine(childRoot, "cleanup-started"), "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionDownResult down = await child.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            using CancellationTokenSource ownerFinished = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            ownerFinished.CancelAfter(TimeSpan.FromSeconds(10));
            while (!File.Exists(Path.Combine(childRoot, "owner-updates-complete")))
            {
                await Task.Delay(10, ownerFinished.Token).ConfigureAwait(true);
            }

            if (down.Result.Outcome.ExitCode != ToolExitCode.Success)
            {
                // Retry only after the conflicting owner has finished, within a fresh declared cleanup allowance.
                down.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008" || diagnostic.RuleId == "HXR023");
                down = await child.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState issued) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected) with { StopTimeout = TimeSpan.FromSeconds(selected.StopTimeoutSeconds) });
        CompositionWorkspace.CreatePrivateDirectory(issued.Workspace);
        await engine.StateStore.WriteAsync(issued, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(issued.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        ProcessStartInfo start = new("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (string argument in new[] { typeof(LocalRunLifecycleTests).Assembly.Location, "-method", "*ExplicitStopAcrossProcessesPreservesFirstFailureAsync", "-noLogo" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[variable] = workspace.Root;
        using Process other = new() { StartInfo = start };
        other.Start().ShouldBeTrue();
        Task<string> output = other.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = other.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using CancellationTokenSource cleanupStarted = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cleanupStarted.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(workspace.Root, "cleanup-started")) && !other.HasExited)
        {
            await Task.Delay(10, cleanupStarted.Token).ConfigureAwait(true);
        }

        File.Exists(Path.Combine(workspace.Root, "cleanup-started")).ShouldBeTrue();
        for (int update = 0; update < 20; update++)
        {
            _ = await engine.RecordTestOutcomeAsync(issued.RunId, [], new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled), []), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "owner-updates-complete"), "finished", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await other.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        other.ExitCode.ShouldBe(0, await output.ConfigureAwait(true) + await error.ConfigureAwait(true));

        // A late owner callback after the other process deleted state must also preserve completion.
        ModuleTestRunRecord late = (await engine.RecordTestOutcomeAsync(issued.RunId, [], Failure("HXT009"), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        late.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        late.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        late.CleanupDisposition.ShouldBe("complete");
        late.ResourceDisposition.ShouldBe("removed");
        (await engine.StateStore.TryReadAsync(issued.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
        TestEnvironmentContractReader.ReadResult(Path.Combine(engine.StateStore.Directory, "diagnostics", issued.RunId, "test-result.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Concurrent stops and explicit late cleanup updates cannot reverse verified completion or release.</summary>
    /// <returns>The monotonic cleanup and missing tracking assertions.</returns>
    [Fact]
    public async Task OverlappingDownAndLateCleanupUpdatesPreserveCompletionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionTestFiles.WriteScript(options.DockerCommand, "sleep 0.1; exit 0");
        CompositionDownResult[] stops = await Task.WhenAll(engine.DownAsync(state.RunId, TestContext.Current.CancellationToken), new CompositionEngine(options).DownAsync(state.RunId, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        stops.ShouldAllBe(result => result.Result.Outcome.ExitCode == ToolExitCode.Success);
        foreach (string stale in new[] { "in-progress", "incomplete" })
        {
            ModuleTestRunRecord late = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT009"), "retained", new CompositionRunResources([new string('a', 64)], [], true), TestContext.Current.CancellationToken, cleanupDisposition: stale, cleanupAttemptId: CompositionRunPlanFactory.NewRunId()).ConfigureAwait(true)).ShouldNotBeNull();
            late.CleanupDisposition.ShouldBe("complete");
            late.ResourceDisposition.ShouldBe("removed");
            late.RemainingResources.IsEmpty.ShouldBeTrue();
            late.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        }

        (await engine.StateStore.TryReadAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "test-result.json"), TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("released");
    }

    /// <summary>Durable owner metadata recovers a missing or malformed active state, and invalid ownership refuses cleanup.</summary>
    /// <param name="malformed">Whether the active document is malformed rather than absent.</param>
    /// <param name="invalidOwner">Whether durable owner metadata disagrees with the requested run.</param>
    /// <returns>The recovered cleanup and preserved first-outcome assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DownRecoversAndValidatesDurableOwnerAsync(bool malformed, bool invalidOwner)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        File.Delete(engine.StateStore.PathFor(state.RunId));
        if (malformed)
        {
            await File.WriteAllTextAsync(engine.StateStore.PathFor(state.RunId), "{malformed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (invalidOwner)
        {
            await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "owner.json"), state with { RunId = CompositionRunPlanFactory.NewRunId() }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(invalidOwner ? ToolExitCode.TopologyOrLifecycle : ToolExitCode.Success);
        Directory.Exists(state.Workspace).ShouldBe(invalidOwner);
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBe(first.TerminalAtUtc);
        result.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("failed");
        result.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe(invalidOwner ? "not-requested" : "complete");
    }

    /// <summary>Ordinary run and debug owners intentionally omit execution context and remain cleanable from active or durable metadata.</summary>
    /// <param name="debug">Whether debugger attachment was requested.</param>
    /// <param name="recoverOwner">Whether only the durable owner snapshot remains.</param>
    /// <returns>The cleanup and preserved unknown-context assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OrdinaryRunAndDebugOwnersWithUnknownContextCleanUpAsync(bool debug, bool recoverOwner)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState selectedState) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { SelectedTest = null, DebugAttach = debug };
        CompositionEngine engine = new(options);
        CompositionRunState state = selectedState with { SelectedTest = null, ExecutionContext = null };
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        engine.StateStore.Claims.Acquire(state.RunId, ["ordinary-owner"]);
        string ownerPath = Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "owner.json");
        await CompositionDocumentStore.WriteAsync(ownerPath, state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (!recoverOwner)
        {
            await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        Directory.Exists(state.Workspace).ShouldBeFalse();
        engine.StateStore.Claims.FindOwned(state.RunId).ShouldBeEmpty();
        (await engine.StateStore.TryReadAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        CompositionRunState owner = (await CompositionDocumentStore.TryReadAsync<CompositionRunState>(ownerPath, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        owner.ExecutionContext.ShouldBeNull();
        owner.CreatedAt.ShouldBe(state.CreatedAt);
        File.Exists(Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "test-result.json")).ShouldBeFalse();
    }

    /// <summary>Legacy selected owners recover without inventing absent timestamps, identities, or execution context.</summary>
    /// <param name="identityShape">Which newer identity observations were actually available.</param>
    /// <returns>The cleanup, first-outcome and honest legacy metadata assertions.</returns>
    [Theory]
    [InlineData("absent")]
    [InlineData("attempt-only")]
    [InlineData("environment-only")]
    [InlineData("both")]
    public async Task LegacySelectedOwnerRecoveryPreservesUnknownFieldsAsync(string identityShape)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState issued) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        CompositionRunState state = issued with
        {
            CreatedAt = null,
            ExecutionContext = null,
            AttemptId = identityShape is "attempt-only" or "both" ? issued.AttemptId : null,
            EnvironmentId = identityShape is "environment-only" or "both" ? issued.EnvironmentId : null,
        };
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        File.Delete(engine.StateStore.PathFor(state.RunId));
        CompositionEngine independent = new(options with { SelectedTest = null });
        (await independent.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        Directory.Exists(state.Workspace).ShouldBeFalse();
        string directory = Path.Combine(options.StateDirectory, "diagnostics", state.RunId);
        ModuleTestRunRecord final = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        final.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        final.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        final.CleanupDisposition.ShouldBe("complete");
        CompositionRunState owner = (await CompositionDocumentStore.TryReadAsync<CompositionRunState>(Path.Combine(directory, "owner.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        owner.CreatedAt.ShouldBeNull();
        owner.ExecutionContext.ShouldBeNull();
        owner.AttemptId.ShouldBe(state.AttemptId);
        owner.EnvironmentId.ShouldBe(state.EnvironmentId);
        File.Exists(Path.Combine(directory, "test-result.json")).ShouldBeFalse();
    }

    /// <summary>Optional legacy metadata never weakens exact ownership or validation of identities that are present.</summary>
    /// <param name="invalidField">The mismatched or invalid ownership field.</param>
    /// <returns>The cleanup refusal and preserved workspace assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture names an unsupported mutation.</exception>
    [Theory]
    [InlineData("schema")]
    [InlineData("run")]
    [InlineData("workspace")]
    [InlineData("attempt")]
    [InlineData("environment")]
    [InlineData("duplicate")]
    [InlineData("owner-as-attempt")]
    public async Task LegacyUnknownsDoNotPermitMismatchedOwnershipAsync(string invalidField)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState issued) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { SelectedTest = null };
        CompositionEngine engine = new(options);
        CompositionRunState state = issued with { SelectedTest = null, CreatedAt = null, ExecutionContext = null };
        state = invalidField switch
        {
            "schema" => state with { Schema = "unsupported" },
            "run" => state with { RunId = CompositionRunPlanFactory.NewRunId() },
            "workspace" => state with { Workspace = Path.Combine(workspace.Root, "peer") },
            "attempt" => state with { AttemptId = "invalid" },
            "environment" => state with { EnvironmentId = "invalid" },
            "duplicate" => state with { EnvironmentId = state.AttemptId },
            "owner-as-attempt" => state with { AttemptId = issued.RunId },
            _ => throw new ArgumentOutOfRangeException(nameof(invalidField)),
        };
        CompositionWorkspace.CreatePrivateDirectory(issued.Workspace);
        await CompositionDocumentStore.WriteAsync(Path.Combine(options.StateDirectory, "diagnostics", issued.RunId, "owner.json"), state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult down = await engine.DownAsync(issued.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        Directory.Exists(issued.Workspace).ShouldBeTrue();
    }

    /// <summary>Failure to remove tracking cannot publish completed cleanup and remains retryable.</summary>
    /// <returns>The state-finalization and retry assertions.</returns>
    [Fact]
    public async Task TrackingDeletionFailureDoesNotPublishCompleteAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionEngine engine = new(options);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXT007"), "retained", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string statePath = engine.StateStore.PathFor(state.RunId);
        CompositionTestFiles.WriteScript(options.DockerCommand, $"if [ -f '{statePath}' ]; then rm '{statePath}'; mkdir '{statePath}'; fi; exit 0");
        CompositionDownResult down = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        Directory.Exists(statePath).ShouldBeTrue();
        string resultPath = Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "test-result.json");
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe("incomplete");
        Directory.Delete(statePath);
        _ = CompositionTestFiles.CreateStubDocker(workspace.Root);
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe("complete");
    }

    /// <summary>Claim traversal refuses special files and oversized claims within a finite read.</summary>
    /// <param name="fifo">Whether the invalid claim is a FIFO instead of oversized JSON.</param>
    /// <returns>The bounded refusal assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClaimTraversalRefusesUnsafePayloadAsync(bool fifo)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionResourceClaimStore claims = new(Path.Combine(workspace.Root, "state"));
        claims.Acquire(CompositionTestFiles.RunId, ["owned"]);
        string claim = Directory.GetFiles(Path.Combine(workspace.Root, "state", "claims"), "*.json").Single();
        File.Delete(claim);
        if (fifo)
        {
            CompositionProcessResult created = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("mkfifo", [claim], null, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            created.ExitCode.ShouldBe(0);
        }
        else
        {
            await File.WriteAllTextAsync(claim, new string(' ', 65537), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        Stopwatch elapsed = Stopwatch.StartNew();
        _ = Should.Throw<IOException>(() => claims.FindOwned(CompositionTestFiles.RunId));
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
    }

    /// <summary>A failed verification scan preserves identities known before attempted removal.</summary>
    /// <returns>The known-container uncertainty assertions.</returns>
    [Fact]
    public async Task RemovalThenDiscoveryFailurePreservesKnownContainerAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string container = new('b', 64);
        string marker = Path.Combine(workspace.Root, "removed");
        string docker = Path.Combine(workspace.Root, "docker");
        CompositionTestFiles.WriteScript(docker, $"if [ \"$1\" = rm ]; then touch '{marker}'; exit 0; fi; if [ -f '{marker}' ]; then exit 1; fi; echo {container}");
        using CancellationTokenSource bound = new(TimeSpan.FromMilliseconds(1400));
        CompositionRunResources remaining = await new CompositionRunResourceScanner(docker).RemoveAsync(CompositionTestFiles.RunId, bound.Token).ConfigureAwait(true);
        remaining.ContainerIds.ShouldBe([container]);
        remaining.DiscoveryFailures.ShouldContain("containers");
        remaining.Unverified.ShouldBeTrue();
    }

    /// <summary>A package command retains its owner when a source-only handoff truthfully refuses package authentication.</summary>
    /// <returns>The pre-launch refusal and command-layer retention assertions.</returns>
    [Fact]
    public async Task PackageCommandFailureEstablishesLocalRetentionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace, "package");
        string manifest = workspace.Save();
        using Process appHost = new();
        CompositionRunSession? session = null;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            RetainOnFailure = false,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            ContinuousIntegrationOverride = false,
            SourceMapping = new SourceMapping(WorkspaceMode.Package, workspace.Root, "sample", [new("sample", "package", workspace.Root)], false),
            SelectedPlatformStartOverride = async (_, selection, token) =>
            {
                // Real source observations cannot authenticate a package handoff. No released package artifacts are invented.
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("source-owned", "process", "Healthy")]), "sample");
                session = new CompositionRunSession(plan, ready, "private-key", appHost);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options, mode: WorkspaceMode.Package).ConfigureAwait(true);
        exit.ShouldNotBe(0);
        output.ToString().ShouldContain("HXT009");
        _ = session.ShouldNotBeNull();
        string resultPath = Path.Combine(options.StateDirectory, "diagnostics", session.RunId, "test-result.json");
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(output.ToString());
        result.Document!.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("failure-retained");
        result.Document.Value.GetProperty("bindings").GetProperty("environment").GetProperty("catalog").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        await AssertRetainedWorkspaceAsync(options, session.RunId, result.Document.Value).ConfigureAwait(true);
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
        Directory.Exists(Path.Combine(session.Plan.Workspace, "native-tests")).ShouldBeFalse();
    }

    /// <summary>An expired shared stopping token returns retryable uncertainty with the session's known observations.</summary>
    /// <returns>The unchanged allowance and last-known observation assertions.</returns>
    [Fact]
    public async Task ExpiredSharedStoppingTokenReturnsKnownIncompleteCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        using Process appHost = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected };
        CompositionReadiness ready = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("known", "process", "Healthy") { ProcessId = 12345 }]);
        CompositionRunSession session = new(plan, ready, "private-key", appHost);
        using CancellationTokenSource expired = new();
        await expired.CancelAsync().ConfigureAwait(true);
        Stopwatch elapsed = Stopwatch.StartNew();
        CompositionDownResult down = await engine.DownWithinBudgetAsync(session, expired.Token).ConfigureAwait(true);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        down.Remaining.Unverified.ShouldBeTrue();
        down.Remaining.ProcessIds.ShouldContain(12345);
        down.Remaining.DiscoveryFailures.ShouldContain("cleanup-verification");
    }

    /// <summary>Expiry while waiting for provisioning preserves the pre-stop resource snapshot without another allowance.</summary>
    /// <returns>The finite pending-cleanup and exact identity assertions.</returns>
    [Fact]
    public async Task SharedStoppingExpiryPreservesTrackedContainerObservationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        string container = new('c', 64);
        await engine.StateStore.WriteAsync(state with { RemainingResources = new CompositionRunResources([container], []) }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream provisioning = new(Path.Combine(directory, "provisioning.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable provisioningLifetime = provisioning.ConfigureAwait(true);
        using Process appHost = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected };
        CompositionReadiness ready = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, []);
        CompositionRunSession session = new(plan, ready, "private-key", appHost);
        using CancellationTokenSource bound = new(TimeSpan.FromMilliseconds(200));
        Stopwatch elapsed = Stopwatch.StartNew();
        CompositionDownResult down = await engine.DownWithinBudgetAsync(session, bound.Token).ConfigureAwait(true);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(1));
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        down.Remaining.ContainerIds.ShouldContain(container);
        down.Remaining.Unverified.ShouldBeTrue();
    }

    /// <summary>
    /// A selected-test owner started by the real engine carries the process execution locality on its ready session, and
    /// that flag agrees with the execution context recorded for the owner, locally and under complete GitHub Actions metadata.
    /// </summary>
    /// <param name="ci">Whether the ambient process declares CI with complete GitHub Actions metadata.</param>
    /// <returns>The ready session locality and recorded owner context assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadySessionLocalityMatchesRecordedExecutionContextAsync(bool ci)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            AppHostAssemblyPath = appHost,
            AspireCommand = CompositionTestFiles.CreateAspire(workspace.Root),
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            StopTimeout = TimeSpan.FromSeconds(5),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });
        string[] names = ["CI", "GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_JOB", "RUNNER_OS", "RUNNER_ENVIRONMENT", "GITHUB_RUN_ATTEMPT"];
        string?[] prior = [.. names.Select(Environment.GetEnvironmentVariable)];
        CompositionStartResult result;
        CompositionRunState? recorded;
        try
        {
            string?[] values = ci ? ["true", "true", "owner/repository", "123", "tests", "Linux", "github-hosted", "1"] : new string?[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], values[i]);
            }

            result = await engine.StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
            recorded = result.Session is null ? null : await engine.StateStore.TryReadAsync(result.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            for (int i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], prior[i]);
            }
        }

        try
        {
            result.IsReady.ShouldBeTrue(string.Join(" | ", result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            CompositionRunSession session = result.Session.ShouldNotBeNull();
            session.Plan.SelectedTest.ShouldBe(selected);
            session.LocalExecution.ShouldBe(!ci);
            JsonElement context = recorded.ShouldNotBeNull().ExecutionContext.ShouldNotBeNull();
            string? kind = context.GetProperty("kind").GetString();
            kind.ShouldBe(ci ? "ci" : "local");
            (kind == "local").ShouldBe(session.LocalExecution);
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Exact down interrupts readiness and prelaunch for declared and ordinary owner invocations.</summary>
    /// <param name="declared">Whether this is a selected declared suite.</param>
    /// <param name="beforeLaunch">Whether cleanup claims the owner before AppHost launch.</param>
    /// <returns>The bounded cancellation, successful cleanup and no-resurrection assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentDownCancelsStartingOwnerAsync(bool declared, bool beforeLaunch)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, beforeLaunch ? "blocked-preflight" : "never", TestContext.Current.CancellationToken).ConfigureAwait(true);
        TaskCompletionSource<string> building = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            AppHostAssemblyPath = appHost,
            SelectedTest = declared ? selected : null,
            AspireCommand = CompositionTestFiles.CreateAspire(workspace.Root),
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            StopTimeout = TimeSpan.FromSeconds(5),
            PlatformModelBuildOverride = (_, runWorkspace, _) =>
            {
                building.SetResult(Path.GetFileName(runWorkspace));
                return Task.FromResult(appHost);
            },
        };
        CompositionEngine engine = new(options);
        using CancellationTokenSource testBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testBound.CancelAfter(TimeSpan.FromSeconds(30));
        Task<CompositionStartResult> starting = engine.StartAsync(manifest, testBound.Token);
        string runId = await building.Task.WaitAsync(testBound.Token).ConfigureAwait(true);
        string runtimeStarted = Path.Combine(Path.GetDirectoryName(appHost)!, "runtime-started");
        try
        {
            string startupCheckpoint = beforeLaunch ? Path.Combine(Path.GetDirectoryName(appHost)!, "preflight-started") : runtimeStarted;
            while (!File.Exists(startupCheckpoint))
            {
                await Task.Delay(10, testBound.Token).ConfigureAwait(true);
            }

            Task<CompositionDownResult> stopping = engine.DownAsync(runId, testBound.Token);
            while (!File.Exists(Path.Combine(options.StateDirectory, "diagnostics", runId, "cleanup-request.json")))
            {
                await Task.Delay(10, testBound.Token).ConfigureAwait(true);
            }

            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(appHost)!, "release-preflight"), "continue", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionStartResult started = await starting.WaitAsync(testBound.Token).ConfigureAwait(true);
            started.Result.Status.ShouldBe("cancelled", JsonSerializer.Serialize(started.Result));
            started.Result.Outcome.RuleId.ShouldBe("HXC130");
            started.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Cancelled);
            CompositionDownResult down = await stopping.WaitAsync(testBound.Token).ConfigureAwait(true);
            ModuleTestRunRecord? priorCancellation = null;
            if (down.Result.Outcome.RuleId == "HXR037")
            {
                declared.ShouldBeTrue();
                AssertRetryableMetadataRefusal(down);
                priorCancellation = (await engine.ReadCommittedOutcomeAsync(runId, testBound.Token, waitForLock: true).ConfigureAwait(true)).ShouldNotBeNull();
                priorCancellation.TerminalStatus.ShouldBe("cancelled");
                down = await engine.DownAsync(runId, testBound.Token).ConfigureAwait(true);
            }

            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(down));
            Directory.Exists(Path.Combine(options.WorkspaceRoot, runId)).ShouldBeFalse();
            File.Exists(engine.StateStore.PathFor(runId)).ShouldBeFalse();
            if (beforeLaunch)
            {
                File.Exists(runtimeStarted).ShouldBeFalse();
            }

            if (declared)
            {
                JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(options.StateDirectory, "diagnostics", runId, "test-result.json"), testBound.Token).Document!.Value;
                result.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("cancelled");
                result.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe("complete");
                if (priorCancellation is not null)
                {
                    ModuleTestRunRecord final = (await engine.ReadCommittedOutcomeAsync(runId, testBound.Token, waitForLock: true).ConfigureAwait(true)).ShouldNotBeNull();
                    AssertFirstOutcomeUnchanged(final, priorCancellation);
                }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(appHost)!, "release-preflight"), "continue", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await testBound.CancelAsync().ConfigureAwait(true);
            _ = await starting.ConfigureAwait(true);
            _ = await engine.DownAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Contended mandatory metadata refuses cleanup before stopping the owner and permits an explicit retry.</summary>
    /// <param name="useSession">Whether down receives the live session rather than its exact run identity.</param>
    /// <returns>The first refusal, live owner and immutable retry assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContendedDownPreservesOwnerUntilExplicitRetryAsync(bool useSession)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        CompositionEngine engine = await CreateReadyCleanupEngineAsync(workspace, selected, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using CancellationTokenSource testBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        testBound.CancelAfter(TimeSpan.FromSeconds(30));
        CompositionStartResult started = await engine.StartAsync(manifest, testBound.Token).ConfigureAwait(true);
        started.IsReady.ShouldBeTrue(JsonSerializer.Serialize(started.Result));
        CompositionRunSession session = started.Session.ShouldNotBeNull();
        using Process owned = Process.GetProcessById(session.AppHost.Id);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", session.RunId);
        try
        {
            ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(session.RunId, session.Readiness.Resources, Failure("HXT007"), "retained", new CompositionRunResources([], [owned.Id], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            first.TerminalStatus.ShouldBe("failed");
            FileStream held = new(Path.Combine(directory, "outcome.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            await using (held.ConfigureAwait(true))
            {
                CompositionDownResult refused = useSession
                    ? await engine.DownAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true)
                    : await engine.DownAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                AssertRetryableMetadataRefusal(refused);
                refused.Remaining.ProcessIds.ShouldContain(owned.Id);
                owned.Refresh();
                owned.HasExited.ShouldBeFalse();
                Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
                File.Exists(engine.StateStore.PathFor(session.RunId)).ShouldBeTrue();
                File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeFalse();
            }

            CompositionDownResult retried = useSession
                ? await engine.DownAsync(session, testBound.Token).ConfigureAwait(true)
                : await engine.DownAsync(session.RunId, testBound.Token).ConfigureAwait(true);
            retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(retried));
            owned.Refresh();
            owned.HasExited.ShouldBeTrue();
            Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
            File.Exists(engine.StateStore.PathFor(session.RunId)).ShouldBeFalse();
            ModuleTestRunRecord final = (await engine.ReadCommittedOutcomeAsync(session.RunId, testBound.Token, waitForLock: true).ConfigureAwait(true)).ShouldNotBeNull();
            AssertFirstOutcomeUnchanged(final, first);
            final.CleanupDisposition.ShouldBe("complete");
        }
        finally
        {
            _ = await engine.DownAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcess.Kill(owned);
        }
    }

    /// <summary>A startup result without a session never restores malformed committed terminal metadata.</summary>
    /// <param name="undefinedExit">Whether the record contains an undefined exit code rather than an unsupported status pairing.</param>
    /// <returns>The command refusal, untouched durable bytes and retained workspace assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SessionlessStartupRejectsMalformedCommittedOutcomeAsync(bool undefinedExit)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = (_, _, _) => Task.FromResult(new CompositionStartResult(Failure("HXR021"), state.RunId, null)),
        };
        CompositionEngine engine = new(options);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolOutcome malformed = undefinedExit ? new ToolOutcome((ToolExitCode)999, ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007") : ToolOutcome.Passed();
        ModuleTestRunRecord terminal = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", malformed, DateTimeOffset.UtcNow, "unknown", new CompositionRunResources([], []));
        string committed = Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "selected-test.json");
        await CompositionDocumentStore.WriteAsync(committed, terminal, TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] original = await File.ReadAllBytesAsync(committed, TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.ProductOrTest, output.ToString());
        using JsonDocument rendered = JsonDocument.Parse(output.ToString());
        rendered.RootElement.GetProperty("status").GetString().ShouldBe("failed");
        rendered.RootElement.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("ruleId").GetString()).ShouldContain("HXT008");
        (await File.ReadAllBytesAsync(committed, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(original);
        Directory.Exists(state.Workspace).ShouldBeTrue();
    }

    /// <summary>Sessionless recovery waits for brief finalization contention while preserving the exhausted resource-stopping allowance.</summary>
    /// <param name="unavailable">Whether a prerequisite was unavailable rather than the owner being cancelled.</param>
    /// <param name="contended">Whether another finalizer briefly holds the outcome lock.</param>
    /// <returns>The original outcome, cause, time and unchanged resource assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SessionlessStartupPreservesCommittedOutcomeAsync(bool unavailable, bool contended)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        int cleanups = 0;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = (_, _, _) => Task.FromResult(new CompositionStartResult(Failure("HXR021"), state.RunId, null) { StoppingTimestamp = Stopwatch.GetTimestamp() - (10 * Stopwatch.Frequency) }),
            PlatformDownOverride = (_, _) =>
            {
                cleanups++;
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], [])));
            },
        };
        CompositionEngine engine = new(options);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string status = unavailable ? "unavailable" : "cancelled";
        ToolExitCode exitCode = unavailable ? ToolExitCode.PrerequisiteUnavailable : ToolExitCode.Cancelled;
        ToolOutcome outcome = new(exitCode, ToolPhase.Test, unavailable ? ToolFailureCategory.PrerequisiteUnavailable : ToolFailureCategory.Cancelled, unavailable ? "HXR030" : "HXC130");
        string cause = unavailable ? "startup-failure" : "run-cancelled";
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], new ToolCommandResult(status, outcome, []), "remaining", new CompositionRunResources([], []) { WorkspaceIds = ["workspace:" + state.RunId] }, TestContext.Current.CancellationToken, terminalCause: cause, cleanupDisposition: "incomplete", cleanupAttemptId: CompositionRunPlanFactory.NewRunId()).ConfigureAwait(true)).ShouldNotBeNull();
        string directory = Path.Combine(options.StateDirectory, "diagnostics", state.RunId);
        string resultPath = Path.Combine(directory, "test-result.json");
        byte[] committed = await File.ReadAllBytesAsync(Path.Combine(directory, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] published = await File.ReadAllBytesAsync(resultPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        FileStream? finalization = contended ? new FileStream(Path.Combine(directory, "outcome.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable finalizationLifetime = (finalization ?? Stream.Null).ConfigureAwait(true);
        if (contended)
        {
            _ = await Should.ThrowAsync<IOException>(() => engine.ReadCommittedOutcomeAsync(state.RunId, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        }

        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        string manifest = workspace.Save();
        async Task FinishFinalizationAsync()
        {
            if (contended)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    await finalization!.DisposeAsync().ConfigureAwait(true);
                }
            }
        }

        Task finalizing = FinishFinalizationAsync();
        int actualExit;
        try
        {
            actualExit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        }
        finally
        {
            await finalizing.ConfigureAwait(true);
        }

        actualExit.ShouldBe((int)exitCode, output.ToString());
        using JsonDocument rendered = JsonDocument.Parse(output.ToString());
        rendered.RootElement.GetProperty("status").GetString().ShouldBe(status);
        rendered.RootElement.GetProperty("testResultPath").GetString().ShouldBe(resultPath);
        rendered.RootElement.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("ruleId").GetString()).ShouldNotContain("HXT008");
        JsonElement result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        result.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe(cause);
        result.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBe(first.TerminalAtUtc);
        (await File.ReadAllBytesAsync(Path.Combine(directory, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(committed);
        (await File.ReadAllBytesAsync(resultPath, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(published);
        cleanups.ShouldBe(0);
        Directory.Exists(state.Workspace).ShouldBeTrue();
    }

    /// <summary>A retained failure's known survivors are recorded after the resource-stopping allowance expires.</summary>
    /// <returns>The retained result, stable outcome and discoverable workspace assertions.</returns>
    [Fact]
    public async Task RetainedSurvivorsAreRecordedAfterStoppingBudgetExpiresAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
        };
        CompositionEngine engine = new(options);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failure("HXR021"), "retained", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken, terminalCause: "startup-timeout").ConfigureAwait(true)).ShouldNotBeNull();
        string optionalArchive = Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "selected-plan.json");
        const string archiveSentinel = "{\"observation\":\"prior-archive\"}";
        await File.WriteAllTextAsync(optionalArchive, archiveSentinel, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(state.Workspace, "plan.json"), "{\"observation\":\"late-optional-copy\"}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        options = options with
        {
            SelectedPlatformStartOverride = (_, _, _) => Task.FromResult(new CompositionStartResult(Failure("HXR021"), state.RunId, null) { StoppingTimestamp = Stopwatch.GetTimestamp() - (10 * Stopwatch.Frequency) }),
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.ProductOrTest);
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(options.StateDirectory, "diagnostics", state.RunId, "test-result.json"), TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBe(first.TerminalAtUtc);
        result.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("startup-timeout");
        await AssertRetainedWorkspaceAsync(options, state.RunId, result).ConfigureAwait(true);
        output.ToString().ShouldNotContain("HXT008");
        (await File.ReadAllTextAsync(optionalArchive, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(archiveSentinel);
    }

    /// <summary>The known execution-lifetime fallback remains a timeout before native activation can return.</summary>
    /// <returns>The timeout cause and failure-retained result assertions.</returns>
    [Fact]
    public async Task ExhaustedExecutionLifetimeBeforeNativeReturnRecordsTimeoutCauseAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        using Process appHost = new();
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = async (_, selection, _) =>
            {
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, new CompositionRunSession(plan, ready, "private-key", appHost));
            },
        };
        using CancellationTokenSource expired = new();
        await expired.CancelAsync().ConfigureAwait(true);
        using ModuleTestExecutionBudget budget = new(expired.Token);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        System.Reflection.MethodInfo execute = typeof(ModuleCommandExecutionService).GetMethod("ExecutePlatformAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).ShouldNotBeNull();
        Task<int> invocation = (Task<int>)execute.Invoke(null, [ModuleInvocationCommand.Test, manifest, "verify", null, null, selected.RequestedAtUtc, ToolOutputFormat.Json, output, "child.dll", null, options, WorkspaceMode.Source, options.SourceMapping, budget, null, null, null, null, TestContext.Current.CancellationToken])!;
        (await invocation.ConfigureAwait(true)).ShouldBe((int)ToolExitCode.ProductOrTest, output.ToString());
        using JsonDocument rendered = JsonDocument.Parse(output.ToString());
        rendered.RootElement.TryGetProperty("testResultPath", out JsonElement resultPath).ShouldBeTrue(output.ToString());
        JsonElement result = TestEnvironmentContractReader.ReadResult(resultPath.GetString()!, TestContext.Current.CancellationToken).Document!.Value;
        result.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("execution-timeout");
        result.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("failure-retained");
    }

    /// <summary>Builds the ready process used to observe cleanup before and after mandatory metadata becomes available.</summary>
    /// <param name="workspace">The isolated declaration and runtime workspace.</param>
    /// <param name="selected">The original immutable test selection.</param>
    /// <param name="cancellationToken">The fixture construction allowance.</param>
    /// <returns>The engine using the existing bounded process fixture.</returns>
    private static async Task<CompositionEngine> CreateReadyCleanupEngineAsync(PlatformManifestTestWorkspace workspace, ModuleTestSelection selected, CancellationToken cancellationToken)
    {
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", cancellationToken).ConfigureAwait(true);
        return new(LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            AppHostAssemblyPath = appHost,
            AspireCommand = CompositionTestFiles.CreateAspire(workspace.Root),
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            StopTimeout = TimeSpan.FromSeconds(5),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });
    }

    /// <summary>Verifies the documented refusal when mandatory cleanup metadata is unavailable.</summary>
    /// <param name="refused">The first explicit cleanup command result.</param>
    private static void AssertRetryableMetadataRefusal(CompositionDownResult refused)
    {
        refused.Result.Status.ShouldBe("failed");
        refused.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        refused.Result.Outcome.RuleId.ShouldBe("HXR037");
        refused.Result.Diagnostics.Count(diagnostic => diagnostic.RuleId == "HXT008").ShouldBe(1);
        refused.Result.Diagnostics.Single(diagnostic => diagnostic.RuleId == "HXR037").Hint.ShouldBe("Retry down for the same owner run identity.");
    }

    /// <summary>Verifies an explicit cleanup retry preserves the original terminal observation and expiry.</summary>
    /// <param name="current">The observation after cleanup.</param>
    /// <param name="original">The first committed observation.</param>
    private static void AssertFirstOutcomeUnchanged(ModuleTestRunRecord current, ModuleTestRunRecord original)
    {
        current.TerminalStatus.ShouldBe(original.TerminalStatus);
        current.TerminalAtUtc.ShouldBe(original.TerminalAtUtc);
        current.TerminalOutcome.ShouldBe(original.TerminalOutcome);
        current.Selection.ExecutionDeadlineUtc.ShouldBe(original.Selection.ExecutionDeadlineUtc);
        current.Selection.CredentialExpiryUtc.ShouldBe(original.Selection.CredentialExpiryUtc);
        (current.EnvironmentBindings?.GetRawText()).ShouldBe(original.EnvironmentBindings?.GetRawText());
    }

    private static async Task AssertRetainedWorkspaceAsync(CompositionEngineOptions options, string runId, JsonElement result)
    {
        string expected = "workspace:" + runId;
        result.GetProperty("retention").GetProperty("retainedResourceIds").EnumerateArray().Select(id => id.GetString()).ShouldContain(expected);
        result.GetProperty("cleanup").GetProperty("leftoverResourceIds").EnumerateArray().Select(id => id.GetString()).ShouldContain(expected);
        ToolCommandResult listing = await CompositionRunListing.ReadAsync(new CompositionRunStateStore(options.StateDirectory), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listing.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        listing.Environments.ShouldNotBeNull().Single(entry => entry.OwnerRunId == runId).RemainingResources.ShouldNotBeNull().WorkspaceIds.ShouldContain(expected);
    }

    private static ToolCommandResult Failure(string rule) => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, rule, ToolExitCode.ProductOrTest), []);
}