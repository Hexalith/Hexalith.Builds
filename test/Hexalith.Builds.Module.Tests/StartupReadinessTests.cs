// <copyright file="StartupReadinessTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies readiness-aware startup, debug attach, and preserved diagnostics.
/// </summary>
public sealed class StartupReadinessTests
{
    /// <summary>Verifies the schema default is 600 seconds and is not labelled an override.</summary>
    [Fact]
    public void DefaultDeadlineIsSixHundredSeconds()
    {
        CompositionEngineOptions options = new("apphost.dll", "runner.dll");
        options.ReadinessTimeout.ShouldBe(TimeSpan.FromSeconds(SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds));
        CompositionEffectiveDeadline deadline = CompositionStartupDeadlineResolver.Resolve(null, options.ReadinessTimeout);
        deadline.TimeoutSeconds.ShouldBe(600);
        deadline.IsOverride.ShouldBeFalse();
        deadline.ModuleId.ShouldBeNull();
        deadline.Justification.ShouldBeNull();
    }

    /// <summary>Verifies the largest finite override wins and non-finite values are ignored.</summary>
    [Fact]
    public void LargestFiniteOverrideWins()
    {
        PlatformModuleDeclaration billing = Declaration("""
            {"identity":{"moduleId":"billing"},"lifecycle":{"startup":{"override":{"timeoutSeconds":900,"justification":"billing warmup"}}}}
            """);
        PlatformModuleDeclaration orders = Declaration("""
            {"identity":{"moduleId":"orders"},"lifecycle":{"startup":{"override":{"timeoutSeconds":1200,"justification":"orders replay"}}}}
            """);
        PlatformModuleDeclaration unbounded = Declaration("""
            {"identity":{"moduleId":"events"},"lifecycle":{"startup":{"override":{"timeoutSeconds":1e309,"justification":"unbounded"}}}}
            """);
        PlatformModuleDeclaration unrepresentable = Declaration("""
            {"identity":{"moduleId":"huge"},"lifecycle":{"startup":{"override":{"timeoutSeconds":1e308,"justification":"overflow"}}}}
            """);

        CompositionEffectiveDeadline selected = CompositionStartupDeadlineResolver.Resolve([billing, orders, unbounded, unrepresentable], TimeSpan.FromSeconds(600));

        selected.TimeoutSeconds.ShouldBe(1200);
        selected.Timeout.ShouldBe(TimeSpan.FromSeconds(1200));
        selected.IsOverride.ShouldBeTrue();
        selected.ModuleId.ShouldBe("orders");
        selected.Justification.ShouldBe("orders replay");
        CompositionStartupClock.Remaining(null, DateTimeOffset.UtcNow).ShouldBe(TimeSpan.FromSeconds(600));
        CompositionStartupClock.Remaining(DateTimeOffset.UtcNow.AddSeconds(-5), DateTimeOffset.UtcNow).ShouldBe(TimeSpan.Zero);
        CompositionLaunchConfiguration.ProjectConfiguration(WorkspaceMode.Source, false).ShouldBe("Debug");
        CompositionLaunchConfiguration.ProjectConfiguration(WorkspaceMode.Package, false).ShouldBe("Release");
        CompositionLaunchConfiguration.ProjectConfiguration(WorkspaceMode.Package, true).ShouldBe("Debug");
    }

