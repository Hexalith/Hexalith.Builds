// <copyright file="StartupCoordinationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies one startup clock, authoritative dependency tasks, and post-task readiness.</summary>
public sealed class StartupCoordinationTests
{
    /// <summary>Verifies a short active override cannot cancel discovery of the larger selected dependency override.</summary>
    /// <returns>The original-clock and live-token assertions.</returns>
    [Fact]
    public async Task DependencyDeadlineIsDiscoveredBeforeShortActiveOverrideIsArmedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.identity.moduleId", "\"mcpcli\"");
        workspace.Change("modules.0.identity.dependencies", "[\"memories\"]");
        workspace.Change("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":0.05,\"justification\":\"short active verification\"}");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        SetDependencyStartup(mapping, 15, null);
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        using CompositionStartupRequest request = new(requestedAt, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await request.ConfigureAsync(workspace.Save(), WorkspaceMode.Source, null, async (_, _, token) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200), token).ConfigureAwait(false);
            return mapping;
        }).ConfigureAwait(true);

        (DateTimeOffset.UtcNow - requestedAt).ShouldBeGreaterThan(TimeSpan.FromMilliseconds(50));
        request.RequestedAt.ShouldBe(requestedAt);
        request.Deadline.TimeoutSeconds.ShouldBe(15);
        request.Deadline.ModuleId.ShouldBe("memories");
        request.Deadline.IsOverride.ShouldBeTrue();
        request.Token.IsCancellationRequested.ShouldBeFalse();
        await Task.Delay(TimeSpan.FromMilliseconds(100), request.Token).ConfigureAwait(true);
    }

    /// <summary>Verifies expensive mapping uses the validated effective bound after metadata discovery.</summary>
    /// <returns>The selected-deadline cancellation assertions.</returns>
    [Fact]
    public async Task ShortSelectedDeadlineStillBoundsSourceMappingAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":0.5,\"justification\":\"bounded mapping\"}");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        bool mappingCancelled = false;
        CompositionEngineOptions options = Options(workspace.Root, "unused.dll") with
        {
            SourceMapping = mapping,
            RootManifestCheckOverride = (_, _) => Task.FromResult(true),
            MappingResolverOverride = async (_, _, token) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    return mapping;
                }
                catch (OperationCanceledException)
                {
                    mappingCancelled = true;
                    throw;
                }
            },
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exit = await ModuleCommandApplication.InvokeAsync(["run", "--manifest", workspace.Save(), "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, output.ToString());
            mappingCancelled.ShouldBeTrue();
            output.ToString().ShouldContain("HXR021");
            using JsonDocument formatted = JsonDocument.Parse(output.ToString());
            formatted.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("message").GetString()!.Contains("deadline='0.5s'", StringComparison.Ordinal));
        }
    }

    /// <summary>Verifies command-level mapping is cancelled by the original default deadline.</summary>
    /// <param name="command">The lifecycle command.</param>
    /// <param name="kind">The manifest generation.</param>
    /// <returns>The bounded mapping assertions.</returns>
    [Theory]
    [InlineData("run", "v1")]
    [InlineData("debug", "v1")]
    [InlineData("run", "v2")]
    [InlineData("debug", "v2")]
    public async Task MappingConsumesOriginalDefaultDeadlineAsync(string command, string kind)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        string manifest = kind == "v2" ? workspace.Save() : CompositionTestFiles.CopyExecutableManifest(ExecutableManifest(), workspace.Root);
        bool mappingCancelled = false;
        CompositionEngineOptions options = Options(workspace.Root, "unused.dll") with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            RootManifestCheckOverride = (_, _) => Task.FromResult(true),
            MappingResolverOverride = async (_, _, token) =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                    return CompositionTestFiles.CreatePlatformMapping(workspace.Root);
                }
                catch (OperationCanceledException)
                {
                    mappingCancelled = true;
                    throw;
                }
            },
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            int exit = await ModuleCommandApplication.InvokeAsync([command, "--manifest", manifest, "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, output.ToString());
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(7));
            mappingCancelled.ShouldBeTrue();
            output.ToString().ShouldContain("HXR021");
            using JsonDocument formatted = JsonDocument.Parse(output.ToString());
            formatted.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("message").GetString()!.Contains("diagnostics='", StringComparison.Ordinal));
            string summary = Directory.GetFiles(Path.Combine(options.StateDirectory, "diagnostics"), "summary.json", SearchOption.AllDirectories).Single();
            using JsonDocument retained = JsonDocument.Parse(await File.ReadAllTextAsync(summary, TestContext.Current.CancellationToken).ConfigureAwait(true));
            retained.RootElement.GetProperty("durationSeconds").GetDouble().ShouldBeGreaterThanOrEqualTo(1.8);
        }
    }

    /// <summary>Verifies a dependency-only override applies to mapping and its elapsed time reaches run state.</summary>
    /// <param name="command">The lifecycle command.</param>
    /// <returns>The override and duration assertions.</returns>
    [Theory]
    [InlineData("run")]
    [InlineData("debug")]
    public async Task DependencyOverrideIncludesMappingDurationAsync(string command)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.identity.moduleId", "\"mcpcli\"");
        workspace.Change("modules.0.identity.dependencies", "[\"memories\"]");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        SetDependencyStartup(mapping, 15, null);
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngineOptions options = Options(workspace.Root, appHost) with
        {
            SourceMapping = mapping,
            ReadinessTimeout = TimeSpan.FromSeconds(3),
            RootManifestCheckOverride = (_, _) => Task.FromResult(true),
            MappingResolverOverride = async (_, _, token) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(3.5), token).ConfigureAwait(false);
                return mapping;
            },
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exit = await ModuleCommandApplication.InvokeAsync([command, "--manifest", workspace.Save(), "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.Success, output.ToString());
            using JsonDocument result = JsonDocument.Parse(output.ToString());
            string runId = result.RootElement.GetProperty("runId").GetString()!;
            CompositionEngine engine = new(options);
            try
            {
                CompositionRunState state = (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
                state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(3.5);
                state.EffectiveDeadlineSeconds.ShouldBe(15);
                state.EffectiveDeadlineModuleId.ShouldBe("memories");
            }
            finally
            {
                _ = await engine.DownAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies a dependency task runs once in its owner's repository and its descendant belongs to down.</summary>
    /// <returns>The owner-path and process-scoping assertions.</returns>
    [Fact]
    public async Task DependencyTaskDescendantIsOwnedAndForeignProcessesSurviveAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.identity.moduleId", "\"mcpcli\"");
        workspace.Change("modules.0.identity.dependencies", "[\"memories\"]");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        string owner = mapping.Entries.Single(entry => entry.Identity == "Hexalith.Memories").Path!;
        CompositionTestFiles.WriteScript(Path.Combine(owner, "tools", "verify.sh"), "pwd > task-directory\nprintf x >> task-count\nsleep 120 >/dev/null 2>&1 &\necho $! > descendant\n");
        SetDependencyStartup(mapping, 30, "tools/verify.sh");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with { SourceMapping = mapping, PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost) });
        using Process foreign = Process.Start(new ProcessStartInfo("sleep", "120") { UseShellExecute = false })!;
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Process? descendant = null;
        bool stopped = false;
        try
        {
            result.IsReady.ShouldBeTrue(string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            (await File.ReadAllTextAsync(Path.Combine(owner, "task-directory"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Trim().ShouldBe(owner);
            (await File.ReadAllTextAsync(Path.Combine(owner, "task-count"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("x");
            descendant = Process.GetProcessById(int.Parse((await File.ReadAllTextAsync(Path.Combine(owner, "descendant"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Trim(), System.Globalization.CultureInfo.InvariantCulture));
            (await engine.Scanner.FindAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true)).ProcessIds.ShouldContain(descendant.Id);
            _ = await engine.DownAsync(result.Session!, TestContext.Current.CancellationToken).ConfigureAwait(true);
            stopped = true;
            descendant.HasExited.ShouldBeTrue();
            foreign.HasExited.ShouldBeFalse();
        }
        finally
        {
            if (result.Session is not null && !stopped)
            {
                _ = await engine.DownAsync(result.Session, CancellationToken.None).ConfigureAwait(true);
            }

            Kill(descendant);
            descendant?.Dispose();
            Kill(foreign);
        }
    }

    /// <summary>Verifies task side effects happen once and stale health cannot finish startup.</summary>
    /// <param name="regress">Whether health degrades during the successful task.</param>
    /// <returns>The freshness assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulTaskRequiresFreshHealthAndRunsOnceAsync(bool regress)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, regress ? "post-task-regression" : "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string marker = Path.Combine(Path.GetDirectoryName(appHost)!, "regressed");
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "verify.sh"), "printf x >> task-count\ntouch '" + marker + "'\n");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"verify.sh\"");
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(5),
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "task-count"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("x");
            result.IsReady.ShouldBe(!regress, string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            if (regress)
            {
                result.Result.Outcome.RuleId.ShouldBe("HXR021");
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("module-health-unhealthy", StringComparison.Ordinal));
                CompositionRunState state = (await engine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
                state.ReadinessResults.ShouldContain(resource => resource.Name == "eventstore" && resource.State == "Unhealthy");
            }
            else
            {
                _ = result.Session!.Readiness.RefreshId.ShouldNotBeNull();
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

    /// <summary>Verifies the deadline is checked again after the final readiness read.</summary>
    /// <returns>The ready-transition assertions.</returns>
    [Fact]
    public async Task DeadlineAtFinalReadyCommitRefusesSuccessAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        bool reachedCommit = false;
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(3),
            BeforeReadyCommitOverride = async token =>
            {
                reachedCommit = true;
                await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            },
        });
        CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        reachedCommit.ShouldBeTrue();
        result.IsReady.ShouldBeFalse();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        CompositionRunState state = (await engine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        state.Status.ShouldBe(CompositionRunStatus.Failed);
    }

    private static void SetDependencyStartup(SourceMapping mapping, int seconds, string? executable)
    {
        string path = Path.Combine(mapping.Entries.Single(entry => entry.Identity == "Hexalith.Memories").Path!, "module", "hexalith.module-manifest.v2.json");
        JsonNode document = JsonNode.Parse(File.ReadAllText(path))!;
        JsonNode lifecycle = document["modules"]![0]!["lifecycle"]!;
        lifecycle["startup"] = JsonNode.Parse("{\"defaultTimeoutSeconds\":600,\"override\":{\"timeoutSeconds\":" + seconds + ",\"justification\":\"dependency verification\"}}");
        if (executable is not null)
        {
            lifecycle["tasks"] = JsonNode.Parse("[{\"name\":\"verify\",\"scope\":\"per-start-verify\",\"authorityClass\":\"application\",\"executable\":\"" + executable + "\",\"arguments\":[]}]");
        }

        File.WriteAllText(path, document.ToJsonString());
    }

    private static CompositionEngineOptions Options(string root, string appHost) => new(appHost, typeof(ModuleCommandApplication).Assembly.Location)
    {
        AspireCommand = CompositionTestFiles.CreateAspire(root),
        DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
        DockerCommand = CompositionTestFiles.CreateStubDocker(root),
        StateDirectory = Path.Combine(root, "state"),
        WorkspaceRoot = Path.Combine(root, "workspaces"),
        ReadinessTimeout = TimeSpan.FromSeconds(30),
        StopTimeout = TimeSpan.FromSeconds(5),
    };

    private static string ExecutableManifest() => Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");

    private static void SkipWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts and process environment tags.");
        }
    }

    private static void Kill(Process? process)
    {
        if (process?.HasExited == false)
        {
            process.Kill(entireProcessTree: true);
        }
    }
}
