// <copyright file="CompositionEnvironmentTaskTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks environment-creation task scheduling, private invocation paths and the unchanged per-start path.</summary>
// Finite startup assertions must not compete with independent SDK/process fixtures.
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CompositionEnvironmentTaskTests
{
    private const string _readVariable = "printf '%s' \"${HEXALITH_TASK_INVOCATION_PATH-unset}\"";

    /// <summary>Verifies both startup scopes are scheduled in declaration order and only environment tasks get a private path.</summary>
    [Fact]
    public void SelectSchedulesEnvironmentCreationAndVerifyTasksInDeclarationOrder()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "task.sh"), "exit 0\n");
            PlatformModuleDeclaration declaration = Declaration(root, [
                ("create", "once-per-environment-creation"),
                ("check", "per-start-verify"),
                ("recover", "recovery"),
                ("seed", "once-per-environment-creation"),
            ]);
            string workspace = Path.Combine(root, "workspaces", "run");

            IReadOnlyList<CompositionStartupTask> tasks = CompositionStartupTasks.Select(Path.Combine(root, "module.json"), [declaration], null, workspace);

            tasks.Select(task => task.Name).ShouldBe(["create", "check", "seed"]);
            tasks.Select(task => task.Scope).ShouldBe([CompositionStartupTasks.EnvironmentCreationScope, CompositionStartupTasks.PerStartVerifyScope, CompositionStartupTasks.EnvironmentCreationScope]);
            tasks[0].InvocationPath.ShouldBe(Path.Combine(Path.GetFullPath(workspace), CompositionStartupTasks.InvocationDirectoryName, "sample.create.json"));
            tasks[1].InvocationPath.ShouldBeNull();
            tasks[2].InvocationPath.ShouldBe(Path.Combine(Path.GetFullPath(workspace), CompositionStartupTasks.InvocationDirectoryName, "sample.seed.json"));
            tasks.ShouldAllBe(task => task.ExecutableError == null && task.ExecutablePath == Path.Combine(root, "task.sh"));

            // Without a run workspace no invocation path exists, so the environment task fails closed when it runs.
            CompositionStartupTasks.Select(Path.Combine(root, "module.json"), [declaration]).ShouldAllBe(task => task.InvocationPath == null);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies an identifier that is not a plain path segment cannot escape the private invocation directory.</summary>
    /// <param name="moduleId">The declaring module.</param>
    /// <param name="name">The task name.</param>
    [Theory]
    [InlineData("sample", "../escape")]
    [InlineData("Sample", "create")]
    [InlineData("sample", "create/../../x")]
    public void InvocationPathStaysInsideThePrivateDirectory(string moduleId, string name)
    {
        string workspace = Path.Combine(Path.GetTempPath(), "workspace");

        string path = CompositionStartupTasks.InvocationPathFor(workspace, moduleId, name);

        Path.GetDirectoryName(path).ShouldBe(Path.Combine(Path.GetFullPath(workspace), CompositionStartupTasks.InvocationDirectoryName));
        Path.GetFileName(path).ShouldMatch("^[0-9a-f]{32}\\.json$");
    }

    /// <summary>Verifies an environment task sees exactly its own invocation path and a per-start task sees none.</summary>
    /// <returns>The environment assertions.</returns>
    [Fact]
    public async Task OnlyEnvironmentCreationTasksReceiveAnInvocationPathAsync()
    {
        SkipWindows();
        string root = CompositionTestFiles.CreateDirectory();
        string? inherited = Environment.GetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath);
        try
        {
            // A decoy inherited by the runner must never reach either task.
            Environment.SetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath, Path.Combine(root, "decoy.json"));
            string invocation = CompositionStartupTasks.InvocationPathFor(Path.Combine(root, "run"), "sample", "create");
            CompositionStartupTask create = new("sample", "create", "/bin/sh", ["-c", _readVariable + " > create.observed"])
            {
                Scope = CompositionStartupTasks.EnvironmentCreationScope,
                WorkingDirectory = root,
                InvocationPath = invocation,
            };
            CompositionStartupTask verify = new("sample", "verify", "/bin/sh", ["-c", _readVariable + " > verify.observed"])
            {
                WorkingDirectory = root,
            };

            (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync([create, verify], DateTimeOffset.UtcNow.AddSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);

            failingTask.ShouldBeNull();
            deadlineElapsed.ShouldBeFalse();
            (await File.ReadAllTextAsync(Path.Combine(root, "create.observed"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(invocation);
            (await File.ReadAllTextAsync(Path.Combine(root, "verify.observed"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("unset");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath, inherited);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the runner schedules an environment task, creates its private directory and reports the task.</summary>
    /// <param name="documentRequired">Whether the task refuses to run without the model-written invocation document.</param>
    /// <returns>The engine assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentCreationTaskRunsWithItsPrivateInvocationPathAsync(bool documentRequired)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        string script = "printf '%s' \"$HEXALITH_TASK_INVOCATION_PATH\" > observed-path\n"
            + "stat -c %a \"$(dirname \"$HEXALITH_TASK_INVOCATION_PATH\")\" > observed-mode\n"
            + (documentRequired ? "[ -f \"$HEXALITH_TASK_INVOCATION_PATH\" ] || exit 2\n" : string.Empty)
            + "exit 0\n";
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "create.sh"), script);
        workspace.Change("modules.0.lifecycle.tasks.0.scope", "\"once-per-environment-creation\"");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"create.sh\"");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            string runId = result.RunId.ShouldNotBeNull();
            string expected = CompositionStartupTasks.InvocationPathFor(Path.Combine(workspace.Root, "workspaces", runId), "sample", "verify");
            (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "observed-path"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(expected);
            (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "observed-mode"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Trim().ShouldBe("700");
            if (documentRequired)
            {
                // The fake model writes no invocation document: the task fails closed and is named.
                result.Session.ShouldBeNull();
                result.Result.Outcome.RuleId.ShouldBe("HXR021");
                result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXR021" && diagnostic.Message.Contains("'sample/verify'", StringComparison.Ordinal));
                Directory.Exists(Path.Combine(workspace.Root, "workspaces", runId)).ShouldBeFalse();
                File.Exists(Path.Combine(engine.StateStore.Directory, "diagnostics", runId, "summary.json")).ShouldBeTrue();
                return;
            }

            result.IsReady.ShouldBeTrue(string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            CompositionRunPlan persisted = (await CompositionDocumentStore.TryReadAsync<CompositionRunPlan>(Path.Combine(result.Session!.Plan.Workspace, "plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            CompositionStartupTask task = persisted.StartupTasks.ShouldHaveSingleItem();
            task.Scope.ShouldBe(CompositionStartupTasks.EnvironmentCreationScope);
            task.InvocationPath.ShouldBe(expected);
            CompositionStartupTaskResult reported = result.Session.Readiness.StartupTaskResults.ShouldHaveSingleItem();
            reported.TaskId.ShouldBe("sample/verify");
            reported.Status.ShouldBe("passed");
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies a per-start task keeps its previous plan shape and environment.</summary>
    /// <returns>The unchanged per-start assertions.</returns>
    [Fact]
    public async Task PerStartVerifyTaskKeepsItsUnchangedPathAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "verify.sh"), _readVariable + " > observed-path\nexit 0\n");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"verify.sh\"");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
        });

        string? inherited = Environment.GetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath);
        Environment.SetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath, Path.Combine(workspace.Root, "decoy.json"));
        CompositionStartResult result;
        try
        {
            result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompositionEnvironment.TaskInvocationPath, inherited);
        }

        try
        {
            result.IsReady.ShouldBeTrue(string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "observed-path"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("unset");
            string planPath = Path.Combine(result.Session!.Plan.Workspace, "plan.json");
            using JsonDocument plan = JsonDocument.Parse(await File.ReadAllTextAsync(planPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            JsonElement task = plan.RootElement.GetProperty("startupTasks").EnumerateArray().ShouldHaveSingleItem();
            task.GetProperty("scope").GetString().ShouldBe(CompositionStartupTasks.PerStartVerifyScope);
            task.TryGetProperty("invocationPath", out _).ShouldBeFalse();
            Directory.Exists(Path.Combine(result.Session.Plan.Workspace, CompositionStartupTasks.InvocationDirectoryName)).ShouldBeFalse();
            result.Session.Readiness.StartupTaskResults.ShouldHaveSingleItem().Status.ShouldBe("passed");
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static PlatformModuleDeclaration Declaration(string root, IEnumerable<(string Name, string Scope)> tasks) =>
        new(Path.Combine(root, "module.json"), JsonSerializer.SerializeToElement(new
        {
            identity = new { moduleId = "sample" },
            lifecycle = new { tasks = tasks.Select(task => new { name = task.Name, scope = task.Scope, executable = "task.sh", arguments = Array.Empty<string>() }).ToArray() },
        }));

    private static CompositionEngineOptions Options(string root, string appHost) =>
        new(appHost, typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = CompositionTestFiles.CreateAspire(root),
            AspirePackagesRoot = CompositionTestFiles.CreateAspirePackages(root).Root,
            DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(root),
            StateDirectory = Path.Combine(root, "state"),
            WorkspaceRoot = Path.Combine(root, "workspaces"),
            ReadinessTimeout = TimeSpan.FromSeconds(60),
            StopTimeout = TimeSpan.FromSeconds(5),
        };

    private static void SkipWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The startup task doubles use POSIX scripts.");
        }
    }
}