    /// <summary>Verifies <c>debug</c> is a public command and package mode is refused.</summary>
    /// <returns>The command assertion.</returns>
    [Fact]
    public async Task DebugIsRegisteredAndRefusesPackageModeAsync()
    {
        StringWriter debugOutput = new();
        await using (debugOutput.ConfigureAwait(true))
        {
            int debugExit = await ModuleCommandApplication.InvokeAsync(
                ["debug", "--manifest", "missing.json", "--output", "json"],
                debugOutput,
                TextWriter.Null,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            debugExit.ShouldNotBe((int)ToolExitCode.Success);
            debugOutput.ToString().ShouldNotContain("HXC001");
        }

        StringWriter packageOutput = new();
        await using (packageOutput.ConfigureAwait(true))
        {
            int packageExit = await ModuleCommandApplication.InvokeAsync(
                ["debug", "--manifest", "missing.json", "--mode", "package", "--output", "json"],
                packageOutput,
                TextWriter.Null,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            packageExit.ShouldBe((int)ToolExitCode.UsageOrManifest);
            packageOutput.ToString().ShouldContain("HXC001");
        }
    }

    /// <summary>Verifies a ready run records duration, results and the default deadline, and debug reports process ids.</summary>
    /// <returns>The ready-run assertion.</returns>
    /// <param name="debugAttach">Whether launcher ids are requested.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadyRunRecordsDeadlineAndDebugProcessIdsAsync(bool debugAttach)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine debugEngine = new(Options(root, appHost) with
            {
                ReadinessTimeout = TimeSpan.FromSeconds(600),
                DebugAttach = debugAttach,
            });

            CompositionStartResult result = await debugEngine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            try
            {
                string? failure = result.Result.Diagnostics.Count == 0 ? null : result.Result.Diagnostics[0].Message;
                result.IsReady.ShouldBeTrue(failure);
                debugEngine.StateStore.Claims.ClaimedPorts().IsSupersetOf(result.Session!.Plan.Ports.ToList()).ShouldBeTrue();
                CompositionRunState? state = await debugEngine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = state.ShouldNotBeNull();
                state.Status.ShouldBe(CompositionRunStatus.Ready);
                state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
                state.EffectiveDeadlineSeconds.ShouldBe(600);
                state.EffectiveDeadlineIsOverride.ShouldBeFalse();
                state.ReadinessResults.ShouldContain(resource => resource.Name == "eventstore" && resource.State == "Healthy");
                state.ProjectProcessIds.ShouldBe(debugAttach ? [4242] : []);
                if (debugAttach)
                {
                    result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("launcher process id '4242'", StringComparison.Ordinal));
                }
                else
                {
                    result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Message.Contains("launcher process id", StringComparison.Ordinal));
                }
            }
            finally
            {
                if (result.Session is not null)
                {
                    _ = await debugEngine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    debugEngine.StateStore.Claims.ClaimedPorts().ShouldBeEmpty();
                }
            }

            (await debugEngine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the engine uses a declared override instead of the shorter fallback.</summary>
    /// <returns>The override assertion.</returns>
    [Fact]
    public async Task DeclaredOverrideReplacesFallbackDeadlineAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.scope", "\"recovery\"");
        workspace.Change("modules.0.lifecycle.startup", "{\"defaultTimeoutSeconds\":600,\"override\":{\"timeoutSeconds\":900,\"justification\":\"needs more time\"}}");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(30),
            SourceMapping = Mapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            string? failure = result.Result.Diagnostics.Count == 0 ? null : result.Result.Diagnostics[0].Message;
            result.IsReady.ShouldBeTrue(failure);
            CompositionRunState? state = await engine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            state.EffectiveDeadlineSeconds.ShouldBe(900);
            state.EffectiveDeadlineIsOverride.ShouldBeTrue();
            state.EffectiveDeadlineModuleId.ShouldBe("sample");
            state.EffectiveDeadlineJustification.ShouldBe("needs more time");
            state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies a deadline timeout lists every unready resource and keeps diagnostics after teardown.</summary>
    /// <returns>The timeout assertion.</returns>
    [Fact]
    public async Task TimeoutListsEveryUnreadyResourceAndPreservesDiagnosticsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "unready", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine engine = new(Options(root, appHost) with { ReadinessTimeout = TimeSpan.FromSeconds(8) });
            CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Session.ShouldBeNull();
            result.Result.Outcome.RuleId.ShouldBe("HXR021");
            result.Result.Diagnostics.Select(diagnostic => diagnostic.Field).ShouldBe(["orders", "billing"], ignoreOrder: true);
            string output = string.Join('\n', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message));
            output.ShouldContain("HTTP 503");
            output.ShouldContain("connection-unavailable");
            output.ShouldContain("diagnostics='");
            _ = result.RunId.ShouldNotBeNull();
            Directory.Exists(Path.Combine(root, "workspaces", result.RunId)).ShouldBeFalse();
            CompositionRunState? state = await engine.StateStore.TryReadAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            _ = state.DiagnosticsPath.ShouldNotBeNull();
            string observed = await File.ReadAllTextAsync(Path.Combine(state.DiagnosticsPath, "observed.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observed.ShouldBe("apphost.stdout.observed" + Environment.NewLine);
            observed.ShouldNotContain("HTTP 503");
            Directory.Exists(state.DiagnosticsPath).ShouldBeTrue();
            File.Exists(Path.Combine(state.DiagnosticsPath, "summary.json")).ShouldBeTrue();
            state.EffectiveDeadlineSeconds.ShouldBe(8);
            state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a startup task that fails before the deadline is named and diagnostics survive teardown.</summary>
    /// <param name="selected">Whether a declared suite requires a durable v1 terminal result.</param>
    /// <returns>The task-failure assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupTaskFailureNamesTheTaskAndPreservesDiagnosticsAsync(bool selected)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "fail.sh"), "exit 1");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"fail.sh\"");
        ModuleTestSelection? suite = null;
        if (selected)
        {
            workspace.Change("modules.0.tests", "[{\"name\":\"verify\",\"executor\":\"python3\",\"entry\":\"runner.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":30,\"stopTimeoutSeconds\":5,\"mode\":\"source\"}]");
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "raise AssertionError('No test child may run')", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifest = workspace.Save();
            suite = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow);
        }

        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(selected ? 600 : 60),
            SelectedTest = suite,
            SourceMapping = Mapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Session.ShouldBeNull();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("verify", StringComparison.Ordinal));
        _ = result.RunId.ShouldNotBeNull();
        Directory.Exists(Path.Combine(workspace.Root, "workspaces", result.RunId)).ShouldBeFalse();
        CompositionRunState? state = await engine.StateStore.TryReadAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string diagnosticsPath = Path.Combine(engine.StateStore.Directory, "diagnostics", result.RunId);
        if (selected)
        {
            state.ShouldBeNull();
        }
        else
        {
            state.ShouldNotBeNull().DiagnosticsPath.ShouldBe(diagnosticsPath);
        }

        File.Exists(Path.Combine(diagnosticsPath, "summary.json")).ShouldBeTrue();
        using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnosticsPath, "summary.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        summary.RootElement.GetProperty("effectiveDeadlineSeconds").GetDouble().ShouldBe(selected ? 600 : 60);
        summary.RootElement.GetProperty("durationSeconds").GetDouble().ShouldBeGreaterThan(0);
        summary.RootElement.GetProperty("failingTask").GetString().ShouldBe("sample/verify");
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("failed before", StringComparison.Ordinal));
        result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Message.Contains("deadline elapsed", StringComparison.Ordinal));
        if (selected)
        {
            JsonElement terminal = CleanedStartupResult(diagnosticsPath, result.RunId, suite!, "startup-failure", 600);
            terminal.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBeLessThan(suite!.RequestedAtUtc.AddSeconds(600));
        }
    }

    /// <summary>Verifies a per-start task becomes ready only when its arguments are supplied.</summary>
    /// <returns>The zero-exit assertion.</returns>
    [Fact]
    public async Task StartupTaskReadyRequiresItsArgumentsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "check.sh"), "[ \"$1\" = \"expected\" ] || exit 1\nexit 0\n");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"check.sh\"");
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", "[\"expected\"]");
        workspace.Change("modules.0.tests", "[{\"name\":\"verify\",\"executor\":\"python3\",\"entry\":\"runner.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":120,\"stopTimeoutSeconds\":5,\"mode\":\"source\"}]");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "# suite identity fixture", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        SourceMapping mapping = Mapping(workspace.Root);
        IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [manifest]) { ActiveModule = selected.ModuleId });
        string expectedIdentity = Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentDescriptorWriter.CompositionIdentity(closure, selected);
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SelectedTest = selected,
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            SourceMapping = mapping,
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        CompositionStartResult result = await engine.StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            string? failure = result.Result.Diagnostics.Count == 0 ? null : result.Result.Diagnostics[0].Message;
            result.IsReady.ShouldBeTrue(failure);
            result.Session!.Plan.SelectedTest.ShouldBe(selected);
            result.Session.Plan.CompositionContentIdentity.ShouldBe(expectedIdentity);
            CompositionRunPlan persisted = (await CompositionDocumentStore.TryReadAsync<CompositionRunPlan>(Path.Combine(result.Session.Plan.Workspace, "plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            persisted.CompositionContentIdentity.ShouldBe(expectedIdentity);
            CompositionStartupTaskResult task = result.Session.Readiness.StartupTaskResults.ShouldHaveSingleItem();
            task.TaskId.ShouldBe("sample/verify");
            task.Status.ShouldBe("passed");
            _ = task.StartedAt.ShouldNotBeNull();
            task.FinishedAt.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(task.StartedAt.Value);
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies a startup task that outlives the deadline reports expiry and does not run to completion.</summary>
    /// <param name="selected">Whether the test command supplies an externally bounded startup token.</param>
    /// <returns>The bounded-task assertion.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupTaskIsStoppedAtTheDeadlineAsync(bool selected)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "slow.sh"), "touch task-started\nsleep 60\n");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"slow.sh\"");
        ModuleTestSelection? suite = null;
        if (selected)
        {
            workspace.Change("modules.0.lifecycle.startup", "{\"override\":{\"timeoutSeconds\":15,\"justification\":\"Finite startup task deadline regression\"}}");
            workspace.Change("modules.0.tests", "[{\"name\":\"verify\",\"executor\":\"python3\",\"entry\":\"runner.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":30,\"stopTimeoutSeconds\":5,\"mode\":\"source\"}]");
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "raise AssertionError('No test child may run')", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifest = workspace.Save();
            suite = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow);
        }

        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(15),
            SelectedTest = suite,
            SelectedExecutionOwnerToken = selected ? TestContext.Current.CancellationToken : null,
            SourceMapping = Mapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), selected ? startup.Token : TestContext.Current.CancellationToken).ConfigureAwait(true);
        stopwatch.Stop();

        File.Exists(Path.Combine(workspace.Root, "task-started")).ShouldBeTrue("The fixture must reach the task phase before testing its deadline diagnostic. " + string.Join(" | ", result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        result.Session.ShouldBeNull();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("within the readiness bound", StringComparison.Ordinal));
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("startup task '", StringComparison.Ordinal) && diagnostic.Message.Contains("had not finished", StringComparison.Ordinal), string.Join(" | ", result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Message.Contains("failed before", StringComparison.Ordinal));
        string runId = result.RunId.ShouldNotBeNull();
        CompositionRunState? state = await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string diagnosticsPath = Path.Combine(engine.StateStore.Directory, "diagnostics", runId);
        if (selected)
        {
            state.ShouldBeNull();
        }
        else
        {
            state.ShouldNotBeNull().DiagnosticsPath.ShouldBe(diagnosticsPath);
        }

        using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnosticsPath, "summary.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        summary.RootElement.GetProperty("effectiveDeadlineSeconds").GetDouble().ShouldBe(15);
        summary.RootElement.GetProperty("effectiveDeadlineIsOverride").GetBoolean().ShouldBe(selected);
        summary.RootElement.GetProperty("durationSeconds").GetDouble().ShouldBeGreaterThan(0);
        Directory.Exists(Path.Combine(workspace.Root, "workspaces", runId)).ShouldBeFalse();
        summary.RootElement.GetProperty("failingTask").ValueKind.ShouldBe(JsonValueKind.Null);
        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        if (selected)
        {
            JsonElement terminal = CleanedStartupResult(diagnosticsPath, runId, suite!, "startup-timeout", 15, declaredOverride: true);
            terminal.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBeGreaterThanOrEqualTo(suite!.RequestedAtUtc.AddSeconds(15));
        }
    }

    /// <summary>Verifies the task's local deadline returns expiry rather than a nonzero-exit failure.</summary>
    /// <returns>The direct local-deadline assertions.</returns>
    [Fact]
    public async Task RunningTaskDeadlineReturnsElapsedWithoutFailureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The startup task double uses a POSIX script.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string executable = Path.Combine(workspace.Root, "slow.sh");
        string started = Path.Combine(workspace.Root, "task-started");
        CompositionTestFiles.WriteScript(executable, "touch task-started\nsleep 60\n");
        (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync(
            [new CompositionStartupTask("sample", "verify", executable, [])],
            DateTimeOffset.UtcNow.AddMilliseconds(500),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(started).ShouldBeTrue();
        failingTask.ShouldBeNull();
        deadlineElapsed.ShouldBeTrue();
    }

    /// <summary>Verifies a deadline that elapsed before a task starts is not reported as that task.</summary>
    /// <returns>The deadline assertion.</returns>
    [Fact]
    public async Task DeadlineBeforeTaskStartIsNotNamedAsync()
    {
        (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync(
            [new CompositionStartupTask("sample", "verify", "/missing", [])],
            DateTimeOffset.UtcNow.AddSeconds(-1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        failingTask.ShouldBeNull();
        deadlineElapsed.ShouldBeTrue();
    }

    /// <summary>Verifies caller cancellation is not stored as a failed startup task.</summary>
    /// <returns>The cancellation assertion.</returns>
    [Fact]
    public async Task CallerCancellationIsNotAFailedStartupTaskAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The startup task double uses a POSIX script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string script = Path.Combine(root, "sleep.sh");
            CompositionTestFiles.WriteScript(script, "sleep 30\n");
            using CancellationTokenSource source = new();
            Task<(string? FailingTask, bool DeadlineElapsed)> running = CompositionStartupTasks.RunAsync(
                [new CompositionStartupTask("sample", "verify", script, [])],
                DateTimeOffset.UtcNow.AddMinutes(1),
                source.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);
            await source.CancelAsync().ConfigureAwait(true);
            try
            {
                _ = await running.ConfigureAwait(true);
                Assert.Fail("Caller cancellation was reported as a task result.");
            }
            catch (OperationCanceledException)
            {
                // The caller token is rethrown after the process is killed.
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies time spent before the AppHost counts against the startup deadline.</summary>
    /// <returns>The clock assertion.</returns>
    [Fact]
    public async Task DeadlineStartsBeforePrerequisitesAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Aspire CLI uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string aspire = Path.Combine(root, "slow-aspire");
            CompositionTestFiles.WriteScript(
                aspire,
                "[ \"$#\" -eq 1 ] && [ \"$1\" = '--version' ] || exit 64\nsleep 20\necho '" + CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0] + "+test-build'");
            CompositionEngine engine = new(Options(root, Path.Combine(root, "missing.dll")) with
            {
                AspireCommand = aspire,
                AspireProbeTimeout = TimeSpan.FromSeconds(5),
                ReadinessTimeout = TimeSpan.FromSeconds(1),
                DockerCommand = Path.Combine(root, "missing-docker"),
                DaprHome = Path.Combine(root, "missing-dapr"),
            });

            Stopwatch elapsed = Stopwatch.StartNew();
            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
            result.Result.Outcome.RuleId.ShouldBe("HXR021");
            result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.RuleId == "HXR010");
            result.Result.Diagnostics.Single().Message.ShouldContain("diagnostics='");
            result.RunId.ShouldBeNull();
            Directory.Exists(Path.Combine(root, "workspaces")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a preserved stream mirror keeps tokens and drops the payload when no log path is set.</summary>
    /// <returns>The mirror assertion.</returns>
    [Fact]
    public async Task PreservedStreamMirrorKeepsOnlyTokensAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stream double uses a POSIX shell.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string preserved = Path.Combine(root, "preserved");
            _ = Directory.CreateDirectory(preserved);
            ProcessStartInfo start = new("/bin/sh")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("printf '%s\\n' 'secret-token-value'");
            using Process process = new() { StartInfo = start };
            TaskCompletionSource<bool> completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is null && !completed.TrySetResult(true))
                {
                    // The stream already completed.
                }
            };
            CompositionDiagnosticMirror.Attach(process, null, preserved);
            process.Start().ShouldBeTrue();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await completed.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            string retained = await File.ReadAllTextAsync(Path.Combine(preserved, "observed.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            retained.ShouldBe("apphost.stdout.observed" + Environment.NewLine);
            retained.ShouldNotContain("secret-token-value");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies public debug reaches a ready AppHost for a v1 manifest and a v2 manifest, then down finds the run.</summary>
    /// <param name="kind">The manifest kind.</param>
    /// <returns>The debug assertion.</returns>
    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task PublicDebugReachesReadyAndDownClearsStateAsync(string kind)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest;
            CompositionEngineOptions options;
            if (kind == "v1")
            {
                manifest = CompositionTestFiles.CopyExecutableManifest(ExecutableManifest(), root);
                string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
                options = Options(root, appHost);
            }
            else
            {
                File.Copy(Path.Combine(RepositoryRoot(), "Hexalith.Builds.slnx"), Path.Combine(root, "Hexalith.Builds.slnx"));
                await File.WriteAllTextAsync(Path.Combine(root, "README.md"), "not executed", TestContext.Current.CancellationToken).ConfigureAwait(true);
                string declaration = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "platform", "valid.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
                declaration = declaration.Replace("\"scope\": \"per-start-verify\"", "\"scope\": \"recovery\"", StringComparison.Ordinal);
                manifest = Path.Combine(root, "manifest.json");
                await File.WriteAllTextAsync(manifest, declaration, TestContext.Current.CancellationToken).ConfigureAwait(true);
                string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
                options = Options(root, appHost) with
                {
                    SourceMapping = Mapping(root),
                    PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
                };
            }

            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["debug", "--manifest", manifest, "--output", "json"],
                    output,
                    TextWriter.Null,
                    TestContext.Current.CancellationToken,
                    typeof(ModuleCommandApplication).Assembly.Location,
                    options).ConfigureAwait(true);
                exitCode.ShouldBe((int)ToolExitCode.Success, output.ToString());
                using JsonDocument document = JsonDocument.Parse(output.ToString());
                document.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic =>
                    diagnostic.GetProperty("message").GetString()!.Contains("process id '4242'", StringComparison.Ordinal));
                string runId = document.RootElement.GetProperty("runId").GetString()!;
                CompositionEngine engine = new(options);
                CompositionRunState? state = await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = state.ShouldNotBeNull();
                state.Status.ShouldBe(CompositionRunStatus.Ready);

                StringWriter downOutput = new();
                await using (downOutput.ConfigureAwait(true))
                {
                    int down = await ModuleCommandApplication.InvokeAsync(
                        ["down", "--manifest", manifest, "--run-id", runId, "--output", "json"],
                        downOutput,
                        TextWriter.Null,
                        TestContext.Current.CancellationToken,
                        typeof(ModuleCommandApplication).Assembly.Location,
                        options).ConfigureAwait(true);
                    down.ShouldBe((int)ToolExitCode.Success, downOutput.ToString());
                }

                (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies down stops only the tagged run and leaves an unknown run untouched.</summary>
    /// <returns>The teardown assertion.</returns>
    [Fact]
    public async Task DownStopsOnlyTaggedResourcesAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Process tagging uses the POSIX environment block.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        const string foreignRun = "fedcba9876543210fedcba9876543210";
        const string unknownRun = "00112233445566778899aabbccddeeff";
        using Process foreign = StartTagged(foreignRun);
        using Process untagged = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        using Process tagged = StartTagged(CompositionTestFiles.RunId);
        try
        {
            CompositionEngine engine = new(Options(root, Path.Combine(root, "missing.dll")) with
            {
                DockerCommand = CompositionTestFiles.CreateStubDocker(root),
            });
            await engine.StateStore.WriteAsync(
                new CompositionRunState(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Ready, "A", Path.Combine(root, "workspaces", CompositionTestFiles.RunId), null, null, null, DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionDownResult unknown = await engine.DownAsync(unknownRun, TestContext.Current.CancellationToken).ConfigureAwait(true);
            unknown.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            foreign.HasExited.ShouldBeFalse();
            untagged.HasExited.ShouldBeFalse();
            tagged.HasExited.ShouldBeFalse();

            CompositionDownResult down = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            tagged.HasExited.ShouldBeTrue();
            foreign.HasExited.ShouldBeFalse();
            untagged.HasExited.ShouldBeFalse();
        }
        finally
        {
            Kill(foreign);
            Kill(untagged);
            Kill(tagged);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies partial debug metadata names each project missing its launcher id.</summary>
    /// <param name="mode">The missing or partial metadata fixture.</param>
    /// <returns>The debug diagnostics assertions.</returns>
    [Theory]
    [InlineData("ready-no-pid")]
    [InlineData("ready-partial-pid")]
    public async Task DebugNamesProjectsWithoutLauncherIdsAsync(string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost uses POSIX tools.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine engine = new(Options(root, appHost) with { DebugAttach = true });
            CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            try
            {
                result.IsReady.ShouldBeTrue();
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "worker" && diagnostic.Message.Contains("launcher process id is unavailable", StringComparison.Ordinal));
                if (mode == "ready-partial-pid")
                {
                    result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "eventstore" && diagnostic.Message.Contains("launcher process id '4242'", StringComparison.Ordinal));
                }
            }
            finally
            {
                if (result.Session is not null)
                {
                    _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies model building and preflight are stopped by the original startup deadline.</summary>
    /// <param name="stage">The delayed startup stage.</param>
    /// <returns>The deadline assertions.</returns>
    [Theory]
    [InlineData("model")]
    [InlineData("preflight")]
    public async Task PlatformPreparationUsesRemainingDeadlineAsync(string stage)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost uses POSIX tools.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "slow-preflight", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = Mapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            PlatformModelBuildOverride = async (_, _, token) =>
            {
                if (stage == "model")
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                }

                return appHost;
            },
        });
        Stopwatch elapsed = Stopwatch.StartNew();
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        result.IsReady.ShouldBeFalse();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("diagnostics='", StringComparison.Ordinal));
        Directory.Exists(Path.Combine(workspace.Root, "workspaces", result.RunId!)).ShouldBeFalse();
    }

    /// <summary>Selected startup cancellation during model preparation preserves timeout diagnostics.</summary>
    /// <returns>The startup classification, summary and absence of a test child assertions.</returns>
    [Fact]
    public async Task SelectedStartupTokenExpiresDuringModelBuildAndPreservesSummaryAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost uses POSIX tools.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.tests", "[{\"name\":\"verify\",\"executor\":\"python3\",\"entry\":\"runner.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":120,\"stopTimeoutSeconds\":5,\"mode\":\"source\"}]");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "raise AssertionError('No test child may run')", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bool modelStarted = false;
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = Mapping(workspace.Root),
            SelectedTest = selected,
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            SelectedExecutionOwnerToken = TestContext.Current.CancellationToken,
            PlatformModelBuildOverride = async (_, _, token) =>
            {
                modelStarted = true;
                startup.CancelAfter(TimeSpan.FromMilliseconds(100));
                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                return appHost;
            },
        });
        Stopwatch elapsed = Stopwatch.StartNew();
        CompositionStartResult result = await engine.StartAsync(manifest, startup.Token).ConfigureAwait(true);
        modelStarted.ShouldBeTrue(string.Join(" | ", result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        startup.IsCancellationRequested.ShouldBeTrue();
        TestContext.Current.CancellationToken.IsCancellationRequested.ShouldBeFalse();
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        result.Session.ShouldBeNull();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.RuleId == "HXC130");
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("diagnostics='", StringComparison.Ordinal));
        string runId = result.RunId.ShouldNotBeNull();
        (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
        string diagnosticsPath = Path.Combine(engine.StateStore.Directory, "diagnostics", runId);
        using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(diagnosticsPath, "summary.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        summary.RootElement.GetProperty("durationSeconds").GetDouble().ShouldBeGreaterThan(0);
        summary.RootElement.GetProperty("effectiveDeadlineSeconds").GetDouble().ShouldBe(600);
        JsonElement terminal = CleanedStartupResult(diagnosticsPath, runId, selected, "startup-timeout", 600);
        terminal.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBeLessThan(selected.RequestedAtUtc.AddSeconds(600));
        Directory.Exists(Path.Combine(workspace.Root, "workspaces", runId)).ShouldBeFalse();
        engine.StateStore.Claims.ClaimedPorts().ShouldBeEmpty();
    }

    /// <summary>Verifies task selection retains executable validation causes if a path cannot be resolved.</summary>
    /// <param name="executable">The invalid executable declaration.</param>
    /// <param name="rule">The expected executable-path rule.</param>
    /// <returns>The selected task assertions.</returns>
    [Theory]
    [InlineData("tools/missing.sh", "HXM005")]
    [InlineData("../invalid.sh", "HXM004")]
    public async Task StartupTaskExecutableCauseIsPreservedAsync(string executable, string rule)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            PlatformModuleDeclaration declaration = Declaration(JsonSerializer.Serialize(new
            {
                identity = new { moduleId = "sample" },
                lifecycle = new { tasks = new[] { new { name = "verify", scope = "per-start-verify", executable } } },
            }));
            CompositionStartupTask selected = CompositionStartupTasks.Select(Path.Combine(root, "module.json"), [declaration]).Single();
            selected.ExecutableError.ShouldNotBeNull().ShouldContain(rule);
            (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync([selected], DateTimeOffset.UtcNow.AddSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
            failingTask.ShouldBe("sample/verify");
            deadlineElapsed.ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies v1 descriptor child execution shares the original startup deadline.</summary>
    /// <returns>The descriptor deadline assertions.</returns>
    [Fact]
    public async Task DescriptorLoadingUsesRemainingDeadlineAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost uses POSIX tools.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "slow-descriptor", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine engine = new(Options(root, appHost) with
            {
                DescriptorChildEntryAssemblyPath = appHost,
                ReadinessTimeout = TimeSpan.FromSeconds(2),
            });
            Stopwatch elapsed = Stopwatch.StartNew();
            CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
            result.Result.Outcome.RuleId.ShouldBe("HXR021");
            result.RunId.ShouldBeNull();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>The parent translates genuine child conflicts and refuses malformed blocking owners.</summary>
    /// <param name="validOwner">Whether the child reported an actual managed owner identity.</param>
    /// <returns>The startup refusal and claim cleanup assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ParentStartupTranslatesExclusiveClaimFailuresAsync(bool validOwner)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, validOwner ? "claim-conflict" : "claim-conflict-invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine engine = new(Options(root, appHost));
            CompositionStartResult started = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            started.Session.ShouldBeNull();
            started.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == (validOwner ? "HXR031" : "HXR022"));
            if (validOwner)
            {
                started.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains(new string('f', 32), StringComparison.Ordinal));
            }

            engine.StateStore.Claims.ClaimedPorts().ShouldBeEmpty();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static JsonElement CleanedStartupResult(string diagnosticsPath, string runId, ModuleTestSelection selected, string cause, int effectiveSeconds, bool declaredOverride = false)
    {
        TestEnvironmentContractReadResult terminal = TestEnvironmentContractReader.ReadResult(Path.Combine(diagnosticsPath, "test-result.json"), TestContext.Current.CancellationToken);
        terminal.IsValid.ShouldBeTrue(string.Join(",", terminal.Diagnostics.Select(diagnostic => diagnostic.RuleId + ":" + diagnostic.Field)));
        JsonElement document = terminal.Document!.Value;
        document.GetProperty("bindings").GetProperty("run").GetProperty("runId").GetString().ShouldBe(runId);
        document.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("failed");
        document.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe(cause);
        document.GetProperty("cleanup").GetProperty("disposition").GetString().ShouldBe("complete");
        document.GetProperty("cleanup").GetProperty("leftoverResourceIds").GetArrayLength().ShouldBe(0);
        JsonElement startup = document.GetProperty("bindings").GetProperty("deadlines").GetProperty("startup");
        startup.GetProperty("originAt").GetDateTimeOffset().ShouldBe(selected.RequestedAtUtc);
        startup.GetProperty("effectiveSeconds").GetInt32().ShouldBe(effectiveSeconds);
        startup.GetProperty("expiresAt").GetDateTimeOffset().ShouldBe(selected.RequestedAtUtc.AddSeconds(effectiveSeconds));
        startup.GetProperty("override").GetProperty("state").GetString().ShouldBe(declaredOverride ? "declared" : "not-overridden");
        document.GetProperty("bindings").GetProperty("deadlines").GetProperty("execution").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        return document;
    }

    private static PlatformModuleDeclaration Declaration(string json) =>
        new("fixture.json", JsonSerializer.Deserialize<JsonElement>(json));

    private static CompositionEngineOptions Options(string root, string appHost) =>
        new(appHost, typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = CompositionTestFiles.CreateAspire(root),
            DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(root),
            StateDirectory = Path.Combine(root, "state"),
            WorkspaceRoot = Path.Combine(root, "workspaces"),
            ReadinessTimeout = TimeSpan.FromSeconds(60),
            StopTimeout = TimeSpan.FromSeconds(5),
        };

    private static SourceMapping Mapping(string root) =>
        CompositionTestFiles.CreatePlatformMapping(root);

    private static Process StartTagged(string runId)
    {
        ProcessStartInfo start = new("sleep", "120") { UseShellExecute = false };
        start.Environment[CompositionEnvironment.RunId] = runId;
        return Process.Start(start)!;
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // The process already exited.
        }
    }

    private static string ExecutableManifest() =>
        Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");

    private static string PositiveManifest() =>
        Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "positive", "hexalith.module-manifest.v1.json");

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hexalith.Builds repository root.");
    }
}
