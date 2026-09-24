// <copyright file="CompositionEngineTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the composition engine fails closed before any resource starts and tears down idempotently.
/// </summary>
public sealed class CompositionEngineTests
{
    /// <summary>
    /// Verifies unavailable prerequisites return <c>HXR01x</c> with exit code 2 and create no run state or workspace.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingPrerequisitesStopBeforeAnyResourceAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionEngine engine = new(CreateOptions(root) with
            {
                DaprHome = Path.Combine(root, "missing-dapr-home"),
                DockerCommand = Path.Combine(root, "missing-docker"),
            });

            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsReady.ShouldBeFalse();
            result.Session.ShouldBeNull();
            result.RunId.ShouldBeNull();
            result.Result.Status.ShouldBe("unavailable");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
            result.Result.Outcome.RuleId.ShouldBe("HXR010");
            result.Result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR010", "HXR011"]);
            Directory.Exists(Path.Combine(root, "state")).ShouldBeFalse();
            Directory.Exists(Path.Combine(root, "workspaces")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies a missing AppHost build is an unavailable prerequisite reported before any resource starts.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingAppHostIsUnavailableBeforeAnyResourceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionEngine engine = new(CreateOptions(root) with
            {
                DaprHome = CompositionTestFiles.CreateDaprHome(root, "1.18.0", "1.18.2"),
                DockerCommand = CompositionTestFiles.CreateDocker(root),
            });

            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
            result.Result.Diagnostics.Single().RuleId.ShouldBe("HXR014");
            Directory.Exists(Path.Combine(root, "state")).ShouldBeFalse();
            Directory.Exists(Path.Combine(root, "workspaces")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies an invalid manifest returns the manifest exit code before prerequisites are probed.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task InvalidManifestFailsBeforePrerequisitesAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionEngine engine = new(CreateOptions(root) with { DockerCommand = Path.Combine(root, "missing-docker") });

            CompositionStartResult result = await engine.StartAsync(
                Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "negative", "unknown-schema.json"),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
            result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.RuleId.StartsWith("HXR01", StringComparison.Ordinal));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies a pre-cancelled start returns the cancellation exit code without allocating a run.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task CancelledStartReturnsCancellationAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            using CancellationTokenSource cancelled = new();
            await cancelled.CancelAsync().ConfigureAwait(true);
            CompositionEngine engine = new(CreateOptions(root));

            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), cancelled.Token).ConfigureAwait(true);

            result.Result.Status.ShouldBe("cancelled");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Cancelled);
            result.Result.Outcome.RuleId.ShouldBe("HXC130");
            result.RunId.ShouldBeNull();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies <c>down</c> is keyed by run identity, rejects malformed identities, and is idempotent for unknown runs.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownIsKeyedByRunIdAndIdempotentAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string docker = Path.Combine(root, "docker");
            CompositionTestFiles.WriteScript(docker, "exit 0");
            CompositionEngine engine = new(CreateOptions(root) with { DockerCommand = docker });
            const string otherRunId = "fedcba9876543210fedcba9876543210";
            await engine.StateStore.WriteAsync(
                new CompositionRunState(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Failed, "A", "w", null, null, "HXR021", DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await engine.StateStore.WriteAsync(
                new CompositionRunState(CompositionRunState.SupportedSchema, otherRunId, CompositionRunStatus.Ready, "B", "w", null, null, null, DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            string workspace = Path.Combine(root, "workspaces", CompositionTestFiles.RunId);
            _ = Directory.CreateDirectory(workspace);

            CompositionDownResult first = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionDownResult second = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionDownResult malformed = await engine.DownAsync("../../escape", TestContext.Current.CancellationToken).ConfigureAwait(true);

            first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            first.Remaining.IsEmpty.ShouldBeTrue();
            second.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            Directory.Exists(workspace).ShouldBeFalse();
            File.Exists(engine.StateStore.PathFor(CompositionTestFiles.RunId)).ShouldBeFalse();
            File.Exists(engine.StateStore.PathFor(otherRunId)).ShouldBeTrue();
            malformed.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
            malformed.Result.Diagnostics.Single().RuleId.ShouldBe("HXR024");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies a blocked AppHost standard-input listener is surfaced as a specific failed down result.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownReportsBlockedStdinListenerAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string docker = Path.Combine(root, "docker");
            CompositionTestFiles.WriteScript(docker, "exit 0");
            CompositionEngine engine = new(CreateOptions(root) with { DockerCommand = docker });
            string workspace = Path.Combine(root, "workspaces", CompositionTestFiles.RunId);
            _ = Directory.CreateDirectory(workspace);
            await CompositionDocumentStore.WriteAsync(
                CompositionStartupFailure.PathFor(workspace),
                new CompositionStartupFailure(CompositionStartupFailure.SupportedSchema, CompositionTestFiles.RunId, "HXR028", "stdin-listener", "-", "blocked-after-stop"),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionDownResult result = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            result.Result.Diagnostics.Single().RuleId.ShouldBe("HXR028");
            result.Result.Diagnostics.Single().Message.ShouldContain("stdin-listener");
            result.Remaining.IsEmpty.ShouldBeTrue();
            Directory.Exists(workspace).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies public <c>run</c> and <c>test</c> reach the live prerequisite probe for the executable fixture.
    /// </summary>
    /// <param name="command">The public command.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("run")]
    [InlineData("test")]
    public async Task PublicCommandChecksLivePrerequisitesForExecutableFixtureAsync(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
            CompositionEngineOptions options = new(Path.Combine(root, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
            {
                DockerCommand = CompositionTestFiles.CreateStubDocker(root),
                DaprHome = Path.Combine(root, "missing-dapr"),
                StateDirectory = Path.Combine(root, "state"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
            };
            string[] arguments = command == "test"
                ? [command, "--manifest", manifest, "--profile", "live", "--output", "json"]
                : [command, "--manifest", manifest, "--output", "json"];
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        arguments,
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken,
                        typeof(ModuleCommandApplication).Assembly.Location,
                        options).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable, standardOutput.ToString());
                    standardOutput.ToString().ShouldContain("\"ruleId\":\"HXR011\"");
                    standardOutput.ToString().ShouldNotContain("HXR003");
                    standardOutput.ToString().ShouldNotContain("passed");
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies the readiness outcomes of a started AppHost: early exit (<c>HXR020</c>), readiness timeout
    /// (<c>HXR021</c>), and readiness for another run (<c>HXR022</c>) each return exit code 3, run bounded teardown,
    /// and retain a metadata-only <c>Failed</c> state.
    /// </summary>
    /// <param name="mode">The fake AppHost behavior.</param>
    /// <param name="expectedRuleId">The expected stable rule identity.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("exit", "HXR020")]
    [InlineData("never", "HXR021")]
    [InlineData("foreign", "HXR022")]
    [InlineData("cause", "HXR027")]
    public async Task ReadinessFailuresTearDownAndRetainFailedStateAsync(string mode, string expectedRuleId)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngine engine = new(CreateOptions(root) with
            {
                AppHostAssemblyPath = appHost,
                DaprHome = CompositionTestFiles.CreateDaprHome(root, "1.18.0", "1.18.2"),
                DockerCommand = CompositionTestFiles.CreateStubDocker(root),
                ReadinessTimeout = TimeSpan.FromSeconds(mode == "never" ? 3 : 60),
            });

            Stopwatch elapsed = Stopwatch.StartNew();
            CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            elapsed.Stop();

            result.Session.ShouldBeNull();
            result.Result.Status.ShouldBe("failed");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            result.Result.Outcome.RuleId.ShouldBe(expectedRuleId);
            result.Result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe([expectedRuleId]);
            if (mode == "cause")
            {
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
                result.Result.Diagnostics.Single().Message.ShouldContain("cutover-activation-HTTP-403");
            }

            _ = result.RunId.ShouldNotBeNull();
            Directory.Exists(Path.Combine(root, "workspaces", result.RunId)).ShouldBeFalse();
            CompositionRunState? state = await engine.StateStore.TryReadAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            state.Status.ShouldBe(CompositionRunStatus.Failed);
            state.RuleId.ShouldBe(expectedRuleId);
            state.AppHostProcessId.ShouldBeNull();
            (await engine.Scanner.FindAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies <c>down</c> never signals a process whose identity was reused (different start time) and does stop
    /// the recorded process when its start time matches.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownSignalsOnlyTheRecordedProcessInstanceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The process-signal check uses POSIX sleep and SIGTERM.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        using Process sleeper = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        try
        {
            CompositionEngine engine = new(CreateOptions(root) with { DockerCommand = CompositionTestFiles.CreateStubDocker(root) });
            DateTimeOffset started = new(sleeper.StartTime.ToUniversalTime(), TimeSpan.Zero);

            await WriteRecordedStateAsync(engine, sleeper.Id, started.AddHours(1)).ConfigureAwait(true);
            _ = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            sleeper.HasExited.ShouldBeFalse();

            await WriteRecordedStateAsync(engine, sleeper.Id, started).ConfigureAwait(true);
            CompositionDownResult down = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);

            using CancellationTokenSource exit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            exit.CancelAfter(TimeSpan.FromSeconds(10));
            await sleeper.WaitForExitAsync(exit.Token).ConfigureAwait(true);
            sleeper.HasExited.ShouldBeTrue();
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        }
        finally
        {
            if (!sleeper.HasExited)
            {
                sleeper.Kill();
            }

            CompositionTestFiles.Delete(root);
        }
    }

    private static Task WriteRecordedStateAsync(CompositionEngine engine, int processId, DateTimeOffset startedAt) =>
        engine.StateStore.WriteAsync(
            new CompositionRunState(
                CompositionRunState.SupportedSchema,
                CompositionTestFiles.RunId,
                CompositionRunStatus.Ready,
                "A",
                "w",
                processId,
                startedAt,
                null,
                DateTimeOffset.UnixEpoch),
            TestContext.Current.CancellationToken);

    private static string ExecutableManifest() =>
        Path.Combine(RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");

    private static CompositionEngineOptions CreateOptions(string root) =>
        new(Path.Combine(root, "missing", "Hexalith.Builds.Module.AppHost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            StateDirectory = Path.Combine(root, "state"),
            WorkspaceRoot = Path.Combine(root, "workspaces"),
            ReadinessTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(5),
        };

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