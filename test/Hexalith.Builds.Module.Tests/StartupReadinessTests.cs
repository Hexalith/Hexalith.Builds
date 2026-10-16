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

        CompositionEffectiveDeadline selected = CompositionStartupDeadlineResolver.Resolve([billing, orders, unbounded], TimeSpan.FromSeconds(600));

        selected.TimeoutSeconds.ShouldBe(1200);
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
    [Fact]
    public async Task ReadyRunRecordsDeadlineAndDebugProcessIdsAsync()
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
                DebugAttach = true,
            });

            CompositionStartResult result = await debugEngine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            try
            {
                string? failure = result.Result.Diagnostics.Count == 0 ? null : result.Result.Diagnostics[0].Message;
                result.IsReady.ShouldBeTrue(failure);
                CompositionRunState? state = await debugEngine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = state.ShouldNotBeNull();
                state.Status.ShouldBe(CompositionRunStatus.Ready);
                state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThan(0);
                state.EffectiveDeadlineSeconds.ShouldBe(600);
                state.EffectiveDeadlineIsOverride.ShouldBeFalse();
                state.ReadinessResults.ShouldContain(resource => resource.Name == "eventstore" && resource.State == "Healthy");
                state.ProjectProcessIds.ShouldBe([4242]);
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("process id '4242'", StringComparison.Ordinal));
            }
            finally
            {
                if (result.Session is not null)
                {
                    _ = await debugEngine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
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
    /// <returns>The task-failure assertion.</returns>
    [Fact]
    public async Task StartupTaskFailureNamesTheTaskAndPreservesDiagnosticsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake AppHost toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "fail.sh"), "exit 1");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"fail.sh\"");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(60),
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
        _ = state.ShouldNotBeNull();
        _ = state.DiagnosticsPath.ShouldNotBeNull();
        File.Exists(Path.Combine(state.DiagnosticsPath, "summary.json")).ShouldBeTrue();
        using JsonDocument summary = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(state.DiagnosticsPath, "summary.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        summary.RootElement.GetProperty("failingTask").GetString().ShouldBe("verify");
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
                "[ \"$#\" -eq 1 ] && [ \"$1\" = '--version' ] || exit 64\nsleep 2\necho '" + CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0] + "+test-build'");
            CompositionEngine engine = new(Options(root, Path.Combine(root, "missing.dll")) with
            {
                AspireCommand = aspire,
                AspireProbeTimeout = TimeSpan.FromSeconds(5),
                ReadinessTimeout = TimeSpan.FromSeconds(1),
                DockerCommand = Path.Combine(root, "missing-docker"),
                DaprHome = Path.Combine(root, "missing-dapr"),
            });

            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

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
                new CompositionRunState(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Ready, "A", "w", null, null, null, DateTimeOffset.UnixEpoch),
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
        new(WorkspaceMode.Source, root, "Hexalith.Platform", [new SourceMappingEntry("Hexalith.Platform", "source", root)], false);

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
