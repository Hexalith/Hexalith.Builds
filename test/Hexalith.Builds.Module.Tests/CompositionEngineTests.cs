// <copyright file="CompositionEngineTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the composition engine fails closed before any resource starts and tears down idempotently.
/// </summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CompositionEngineTests
{
    /// <summary>Unsupported selected-suite fallback refuses before prerequisites or allocation.</summary>
    /// <returns>The useful early refusal.</returns>
    [Fact]
    public async Task SelectedFallbackRefusesBeforeAllocationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.startup", "{}");
        workspace.Change("modules.0.tests", "[{\"name\":\"suite\",\"executor\":\"python3\",\"entry\":\"suite.py\",\"reportFormat\":\"trx\",\"executionTimeoutSeconds\":5,\"stopTimeoutSeconds\":1,\"mode\":\"source\"}]");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "suite.py"), "pass", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformModuleDeclaration> declarations = Hexalith.Builds.Tooling.Manifest.PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!;
        ModuleTestSelection selected = ModuleTestSelection.Select(declarations, "suite", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionEngineOptions options = CreateOptions(workspace.Root) with { SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root), SelectedTest = selected, ReadinessTimeout = TimeSpan.FromSeconds(2) };
        CompositionStartResult result = await new CompositionEngine(options).StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
        result.Result.Diagnostics.Single().Message.ShouldContain("600-second startup default");
        result.RunId.ShouldBeNull();
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
        Directory.Exists(options.StateDirectory).ShouldBeFalse();
    }

    /// <summary>Verifies the owning preflight supplies Development through the environment scrubber before runtime startup.</summary>
    /// <returns>The result assertion.</returns>
    [Fact]
    public async Task PlatformPreflightPassesDevelopmentAndScrubsAmbientEnvironmentAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts.");
        }

        const string sentinel = "HEXALITH_G4_PREFLIGHT_SENTINEL";
        string? previous = Environment.GetEnvironmentVariable(sentinel);
        string? previousDotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        string? previousAspnetcoreEnvironment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        Environment.SetEnvironmentVariable(sentinel, "runner-secret-must-not-leak");
        Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", "Production");
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Production");
        try
        {
            using PlatformManifestTestWorkspace workspace = new();
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "preflight-environment", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngineOptions options = CreateOptions(workspace.Root) with
            {
                AppHostAssemblyPath = appHost,
                SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
                DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
                DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root),
                PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
            };
            CompositionEngine engine = new(options);
            CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Session.ShouldBeNull();
            result.Result.Outcome.RuleId.ShouldBe("HXP020");
            result.Result.Diagnostics.Single().Message.ShouldContain("preflight environment observed; no runtime start");
            using JsonDocument captured = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(appHost)!, "preflight-environment.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
            captured.RootElement.GetProperty("DOTNET_ENVIRONMENT").GetString().ShouldBe("Development");
            captured.RootElement.GetProperty("ASPNETCORE_ENVIRONMENT").ValueKind.ShouldBe(JsonValueKind.Null);
            captured.RootElement.GetProperty(sentinel).ValueKind.ShouldBe(JsonValueKind.Null);
            captured.RootElement.GetProperty("HEXALITH_G4_RUN_ID").GetString().ShouldBe(result.RunId);
            captured.RootElement.GetProperty("HEXALITH_G4_PLAN").GetString().ShouldEndWith("plan.json");
            captured.RootElement.GetProperty("HEXALITH_G4_SIGNING_KEY").GetString().ShouldNotBeNullOrWhiteSpace();
            CompositionRunState state = (await engine.StateStore.TryReadAsync(result.RunId!, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            state.AppHostProcessId.ShouldBeNull();
            Directory.Exists(state.Workspace).ShouldBeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, previous);
            Environment.SetEnvironmentVariable("DOTNET_ENVIRONMENT", previousDotnetEnvironment);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", previousAspnetcoreEnvironment);
        }
    }

    /// <summary>Verifies a v2 AppHost preflight refusal preserves failure evidence while removing its workspace.</summary>
    /// <returns>The result assertion.</returns>
    [Fact]
    public async Task PlatformPreflightPreservesFailureStateAndDiagnosticsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":30,\"justification\":\"preflight diagnostics budget\"}");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "preflight-refusal", TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        CompositionEngineOptions options = CreateOptions(workspace.Root) with
        {
            AppHostAssemblyPath = appHost,
            SourceMapping = mapping,
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
            StartupRequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-2),
        };
        CompositionEngine engine = new(options);
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Session.ShouldBeNull();
        result.Result.Outcome.RuleId.ShouldBe("HXP020");
        result.Result.Diagnostics.Single().Message.ShouldContain("owner/source.json: gateway: required binding is unavailable");
        string runId = result.RunId.ShouldNotBeNull();
        CompositionRunState state = (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        state.Status.ShouldBe(CompositionRunStatus.Failed);
        state.RuleId.ShouldBe("HXP020");
        state.AppHostProcessId.ShouldBeNull();
        state.StartupDurationSeconds.ShouldNotBeNull().ShouldBeGreaterThanOrEqualTo(2);
        state.EffectiveDeadlineSeconds.ShouldBe(30);
        state.EffectiveDeadlineIsOverride.ShouldBeTrue();
        state.EffectiveDeadlineJustification.ShouldBe("preflight diagnostics budget");
        state.EffectiveDeadlineModuleId.ShouldBe("sample");
        string diagnostics = state.DiagnosticsPath.ShouldNotBeNull();
        diagnostics.ShouldStartWith(Path.Combine(options.StateDirectory, "diagnostics"));
        result.Result.Diagnostics.Single().Message.ShouldContain(diagnostics);
        CompositionStartupReport report = (await CompositionDocumentStore.TryReadAsync<CompositionStartupReport>(Path.Combine(diagnostics, "summary.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        report.RunId.ShouldBe(runId);
        report.RuleId.ShouldBe("HXP020");
        report.DurationSeconds.ShouldBeGreaterThanOrEqualTo(2);
        report.EffectiveDeadlineSeconds.ShouldBe(30);
        report.EffectiveDeadlineIsOverride.ShouldBeTrue();
        report.EffectiveDeadlineModuleId.ShouldBe("sample");
        Directory.Exists(state.Workspace).ShouldBeFalse();
        Directory.Exists(options.WorkspaceRoot).ShouldBeTrue();
        Directory.GetDirectories(options.WorkspaceRoot).ShouldBeEmpty();
    }

    /// <summary>Verifies a failed Platform model build reports its cause before run state exists.</summary>
    /// <returns>The result assertion.</returns>
    [Fact]
    public async Task PlatformModelBuildFailureKeepsCauseAndDeletesWorkspaceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "preflight-refusal", TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        CompositionEngineOptions options = CreateOptions(workspace.Root) with
        {
            AppHostAssemblyPath = appHost,
            SourceMapping = mapping,
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root),
            PlatformModelBuildOverride = (_, _, _) => throw new InvalidOperationException("model.csproj: package restore failed"),
        };
        CompositionStartResult result = await new CompositionEngine(options).StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Session.ShouldBeNull();
        result.Result.Outcome.RuleId.ShouldBe("HXR020");
        result.Result.Diagnostics.Single().Message.ShouldContain("package restore failed");
        result.Result.Diagnostics.Single().Field!.ShouldContain("Hexalith.Platform.Composition.csproj");
        _ = result.RunId.ShouldNotBeNull();
        CompositionRunState? state = await new CompositionEngine(options).StateStore.TryReadAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = state.ShouldNotBeNull();
        state.Status.ShouldBe(CompositionRunStatus.Failed);
        _ = state.StartupDurationSeconds.ShouldNotBeNull();
        _ = state.EffectiveDeadlineSeconds.ShouldNotBeNull();
        File.Exists(Path.Combine(state.DiagnosticsPath!, "summary.json")).ShouldBeTrue();
        Directory.Exists(options.WorkspaceRoot).ShouldBeTrue();
        Directory.GetDirectories(options.WorkspaceRoot).ShouldBeEmpty();
    }

    /// <summary>An allocated declared owner records mapping refusal as a retained startup failure rather than an orphaned Starting state.</summary>
    /// <param name="declared">Whether a declared suite owns the allocated environment.</param>
    /// <param name="deletionFails">Whether the refused workspace became a peer link.</param>
    /// <returns>The durable first outcome and exact cleanup assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AllocatedMappingFailurePreservesDeclaredOutcomeAndLegacyCleanupAsync(bool declared, bool deletionFails)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = CreateOptions(workspace.Root) with
        {
            AppHostAssemblyPath = typeof(CompositionEngine).Assembly.Location,
            AspirePackagesRoot = CompositionTestFiles.CreateAspirePackages(workspace.Root).Root,
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            SelectedTest = declared ? selected : null,
            RetainOnFailure = declared,
            PlatformModelBuildOverride = async (_, prepared, token) =>
            {
                if (deletionFails)
                {
                    string peer = Path.Combine(workspace.Root, "peer");
                    CompositionWorkspace.CreatePrivateDirectory(peer);
                    await File.WriteAllTextAsync(Path.Combine(peer, "data"), "peer-sentinel", token).ConfigureAwait(false);
                    Directory.Delete(prepared, true);
                    Directory.CreateSymbolicLink(prepared, peer).LinkTarget.ShouldBe(peer);
                }

                throw new WorkspaceMappingException("Verified catalog inputs are unavailable.");
            },
        };
        CompositionEngine engine = new(options);
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        if (deletionFails)
        {
            string retained = result.RunId.ShouldNotBeNull();
            CompositionRunState retry = (await engine.StateStore.TryReadAsync(retained, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            retry.CleanupDisposition.ShouldBe("incomplete");
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
            (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "peer", "data"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("peer-sentinel");
            Directory.Delete(retry.Workspace);
            return;
        }

        if (!declared)
        {
            result.RunId.ShouldBeNull();
            (await engine.StateStore.EnumerateAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeEmpty();
            Directory.GetDirectories(options.WorkspaceRoot).ShouldBeEmpty();
            return;
        }

        string runId = result.RunId.ShouldNotBeNull();
        CompositionRunState state = (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        state.Status.ShouldBe(CompositionRunStatus.Failed);
        state.TestRun.ShouldNotBeNull().FailureRetained.ShouldBeTrue();
        Directory.Exists(state.Workspace).ShouldBeTrue();
        string path = Path.Combine(options.StateDirectory, "diagnostics", runId, "test-result.json");
        Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReadResult terminal = Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        terminal.IsValid.ShouldBeTrue();
        terminal.Document!.Value.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("startup-failure");
        (await engine.DownAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        Directory.Exists(state.Workspace).ShouldBeFalse();
    }

    /// <summary>
    /// Verifies unavailable prerequisites return <c>HXR01x</c> with exit code 2 and create no run state or workspace.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingPrerequisitesStopBeforeAnyResourceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Aspire CLI uses POSIX shell scripts.");
        }

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
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
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
    /// Verifies missing catalog Aspire DCP or dashboard packages are an unavailable prerequisite reported before any
    /// resource starts, without using the paths baked into the AppHost.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingAspireOrchestrationIsUnavailableBeforeAnyResourceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = Path.Combine(root, "apphost", "Hexalith.Builds.Module.AppHost.dll");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(appHost)!);
            await File.WriteAllTextAsync(appHost, string.Empty, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string packages = Path.Combine(root, "empty-nuget-packages");
            _ = Directory.CreateDirectory(packages);
            CompositionEngine engine = new(CreateOptions(root) with
            {
                AppHostAssemblyPath = appHost,
                AspirePackagesRoot = packages,
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
                DockerCommand = CompositionTestFiles.CreateDocker(root),
            });

            CompositionStartResult result = await engine.StartAsync(PositiveManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Session.ShouldBeNull();
            result.RunId.ShouldBeNull();
            result.Result.Status.ShouldBe("unavailable");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
            ToolDiagnostic diagnostic = result.Result.Diagnostics.Single();
            diagnostic.RuleId.ShouldBe("HXR016");
            diagnostic.Message.ShouldContain(packages);
            Directory.Exists(Path.Combine(root, "state")).ShouldBeFalse();
            Directory.Exists(Path.Combine(root, "workspaces")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies the AppHost process receives this machine's catalog DCP and dashboard and run-private AppHost and
    /// store directories that exist before it starts.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task AppHostReceivesLocalAspireOrchestrationAndRunPrivateDirectoriesAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            (string packages, string dcp, string dashboard) = CompositionTestFiles.CreateAspirePackages(root);
            string repository = RepositoryRoot();
            SourceMapping mapping = new(WorkspaceMode.Source, repository, "Hexalith.Builds", [new SourceMappingEntry("Hexalith.Builds", "source", repository)], false);
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "capture", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngineOptions options = CreateOptions(root) with
            {
                AppHostAssemblyPath = appHost,
                AspirePackagesRoot = packages,
                SourceMapping = mapping,
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
                DockerCommand = CompositionTestFiles.CreateStubDocker(root),
            };

            _ = await new CompositionEngine(options).StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            string output = Path.GetDirectoryName(appHost)!;
            using System.Text.Json.JsonDocument captured = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "captured-environment.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
            using System.Text.Json.JsonDocument plan = System.Text.Json.JsonDocument.Parse(
                await File.ReadAllTextAsync(Path.Combine(output, "captured-plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
            string workspace = plan.RootElement.GetProperty("workspace").GetString()!;
            System.Text.Json.JsonElement environment = captured.RootElement;
            environment.GetProperty("DcpPublisher__CliPath").GetString().ShouldBe(dcp);
            environment.GetProperty("DcpPublisher__DashboardPath").GetString().ShouldBe(dashboard);
            environment.GetProperty("HEXALITH_G4_APPHOST_DIRECTORY").GetString().ShouldBe(Path.Combine(workspace, "apphost"));
            environment.GetProperty("HEXALITH_G4_ASPIRE_STORE_PATH").GetString().ShouldBe(Path.Combine(workspace, "aspire-store"));
            environment.GetProperty("HEXALITH_G4_APPHOST_DIRECTORY:exists").GetString().ShouldBe(bool.TrueString);
            environment.GetProperty("HEXALITH_G4_ASPIRE_STORE_PATH:exists").GetString().ShouldBe(bool.TrueString);
            workspace.ShouldStartWith(options.WorkspaceRoot);
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
                new CompositionRunState(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Failed, "A", Path.Combine(root, "workspaces", CompositionTestFiles.RunId), null, null, "HXR021", DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await engine.StateStore.WriteAsync(
                new CompositionRunState(CompositionRunState.SupportedSchema, otherRunId, CompositionRunStatus.Ready, "B", Path.Combine(root, "workspaces", otherRunId), null, null, null, DateTimeOffset.UnixEpoch),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            string workspace = Path.Combine(root, "workspaces", CompositionTestFiles.RunId);
            _ = Directory.CreateDirectory(workspace);
            engine.StateStore.Claims.Acquire(CompositionTestFiles.RunId, ["owner-listener"]);
            engine.StateStore.Claims.Acquire(otherRunId, ["peer-listener"]);

            CompositionDownResult first = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            engine.StateStore.Claims.Acquire(CompositionTestFiles.RunId, ["owner-listener"]);
            Should.Throw<CompositionResourceClaimConflictException>(() => engine.StateStore.Claims.Acquire(CompositionTestFiles.RunId, ["peer-listener"])).OwnerRunId.ShouldBe(otherRunId);
            CompositionDownResult second = await engine.DownAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionDownResult malformed = await engine.DownAsync("../../escape", TestContext.Current.CancellationToken).ConfigureAwait(true);

            first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            first.Remaining.IsEmpty.ShouldBeTrue();
            engine.StateStore.Claims.Acquire(CompositionTestFiles.RunId, ["owner-listener"]);
            Should.Throw<CompositionResourceClaimConflictException>(() => engine.StateStore.Claims.Acquire(CompositionTestFiles.RunId, ["peer-listener"])).OwnerRunId.ShouldBe(otherRunId);
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

    /// <summary>Incomplete teardown keeps owner claims blocking while preserving an independent peer.</summary>
    /// <returns>The exact ownership assertions.</returns>
    [Fact]
    public async Task IncompleteCleanupKeepsOwnerAndPeerClaimsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses POSIX scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string docker = Path.Combine(root, "leaky-docker");
            CompositionTestFiles.WriteScript(docker, "if [ \"$1\" = ps ]; then echo leaked0123456789; fi");
            CompositionEngine engine = new(CreateOptions(root) with { DockerCommand = docker });
            const string owner = CompositionTestFiles.RunId;
            string peer = CompositionRunPlanFactory.NewRunId();
            engine.StateStore.Claims.Acquire(owner, ["owner-listener"]);
            engine.StateStore.Claims.Acquire(peer, ["peer-listener"]);
            CompositionDownResult down = await engine.DownAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            down.Remaining.IsEmpty.ShouldBeFalse();
            Should.Throw<CompositionResourceClaimConflictException>(() => engine.StateStore.Claims.Acquire(peer, ["owner-listener"])).OwnerRunId.ShouldBe(owner);
            Should.Throw<CompositionResourceClaimConflictException>(() => engine.StateStore.Claims.Acquire(owner, ["peer-listener"])).OwnerRunId.ShouldBe(peer);
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
            string manifest = CompositionTestFiles.CopyExecutableManifest(ExecutableManifest(), root);
            CompositionEngineOptions options = new(Path.Combine(root, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
            {
                AspireCommand = CompositionTestFiles.CreateAspire(root),
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
    [InlineData("unsafe-name", "HXR022")]
    [InlineData("unsafe-detail", "HXR022")]
    [InlineData("unsafe-task", "HXR022")]
    [InlineData("diagnostics-io", "HXR027")]
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
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
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
            result.Result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(mode == "diagnostics-io" ? [expectedRuleId, "HXT008"] : [expectedRuleId]);
            result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Message.Contains("<unsafe>", StringComparison.Ordinal));
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
            state.CleanupDisposition.ShouldBe("complete");
            state.RemainingResources.ShouldNotBeNull().IsEmpty.ShouldBeTrue();
            state.AppHostProcessId.ShouldBeNull();
            (await engine.Scanner.FindAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies a readiness failure whose teardown leaves a tagged container behind reports <c>HXR023</c> and persists
    /// it as the failed run's rule so a retried <c>down</c> can see the unclean teardown.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task UncleanTeardownAfterReadinessFailureReportsAndPersistsHxr023Async()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "never", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string docker = Path.Combine(root, "leaky-docker");
            CompositionTestFiles.WriteScript(docker, "case \"$1\" in\n  version) echo '29.0.0' ;;\n  ps) echo leaked0123456789 ;;\n  *) exit 0 ;;\nesac");
            CompositionEngine engine = new(CreateOptions(root) with
            {
                AppHostAssemblyPath = appHost,
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
                DockerCommand = docker,
                ReadinessTimeout = TimeSpan.FromSeconds(3),
            });

            CompositionStartResult result = await engine.StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR021", "HXR023"]);
            _ = result.RunId.ShouldNotBeNull();
            CompositionRunState? state = await engine.StateStore.TryReadAsync(result.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            state.Status.ShouldBe(CompositionRunStatus.Failed);
            state.RuleId.ShouldBe("HXR023");
            state.CleanupDisposition.ShouldBe("incomplete");
            state.RemainingResources.ShouldNotBeNull().DiscoveryFailures.ShouldContain("containers");
            ToolCommandResult listing = await CompositionRunListing.ReadAsync(engine.StateStore, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
            listing.Environments.ShouldNotBeNull().Single().Cleanup.ShouldBe("incomplete");
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

    /// <summary>Verifies every unavailable Aspire observation stops the engine before descriptors and artifacts.</summary>
    /// <param name="mode">The fake Aspire behavior.</param>
    /// <returns>The asynchronous test.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("different")]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("timeout")]
    public async Task UnavailableAspireCreatesNoRunArtifactsAsync(string mode)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Aspire CLI uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string aspire = Path.Combine(root, "observed-aspire");
            if (mode != "missing")
            {
                string body = mode switch
                {
                    "malformed" => "echo 'bad-version'",
                    "different" => "echo '0.0.1'",
                    "empty" => "exit 0",
                    "failed" => $"echo '{CompositionToolchainPins.AspireAppHostSdkVersion}'; exit 1",
                    _ => "sleep 30",
                };
                CompositionTestFiles.WriteScript(aspire, body);
            }

            CompositionEngineOptions options = CreateOptions(root) with
            {
                AspireCommand = aspire,
                AspireProbeTimeout = TimeSpan.FromMilliseconds(200),
                DescriptorChildEntryAssemblyPath = Path.Combine(root, "missing-descriptor-child.dll"),
                DockerCommand = Path.Combine(root, "missing-docker"),
            };
            CompositionStartResult result = await new CompositionEngine(options).StartAsync(
                ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
            result.Result.Diagnostics.Single().RuleId.ShouldBe("HXR015");
            result.Result.Diagnostics.Single().Message.ShouldContain(CompositionToolchainPins.AspireAppHostSdkVersion);
            result.RunId.ShouldBeNull();
            result.Session.ShouldBeNull();
            Directory.Exists(options.StateDirectory).ShouldBeFalse();
            Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies incompatible Aspire blocks both public startup commands before descriptor or run artifacts.</summary>
    /// <param name="command">The public startup command.</param>
    /// <param name="format">The diagnostic format.</param>
    /// <returns>The asynchronous test.</returns>
    [Theory]
    [InlineData("run", "json")]
    [InlineData("run", "human")]
    [InlineData("debug", "json")]
    [InlineData("debug", "human")]
    [InlineData("test", "json")]
    [InlineData("test", "human")]
    public async Task AspireMismatchStopsPublicStartupAsync(string command, string format)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Aspire CLI uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = CompositionTestFiles.CopyExecutableManifest(ExecutableManifest(), root);
            string aspire = Path.Combine(root, "wrong-aspire");
            CompositionTestFiles.WriteScript(aspire, "echo '0.0.1'");
            CompositionEngineOptions options = CreateOptions(root) with
            {
                AspireCommand = aspire,
                DescriptorChildEntryAssemblyPath = Path.Combine(root, "missing-descriptor-child.dll"),
                DockerCommand = Path.Combine(root, "missing-docker"),
            };
            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                StringWriter error = new();
                await using (error.ConfigureAwait(true))
                {
                    string[] arguments = command == "test"
                        ? [command, "--manifest", manifest, "--profile", "live", "--output", format]
                        : [command, "--manifest", manifest, "--output", format];
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        arguments,
                        output,
                        error,
                        TestContext.Current.CancellationToken,
                        typeof(ModuleCommandApplication).Assembly.Location,
                        options).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable);
                    string diagnostics = string.Concat(output, error);
                    diagnostics.ShouldContain("HXR015");
                    diagnostics.ShouldContain("0.0.1");
                    diagnostics.ShouldContain(CompositionToolchainPins.AspireAppHostSdkVersion);
                    diagnostics.ShouldNotContain("HXR010");
                    diagnostics.ShouldNotContain("HXR003");
                    Directory.Exists(options.StateDirectory).ShouldBeFalse();
                    Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies an unwritable pre-run diagnostics directory preserves the startup timeout result.</summary>
    /// <returns>The timeout and bookkeeping assertions.</returns>
    [Fact]
    public async Task PreRunTimeoutSurvivesDiagnosticsIoFailureAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionEngineOptions options = CreateOptions(root) with { StartupRequestedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-30) };
            _ = Directory.CreateDirectory(options.StateDirectory);
            await File.WriteAllTextAsync(Path.Combine(options.StateDirectory, "diagnostics"), "blocked", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionStartResult result = await new CompositionEngine(options).StartAsync(ExecutableManifest(), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Result.Outcome.RuleId.ShouldBe("HXR021");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
            result.RunId.ShouldBeNull();
            Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Final manifest refusal after owner issuance preserves the selected outcome or finalizes ordinary tracking.</summary>
    /// <param name="declared">Whether a selected suite owns this preparation.</param>
    /// <returns>The issued identity, terminal record and resource-disposition assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalManifestRefusalFinalizesIssuedOwnerAsync(bool declared)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = CreateOptions(workspace.Root) with
        {
            AppHostAssemblyPath = typeof(CompositionEngine).Assembly.Location,
            AspirePackagesRoot = CompositionTestFiles.CreateAspirePackages(workspace.Root).Root,
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            DaprHome = CompositionTestFiles.CreateDaprHome(workspace.Root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(600),
            SelectedTest = declared ? selected : null,
            RetainOnFailure = declared,
            PlatformModelBuildOverride = (_, _, _) =>
            {
                workspace.Change("modules.0.identity.moduleId", "\"invalid module identity\"");
                _ = workspace.Save();
                return Task.FromResult(typeof(CompositionEngine).Assembly.Location);
            },
        };
        CompositionEngine engine = new(options);
        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        string runId = result.RunId.ShouldNotBeNull();
        if (declared)
        {
            ModuleTestRunRecord terminal = (await engine.ReadCommittedOutcomeAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            terminal.TerminalCause.ShouldBe("startup-failure");
            terminal.FailureRetained.ShouldBeTrue();
            _ = (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull().TestRun.ShouldNotBeNull();
            Hexalith.Builds.Tooling.TestEnvironment.TestEnvironmentContractReader.ReadResult(Path.Combine(options.StateDirectory, "diagnostics", runId, "test-result.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        }
        else
        {
            File.Exists(engine.StateStore.PathFor(runId)).ShouldBeFalse();
            Directory.Exists(Path.Combine(options.WorkspaceRoot, runId)).ShouldBeFalse();
        }
    }

    private static Task WriteRecordedStateAsync(CompositionEngine engine, int processId, DateTimeOffset startedAt) =>
        engine.StateStore.WriteAsync(
            new CompositionRunState(
                CompositionRunState.SupportedSchema,
                CompositionTestFiles.RunId,
                CompositionRunStatus.Ready,
                "A",
                Path.Combine(Path.GetDirectoryName(engine.StateStore.Directory)!, "workspaces", CompositionTestFiles.RunId),
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
            AspireCommand = CompositionTestFiles.CreateAspire(root),
            ContinuousIntegrationOverride = false,
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
