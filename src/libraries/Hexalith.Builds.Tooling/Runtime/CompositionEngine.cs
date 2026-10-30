// <copyright file="CompositionEngine.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// The runner-owned composition engine: probes prerequisites, plans one run, launches the Builds-owned
/// AppHost with a scrubbed environment, waits for bounded readiness, and tears the run down idempotently.
/// </summary>
/// <param name="options">The engine options.</param>
public sealed class CompositionEngine(CompositionEngineOptions options)
{
    private static readonly TimeSpan _pollInterval = TimeSpan.FromMilliseconds(500);

    private readonly CompositionEngineOptions _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>
    /// Gets the state store used by this engine.
    /// </summary>
    public CompositionRunStateStore StateStore { get; } = new(options.StateDirectory);

    /// <summary>
    /// Gets the resource scanner used by this engine.
    /// </summary>
    public CompositionRunResourceScanner Scanner { get; } = new(options.DockerCommand);

    /// <summary>
    /// Starts one run for a validated manifest.
    /// </summary>
    /// <param name="manifestPath">The manifest path.</param>
    /// <param name="cancellationToken">The cancellation token. Cancellation runs bounded teardown.</param>
    /// <returns>The start outcome; a session only when every resource is healthy.</returns>
    /// <exception cref="InvalidOperationException">A v2 command has no resolved source mapping.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "AppHost process ownership transfers to the returned session or to bounded teardown, which disposes it.")]
    public async Task<CompositionStartResult> StartAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        DateTimeOffset requestedAt = _options.SelectedTest?.RequestedAtUtc ?? _options.StartupRequestedAtUtc ?? DateTimeOffset.UtcNow;
        CompositionEffectiveDeadline deadline = CompositionStartupDeadlineResolver.Resolve(null, _options.ReadinessTimeout);
        using CancellationTokenSource startupBound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        string? runId = null;
        string? preparedWorkspace = null;
        Process? appHost = null;
        FileStream? provisioningLease = null;
        string manifestHash = string.Empty;
        try
        {
            async Task<CompositionStartResult> RefuseUnstartedAsync(IReadOnlyList<ToolDiagnostic> diagnostics, ToolExitCode exit, string status = "failed")
            {
                if (provisioningLease is not null)
                {
                    await provisioningLease.DisposeAsync().ConfigureAwait(false);
                    provisioningLease = null;
                }

                return await RefusePreparedRunAsync(runId, preparedWorkspace, manifestHash, requestedAt, deadline, diagnostics, exit, status).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            bool platformComposition = PlatformManifestCommand.IsV2(manifestPath);
            PlatformManifestValidationResult? platformDeclarations = platformComposition ? PlatformManifestValidator.Validate([manifestPath], cancellationToken) : null;
            if (platformDeclarations is { IsValid: false })
            {
                return Failed("failed", ToolExitCode.UsageOrManifest, platformDeclarations.Diagnostics, null);
            }

            if (_options.SelectedTest is { } selectedTest && !selectedTest.MatchesSource(manifestPath))
            {
                return Failed("failed", ToolExitCode.UsageOrManifest, [Diagnostic("HXP020", ToolPhase.Manifest, ToolFailureCategory.Manifest, "The selected declaration or test entry changed before startup.", "tests", null)], null);
            }

            ManifestLoadResult manifestResult = platformComposition ? new ManifestLoadResult(null, []) : ModuleManifestLoader.Load(manifestPath);
            if (!platformComposition && !manifestResult.IsValid)
            {
                return Failed("failed", ToolExitCode.UsageOrManifest, manifestResult.Diagnostics, null);
            }

            ModuleManifest manifest = manifestResult.Manifest!;
            string? activeModule = PlatformManifestCommand.ActiveModule(platformDeclarations?.Declarations);
            IReadOnlyList<PlatformModuleDeclaration>? closure = platformComposition
                ? CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(
                    _options.SourceMapping ?? throw new InvalidOperationException("Platform v2 requires a resolved source mapping."),
                    [Path.GetFullPath(manifestPath)])
                { ActiveModule = activeModule })
                : null;
            deadline = CompositionStartupDeadlineResolver.Resolve(closure, _options.ReadinessTimeout);
            if (_options.SelectedTest is not null)
            {
                try
                {
                    TestEnvironment.TestEnvironmentDescriptorWriter.ValidatePreProvisioning(deadline);
                }
                catch (InvalidDataException invalid)
                {
                    return Failed("failed", ToolExitCode.UsageOrManifest, [Diagnostic("HXC002", ToolPhase.Usage, ToolFailureCategory.Usage, invalid.Message, "tests", null)], null);
                }
            }

            DateTimeOffset deadlineUtc = requestedAt + deadline.Timeout;
            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
            }

            startupBound.CancelAfter(CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow));
            ToolDiagnostic? aspire = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                _options.AspireCommand,
                TimeSpan.FromTicks(Math.Min(_options.AspireProbeTimeout.Ticks, Math.Max(1, CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow).Ticks))),
                startupBound.Token).ConfigureAwait(false);
            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
            }

            if (aspire is not null)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, [aspire], null);
            }

            CompositionPrerequisiteResult prerequisites = await CompositionPrerequisiteProbe.ProbeAsync(
                _options.DaprHome,
                _options.DockerCommand,
                startupBound.Token,
                _options.SelectedTest?.DaprRuntimeVersion).ConfigureAwait(false);
            if (!prerequisites.IsAvailable)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, prerequisites.Diagnostics, null);
            }

            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
            }

            if (!Path.IsPathFullyQualified(_options.AppHostAssemblyPath) || !File.Exists(_options.AppHostAssemblyPath))
            {
                return Failed(
                    "unavailable",
                    ToolExitCode.PrerequisiteUnavailable,
                    [Diagnostic("HXR014", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "The Builds-owned AppHost is not built.", "apphost", "Build Hexalith.Builds.Module.AppHost before retrying.")],
                    null);
            }

            // The AppHost's assembly metadata names the DCP, dashboard and project paths of the machine that built it.
            ToolDiagnostic? aspireHostUnavailable = CompositionAspireHostPaths.TryResolve(
                _options.AspirePackagesRoot ?? CompositionAspireHostPaths.DefaultPackagesRoot(),
                CompositionToolchainPins.AspireAppHostSdkVersion,
                CompositionAspireHostPaths.CurrentRuntimeIdentifier(),
                out CompositionAspireHostPaths? aspireHost);
            if (aspireHostUnavailable is not null)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, [aspireHostUnavailable], null);
            }

            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));

            // A rejected pinned catalog must not run descriptor child code.
            if (_options.SourceMapping is { Mode: WorkspaceMode.Package } packageMapping)
            {
                runId = CompositionRunPlanFactory.NewRunId();
                _ = await InitializeStateAsync(runId, manifestHash, requestedAt, deadline, startupBound.Token).ConfigureAwait(false);
                provisioningLease = await OpenOutcomeLockAsync(Path.Combine(DiagnosticsDirectory(runId), "provisioning.lock"), startupBound.Token).ConfigureAwait(false);
                preparedWorkspace = WorkspaceFor(runId);
                CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
                CompositionWorkspace.CreatePrivateDirectory(preparedWorkspace);
                await SourceMappingMaterializer.WriteAsync(packageMapping, preparedWorkspace, startupBound.Token).ConfigureAwait(false);
            }

            IReadOnlyList<CompositionRunModule> modules;
            IReadOnlyList<CompositionRunUiMarker> markers;
            string? platformModel = null;
            string? mcpCliTool = null;
            if (platformComposition)
            {
                SourceMapping mapping = _options.SourceMapping ?? throw new InvalidOperationException("Platform v2 requires a resolved source mapping.");
                runId ??= CompositionRunPlanFactory.NewRunId();
                _ = await InitializeStateAsync(runId, manifestHash, requestedAt, deadline, startupBound.Token).ConfigureAwait(false);
                provisioningLease ??= await OpenOutcomeLockAsync(Path.Combine(DiagnosticsDirectory(runId), "provisioning.lock"), startupBound.Token).ConfigureAwait(false);
                preparedWorkspace ??= WorkspaceFor(runId);
                CompositionWorkspace.CreatePrivateDirectory(preparedWorkspace);
                await SourceMappingMaterializer.WriteAsync(mapping, preparedWorkspace, startupBound.Token).ConfigureAwait(false);
                if (PlatformManifestCommand.BuildsMcpCli(activeModule, mapping))
                {
                    // McpCli is a run-scoped tool, not a server. A missing checkout or project raises
                    // WorkspaceMappingException (HXW004), which refuses the run before any resource starts.
                    try
                    {
                        mcpCliTool = await (_options.McpCliBuildOverride?.Invoke(mapping, preparedWorkspace, startupBound.Token)
                            ?? PlatformManifestCommand.BuildMcpCliAsync(mapping, preparedWorkspace, deadlineUtc, startupBound.Token)).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException exception)
                    {
                        IReadOnlyList<ToolDiagnostic> refusal = [Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "McpCli run-scoped build failed: " + exception.Message, PlatformManifestCommand.McpCliProjectPath(mapping), "Correct the named McpCli build input; no resources were started.")];
                        return await FailAndTearDownAsync(runId, null, manifestHash, CompositionRunStatus.Failed, refusal, ToolExitCode.TopologyOrLifecycle, requestedAt, deadline, [], null).ConfigureAwait(false);
                    }

                    if (DateTimeOffset.UtcNow >= deadlineUtc)
                    {
                        return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
                    }
                }

                try
                {
                    platformModel = await (_options.PlatformModelBuildOverride?.Invoke(mapping, preparedWorkspace, startupBound.Token)
                        ?? PlatformManifestCommand.BuildModelAsync(mapping, preparedWorkspace, deadlineUtc, startupBound.Token)).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                {
                    IReadOnlyList<ToolDiagnostic> refusal = [Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, PlatformManifestCommand.BoundedOutput(exception.Message, string.Empty), Path.Combine(mapping.PlatformIdentity?.PlatformPath ?? mapping.Entries.Single(entry => entry.Identity == "Hexalith.Platform").Path!, "src", "Hexalith.Platform.Composition", "Hexalith.Platform.Composition.csproj"), "Correct the named model-build input; no resources were started.")];
                    return await FailAndTearDownAsync(runId, null, manifestHash, CompositionRunStatus.Failed, refusal, ToolExitCode.TopologyOrLifecycle, requestedAt, deadline, [], null).ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= deadlineUtc)
                {
                    return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
                }

                modules = [];
                markers = [];
            }
            else
            {
                ExecutableDescriptorLoadResult descriptors = await ExecutableDescriptorLoader.LoadAsync(
                    manifest,
                    manifestPath,
                    _options.DescriptorChildEntryAssemblyPath,
                    startupBound.Token,
                    _options.SourceMapping).ConfigureAwait(false);
                if (!descriptors.IsValid)
                {
                    bool unavailable = descriptors.Diagnostics[0].Category == ToolFailureCategory.PrerequisiteUnavailable;
                    ToolExitCode descriptorExit = ToolExitCode.UsageOrManifest;
                    if (unavailable)
                    {
                        descriptorExit = ToolExitCode.PrerequisiteUnavailable;
                    }
                    else if (descriptors.Diagnostics[0].Category == ToolFailureCategory.TopologyOrLifecycle)
                    {
                        descriptorExit = ToolExitCode.TopologyOrLifecycle;
                    }

                    return await RefuseUnstartedAsync(descriptors.Diagnostics, descriptorExit, unavailable ? "unavailable" : "failed").ConfigureAwait(false);
                }

                (modules, markers) = CompositionRunPlanFactory.Bind(manifest, descriptors);
                if (_options.SourceMapping is { } mapping)
                {
                    CompositionRunModule? unmapped = modules.FirstOrDefault(module => !mapping.ContainsProject(module.ProjectPath));
                    if (unmapped is not null)
                    {
                        IReadOnlyList<ToolDiagnostic> unmappedDiagnostics =
                        [
                            Diagnostic(
                                "HXW006",
                                ToolPhase.Topology,
                                ToolFailureCategory.TopologyOrLifecycle,
                                $"Module project '{unmapped.ProjectPath}' is outside the active module and mapped source roots.",
                                unmapped.ProjectPath,
                                "Use a project in the active module or a mapped source root."),
                        ];
                        return await RefuseUnstartedAsync(unmappedDiagnostics, ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
                    }
                }
            }

            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));
            if (platformComposition)
            {
                PlatformManifestValidationResult current = PlatformManifestValidator.Validate([manifestPath], startupBound.Token);
                if (!current.IsValid)
                {
                    return await RefuseUnstartedAsync(current.Diagnostics, ToolExitCode.UsageOrManifest).ConfigureAwait(false);
                }

                string checkedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));
                if (!string.Equals(manifestHash, checkedHash, StringComparison.Ordinal))
                {
                    IReadOnlyList<ToolDiagnostic> changed = [Diagnostic("HXP020", ToolPhase.Manifest, ToolFailureCategory.Manifest, $"Platform declaration '{manifestPath}' changed while the run plan was being prepared.", manifestPath, "Retry with a stable declaration file.")];
                    return await RefuseUnstartedAsync(changed, ToolExitCode.UsageOrManifest).ConfigureAwait(false);
                }
            }

            runId ??= CompositionRunPlanFactory.NewRunId();
            CompositionRunState issued = await InitializeStateAsync(runId, manifestHash, requestedAt, deadline, startupBound.Token).ConfigureAwait(false);
            provisioningLease ??= await OpenOutcomeLockAsync(Path.Combine(DiagnosticsDirectory(runId), "provisioning.lock"), startupBound.Token).ConfigureAwait(false);
            string workspace = preparedWorkspace ?? WorkspaceFor(runId);
            if (preparedWorkspace is null)
            {
                CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
                CompositionWorkspace.CreatePrivateDirectory(workspace);
                if (_options.SourceMapping is not null)
                {
                    await SourceMappingMaterializer.WriteAsync(_options.SourceMapping, workspace, startupBound.Token).ConfigureAwait(false);
                }
            }

            CompositionRunPlan plan = CompositionRunPlanFactory.Create(
                runId,
                workspace,
                prerequisites.DaprHome!,
                CompositionPortAllocator.AllocateOwned(Math.Max(1, modules.Count), runId, _options.StateDirectory, !platformComposition && _options.EnableSecondEventStoreInstance),
                modules,
                markers,
                _options.SourceMapping,
                platformComposition ? [Path.GetFullPath(manifestPath)] : [],
                activeModule,
                platformModel);
            plan = plan with
            {
                AttemptId = issued.AttemptId!,
                EnvironmentId = issued.EnvironmentId!,
                SelectedTest = _options.SelectedTest,
                SelectedTests = _options.SelectedTests,
                ResourceClaimStateDirectory = _options.StateDirectory,
                RetainOnFailure = _options.RetainOnFailure,
                EffectiveDeadline = deadline,
                StartupDeadlineUtc = deadlineUtc,
                DebugAttach = _options.DebugAttach,
                StartupTasks = platformComposition ? CompositionStartupTasks.Select(manifestPath, closure, _options.SourceMapping) : [],
                PlatformDeclarationHash = platformComposition ? manifestHash : plan.PlatformDeclarationHash,
                CompositionContentIdentity = closure is null ? null : TestEnvironment.TestEnvironmentDescriptorWriter.CompositionIdentity(closure, _options.SelectedTest),
                McpCliToolPath = mcpCliTool,
            };

            await CompositionDaprComponentRenderer.WriteAsync(plan, startupBound.Token).ConfigureAwait(false);
            string planPath = Path.Combine(workspace, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, startupBound.Token).ConfigureAwait(false);
            string signingKey = CompositionSigningKey.Create();
            IReadOnlyDictionary<string, string> aspireHostEnvironment = aspireHost!.CreateEnvironment(workspace);
            if (platformComposition)
            {
                Dictionary<string, string> preflightEnvironment = new(aspireHostEnvironment, StringComparer.Ordinal)
                {
                    [CompositionEnvironment.PlanPath] = planPath,
                    [CompositionEnvironment.SigningKey] = signingKey,
                    [CompositionEnvironment.RunId] = plan.RunId,
                    [CompositionEnvironment.DaprRuntimePath] = plan.DaprHome,
                    ["DOTNET_ENVIRONMENT"] = "Development",
                };
                CompositionProcessResult preflight = await CompositionProcess.RunAsync(
                    CompositionProcess.CreateStartInfo(
                        DotnetHostPath(),
                        ["exec", _options.AppHostAssemblyPath, "--validate-platform"],
                        Path.GetDirectoryName(_options.AppHostAssemblyPath),
                        preflightEnvironment),
                    CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow),
                    startupBound.Token).ConfigureAwait(false);
                if (preflight.TimedOut)
                {
                    return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
                }

                if (preflight.ExitCode != 0)
                {
                    ToolDiagnostic refusal = Diagnostic("HXP020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, PlatformManifestCommand.BoundedOutput(preflight.Output, preflight.ErrorOutput), "platform", "Supply the named owner input; no resources were started.");
                    return await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Failed, refusal, ToolExitCode.TopologyOrLifecycle, requestedAt, deadline).ConfigureAwait(false);
                }
            }

            if (DateTimeOffset.UtcNow >= deadlineUtc)
            {
                return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
            }

            if (await HasCleanupRequestAsync(runId, cancellationToken).ConfigureAwait(false))
            {
                ToolDiagnostic stopped = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owned run was explicitly stopped during startup.", "down", null);
                return await FailAndTearDownAsync(runId, null, manifestHash, CompositionRunStatus.Cancelled, stopped, ToolExitCode.Cancelled, requestedAt, deadline).ConfigureAwait(false);
            }

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, null, null, startupBound.Token).ConfigureAwait(false);
            appHost = StartAppHost(plan, planPath, signingKey, aspireHostEnvironment);
            if (appHost is null)
            {
                return await FailAndTearDownAsync(
                    runId,
                    null,
                    manifestHash,
                    CompositionRunStatus.Failed,
                    Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The Builds-owned AppHost could not be started.", "apphost", "Check the local .NET runtime and retry."),
                    ToolExitCode.TopologyOrLifecycle,
                    requestedAt,
                    deadline).ConfigureAwait(false);
            }

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, appHost.Id, StartTime(appHost), startupBound.Token).ConfigureAwait(false);
            return await WaitForReadinessAsync(plan, appHost, signingKey, manifestHash, requestedAt, deadline, deadlineUtc, null, _options.SelectedExecutionOwnerToken ?? cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_options.SelectedTest is not null && _options.SelectedExecutionOwnerToken is { IsCancellationRequested: false })
            {
                return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
            }

            ToolDiagnostic cancelled = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The invocation was cancelled; bounded teardown ran.", "cancellation", null);
            return runId is null
                ? Failed("cancelled", ToolExitCode.Cancelled, [cancelled], null)
                : await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Cancelled, cancelled, ToolExitCode.Cancelled, requestedAt, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (startupBound.IsCancellationRequested)
        {
            return await ReportStartupTimeoutAsync(requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false);
        }
        catch (InvalidDataException exception) when (runId is null)
        {
            return Failed(
                "failed",
                ToolExitCode.TopologyOrLifecycle,
                [Diagnostic("HXP020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, PlatformManifestCommand.BoundedOutput(exception.Message, string.Empty), manifestPath, "Correct the authoritative owner declaration.")],
                null);
        }
        catch (WorkspaceMappingException exception) when (runId is not null && appHost is null)
        {
            if (_options.SelectedTest is not null)
            {
                return await FailAndTearDownAsync(runId, null, manifestHash, CompositionRunStatus.Failed, exception.Diagnostics, ToolExitCode.PrerequisiteUnavailable, requestedAt, deadline, [], null).ConfigureAwait(false);
            }

            IReadOnlyList<ToolDiagnostic> diagnostics = await CleanupPreparedWorkspaceAsync(preparedWorkspace, exception.Diagnostics).ConfigureAwait(false);
            bool workspaceRemains = diagnostics.Any(diagnostic => diagnostic.RuleId == "HXW009");
            if (!workspaceRemains)
            {
                _ = StateStore.Delete(runId);
            }
            else if (await StateStore.TryReadAsync(runId, CancellationToken.None).ConfigureAwait(false) is { } tracked)
            {
                await StateStore.WriteAsync(tracked with { CleanupDisposition = "incomplete", RemainingResources = new CompositionRunResources([], [], true) { WorkspaceIds = ["workspace:" + runId] } }, CancellationToken.None).ConfigureAwait(false);
            }

            return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, diagnostics, workspaceRemains ? runId : null);
        }
        catch (CompositionResourceClaimConflictException conflict) when (runId is not null)
        {
            ToolDiagnostic blocked = Diagnostic("HXR031", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, conflict.Message, "resources", "Stop the named owning run or choose independent resources; attachment is never implicit.");
            return await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Failed, blocked, ToolExitCode.TopologyOrLifecycle, requestedAt, deadline).ConfigureAwait(false);
        }
        catch (Exception exception) when (runId is not null && exception is not OutOfMemoryException)
        {
            // Once a run identity exists, resources may exist: every other failure (I/O, port allocation,
            // an inner timeout) is a lifecycle failure that must run bounded teardown instead of leaking the run.
            return await FailAndTearDownAsync(
                runId,
                appHost,
                manifestHash,
                CompositionRunStatus.Failed,
                Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run failed before every resource was healthy; bounded teardown ran.", "apphost", "Inspect the run prerequisites and retry."),
                ToolExitCode.TopologyOrLifecycle,
                requestedAt,
                deadline).ConfigureAwait(false);
        }
        finally
        {
            if (provisioningLease is not null)
            {
                await provisioningLease.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Tears down a run started by this engine instance, stopping its AppHost gracefully first.
    /// </summary>
    /// <param name="session">The ready session.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The teardown outcome.</returns>
    /// <remarks>
    /// Once started, teardown uses an internal bound and is not aborted by the caller's cancellation.
    /// </remarks>
    public async Task<CompositionDownResult> DownAsync(CompositionRunSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource bound = TeardownBound();
        return await DownCoreAsync(session.RunId, bound.Token, session: session).ConfigureAwait(false);
    }

    /// <summary>
    /// Idempotently tears down one run by identity: it stops the recorded AppHost, removes every container
    /// and process tagged with the run, deletes the run workspace, and removes the run state.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The teardown outcome.</returns>
    /// <remarks>
    /// Once started, teardown uses an internal bound and is not aborted by the caller's cancellation.
    /// </remarks>
    public async Task<CompositionDownResult> DownAsync(string runId, CancellationToken cancellationToken)
    {
        if (!CompositionRunPlanFactory.IsRunId(runId))
        {
            ToolDiagnostic usage = Diagnostic("HXR024", ToolPhase.Usage, ToolFailureCategory.Usage, "The run identity is malformed.", "runId", "Use the 32-character run identity returned by the runner.");
            return new CompositionDownResult(
                new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Usage, ToolFailureCategory.Usage, usage.RuleId, ToolExitCode.UsageOrManifest), [usage]),
                new CompositionRunResources([], []));
        }

        cancellationToken.ThrowIfCancellationRequested();
        CompositionRunState? state;
        try
        {
            state = await ReadOwnedStateAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            return IncompleteDown(runId);
        }

        using CancellationTokenSource bound = TeardownBound(state?.SelectedTest);
        CompositionDownResult down = await DownCoreAsync(runId, bound.Token, state?.RemainingResources ?? state?.TestRun?.RemainingResources).ConfigureAwait(false);
        string resultPath = Path.Combine(DiagnosticsDirectory(runId), "test-result.json");
        return down with { Result = down.Result with { TestResultPath = File.Exists(resultPath) ? resultPath : null } };
    }

    /// <summary>Preserves an expired startup request before mapping produced a run identity.</summary>
    /// <param name="requestedAt">The original lifecycle request time.</param>
    /// <param name="deadline">The validated effective startup deadline.</param>
    /// <returns>The preserved startup timeout result.</returns>
    internal Task<CompositionStartResult> ReportSetupTimeoutAsync(DateTimeOffset requestedAt, CompositionEffectiveDeadline deadline) =>
        ReportStartupTimeoutAsync(requestedAt, deadline, null, string.Empty, null);

    /// <summary>Stops an owned selected session using the command's single stopping budget.</summary>
    /// <param name="session">The owned session.</param>
    /// <param name="stoppingToken">The original stopping deadline.</param>
    /// <returns>A truthful bounded cleanup result.</returns>
    internal Task<CompositionDownResult> DownWithinBudgetAsync(CompositionRunSession session, CancellationToken stoppingToken) =>
        DownCoreAsync(session.RunId, stoppingToken, new CompositionRunResources([], ProjectProcessIds(session.Readiness.Resources), true) { WorkspaceIds = ["workspace:" + session.RunId] }, session);

    /// <summary>Records owner cancellation before serializing attachment withdrawal, preserving the first terminal outcome.</summary>
    /// <param name="runId">The exact owner identity.</param>
    /// <param name="cancellationToken">The stopping allowance.</param>
    /// <returns>The durable cancellation task.</returns>
    /// <exception cref="IOException">Verified owner state is unavailable.</exception>
    internal async Task RecordOwnerCancellationAsync(string runId, CancellationToken cancellationToken)
    {
        FileStream held = await OpenOutcomeLockAsync(Path.Combine(DiagnosticsDirectory(runId), "outcome.lock"), cancellationToken).ConfigureAwait(false);
        await using (held.ConfigureAwait(false))
        {
            CompositionRunState state = await ReadOwnedStateAsync(runId, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Owner cancellation cannot be recorded without verified ownership.");

            if (state.CleanupDisposition != "complete")
            {
                await StateStore.WriteAsync(state with { Status = CompositionRunStatus.Cancelled, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Initializes a returned session only while no exact cleanup has claimed or completed its owner.</summary>
    /// <param name="issued">The original session identities.</param>
    /// <param name="cancellationToken">The finite metadata allowance.</param>
    /// <returns>Whether execution may still use this owner.</returns>
    /// <exception cref="IOException">The already-issued owner metadata cannot be verified.</exception>
    internal async Task<bool> EnsureIssuedStateAsync(CompositionRunState issued, CancellationToken cancellationToken)
    {
        if (await HasCleanupRequestAsync(issued.RunId, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        // Already-issued owners require no fallback write and must not consume the callback's lock allowance.
        CompositionRunState? tracked = await CompositionDocumentStore.ReadIfExistsAsync<CompositionRunState>(StateStore.PathFor(issued.RunId), cancellationToken).ConfigureAwait(false);
        if (tracked is not null)
        {
            return IsOwnedState(tracked, issued.RunId)
                ? tracked.CleanupDisposition != "complete" && !await HasCleanupRequestAsync(issued.RunId, cancellationToken).ConfigureAwait(false)
                : throw new IOException("The issued owner cannot be verified.");
        }

        string directory = DiagnosticsDirectory(issued.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream outcomeLock = await OpenOutcomeLockAsync(Path.Combine(directory, "outcome.lock"), cancellationToken).ConfigureAwait(false);
        await using (outcomeLock.ConfigureAwait(false))
        {
            ModuleTestRunRecord? terminal = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), cancellationToken).ConfigureAwait(false);
            if (await HasCleanupRequestAsync(issued.RunId, cancellationToken).ConfigureAwait(false) || terminal?.CleanupDisposition == "complete")
            {
                return false;
            }

            if (await CompositionDocumentStore.ReadIfExistsAsync<CompositionRunState>(StateStore.PathFor(issued.RunId), cancellationToken).ConfigureAwait(false) is null)
            {
                await StateStore.WriteAsync(issued, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }
    }

    /// <summary>Recovers the committed first observation without rerunning optional publication work.</summary>
    /// <param name="runId">The exact owning run.</param>
    /// <param name="cancellationToken">The independent finite metadata allowance.</param>
    /// <param name="waitForLock">Whether to wait for concurrent finalization within the metadata allowance.</param>
    /// <returns>The committed observation when available.</returns>
    /// <exception cref="IOException">The recorded owner cannot be verified.</exception>
    internal async Task<ModuleTestRunRecord?> ReadCommittedOutcomeAsync(string runId, CancellationToken cancellationToken, bool waitForLock = false)
    {
        string directory = DiagnosticsDirectory(runId);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        FileStream outcomeLock = await OpenOutcomeLockAsync(Path.Combine(directory, "outcome.lock"), cancellationToken, waitForLock).ConfigureAwait(false);
        await using (outcomeLock.ConfigureAwait(false))
        {
            ModuleTestRunRecord? record = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), cancellationToken).ConfigureAwait(false);
            return record is null || ValidOutcome(record, runId) ? record : throw new IOException("The recorded first outcome cannot be verified.");
        }
    }

    /// <summary>Immediately records the first terminal outcome and updates only its cleanup disposition thereafter.</summary>
    /// <param name="runId">The owned run.</param>
    /// <param name="resources">Actual observed resource readiness.</param>
    /// <param name="result">The terminal result.</param>
    /// <param name="disposition">The resource disposition.</param>
    /// <param name="remaining">The observed remainder or uncertainty.</param>
    /// <param name="cancellationToken">The durable write token.</param>
    /// <param name="native">The optional safe native test report.</param>
    /// <param name="waitForLock">Whether non-stopping callers may wait briefly for the outcome lock.</param>
    /// <param name="executionTiming">The runner-held execution observation; child-writable timing files are never reread.</param>
    /// <param name="environmentBindings">The immutable validated pre-launch bindings held by the runner.</param>
    /// <param name="terminalCause">The observed first cause, when more precise than the status.</param>
    /// <param name="cleanupDisposition">The latest actual cleanup state.</param>
    /// <param name="cleanupAttemptId">The issued cleanup attempt identity.</param>
    /// <param name="testObservation">The original identity observation held by the runner.</param>
    /// <param name="archiveMetadata">Whether optional metadata copies may consume this allowance.</param>
    /// <returns>The first terminal record.</returns>
    /// <exception cref="IOException">Tracking for an already completed cleanup could not be finalized.</exception>
    internal async Task<ModuleTestRunRecord?> RecordTestOutcomeAsync(string runId, IReadOnlyList<CompositionResourceReadiness> resources, ToolCommandResult result, string disposition, CompositionRunResources remaining, CancellationToken cancellationToken, NativeTestExecutionResult? native = null, bool waitForLock = true, ModuleTestExecutionTiming? executionTiming = null, System.Text.Json.JsonElement? environmentBindings = null, string? terminalCause = null, string? cleanupDisposition = null, string? cleanupAttemptId = null, CompositionTestObservation? testObservation = null, bool archiveMetadata = true)
    {
        CompositionRunState? state = await ReadOwnedStateAsync(runId, cancellationToken).ConfigureAwait(false);
        ModuleTestSelection? selection = _options.SelectedTest ?? state?.SelectedTest;
        if (selection is null)
        {
            return null;
        }

        string directory = DiagnosticsDirectory(runId);
        string path = Path.Combine(directory, "selected-test.json");
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream outcomeLock = await OpenOutcomeLockAsync(Path.Combine(directory, "outcome.lock"), cancellationToken, waitForLock).ConfigureAwait(false);
        await using (outcomeLock.ConfigureAwait(false))
        {
            state = await ReadOwnedStateAsync(runId, cancellationToken).ConfigureAwait(false) ?? state;
            ModuleTestRunRecord? prior = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(path, cancellationToken).ConfigureAwait(false) ?? state?.TestRun;
            if (prior is not null && !ValidOutcome(prior, runId))
            {
                throw new IOException("The recorded first outcome cannot be verified.");
            }

            if (prior is null && result.Outcome.ExitCode == ToolExitCode.Success && _options.SelectedExecutionOwnerToken is { IsCancellationRequested: true })
            {
                ToolDiagnostic withdrawn = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owner withdrew before terminal completion was committed.", "cancellation", null);
                result = new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(withdrawn.Phase, withdrawn.Category, withdrawn.RuleId, ToolExitCode.Cancelled), [withdrawn]);
            }

            ModuleTestRunRecord record = prior ?? new ModuleTestRunRecord(runId, runId, selection, _options.SourceMapping?.ContentHash, _options.SourceMapping?.PlatformIdentity?.Commit, _options.SourceMapping?.PlatformIdentity?.CheckoutCommit, testObservation?.ObservedCandidateHash, testObservation?.ObservedBackendIdentity, false, resources, result.Status, result.Outcome, DateTimeOffset.UtcNow, disposition, remaining)
            {
                FailureRetained = _options.RetainOnFailure && result.Outcome.ExitCode is not (ToolExitCode.Success or ToolExitCode.Cancelled),
                ExecutionTiming = executionTiming,
                EnvironmentBindings = environmentBindings,
                TerminalCause = terminalCause,
            };

            // Explicit cleanup is monotonic. Publish both completion records before removing discoverable tracking.
            bool completing = cleanupDisposition == "complete";
            if (record.CleanupDisposition != "complete")
            {
                if (remaining.Unverified)
                {
                    CompositionRunResources known = record.RemainingResources;
                    remaining = remaining with
                    {
                        ContainerIds = [.. remaining.ContainerIds.Concat(known.ContainerIds).Distinct(StringComparer.Ordinal)],
                        ProcessIds = [.. remaining.ProcessIds.Concat(known.ProcessIds).Distinct()],
                        WorkspaceIds = [.. remaining.WorkspaceIds.Concat(known.WorkspaceIds).Distinct(StringComparer.Ordinal)],
                        ClaimIds = [.. remaining.ClaimIds.Concat(known.ClaimIds).Distinct(StringComparer.Ordinal)],
                    };
                }

                if (cleanupDisposition == "complete" && !remaining.IsEmpty)
                {
                    cleanupDisposition = "incomplete";
                    completing = false;
                }

                record = record with { ResourceDisposition = disposition, RemainingResources = remaining, CleanupDisposition = cleanupDisposition ?? record.CleanupDisposition, CleanupAttemptId = cleanupAttemptId ?? record.CleanupAttemptId };
            }

            try
            {
                record = record with
                {
                    Resources = resources.Count > 0 ? resources : record.Resources,
                    Selections = _options.SelectedTests.Count > 0 ? _options.SelectedTests : record.Selections,
                    ExecutionTiming = executionTiming ?? record.ExecutionTiming,
                    EnvironmentBindings = record.EnvironmentBindings ?? environmentBindings,
                    DiagnosticRuleIds = prior?.DiagnosticRuleIds ?? Rules(result.Diagnostics),
                    LaterDiagnosticRuleIds = prior is null ? [] : [.. prior.LaterDiagnosticRuleIds.Concat(Rules(result.Diagnostics)).Where(rule => rule != "HXC130" && !prior.DiagnosticRuleIds.Contains(rule, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)],
                };
                record = record with { TerminalCause = record.TerminalCause ?? TestEnvironment.TestResultWriter.Cause(record) };

                // First cause/time are committed before any optional metadata/report archive or teardown.
                await CompositionDocumentStore.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
                state ??= CompositionRunPlanFactory.CreateInitialState(runId, WorkspaceFor(runId), selection.ManifestHash, selection.RequestedAtUtc) with
                {
                    SelectedTest = selection,
                    AttemptId = BindingIdentity(environmentBindings, "attemptId") ?? CompositionRunPlanFactory.NewRunId(),
                    EnvironmentId = BindingIdentity(environmentBindings, "environmentId") ?? CompositionRunPlanFactory.NewRunId(),
                    ExecutionContext = System.Text.Json.JsonSerializer.SerializeToElement(TestEnvironment.TestEnvironmentDescriptorWriter.ExecutionContext()),
                };
                if (!File.Exists(Path.Combine(directory, "owner.json")))
                {
                    await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "owner.json"), state, cancellationToken).ConfigureAwait(false);
                }

                if (state.AttemptId is not null && state.EnvironmentId is not null && state.CreatedAt is not null && state.ExecutionContext is not null)
                {
                    _ = await TestEnvironment.TestResultWriter.WriteAsync(directory, state, record, cancellationToken).ConfigureAwait(false);
                }

                if (native?.ReportBytes is { } reportBytes)
                {
                    string reportHash = Convert.ToHexStringLower(SHA256.HashData(reportBytes));
                    await CompositionDocumentStore.WriteBytesAsync(Path.Combine(directory, "native-" + reportHash + ".trx"), reportBytes, cancellationToken).ConfigureAwait(false);
                    await CompositionDocumentStore.WriteBytesAsync(Path.Combine(directory, "native.trx"), reportBytes, cancellationToken).ConfigureAwait(false);
                    record = record with
                    {
                        ReportHash = reportHash.ToUpperInvariant(),
                        TestCounts = native.Report is { } report ? new RunEvidence.ModuleRunTestCounts(true, report.Total, report.Passed, report.Failed, report.Skipped) : null,
                    };
                }

                if (native?.ObservationBytes is { } observationBytes)
                {
                    string detail = Path.Combine(directory, "functional-observations.json");
                    string observationHash = Convert.ToHexStringLower(SHA256.HashData(observationBytes));
                    await CompositionDocumentStore.WriteBytesAsync(Path.Combine(directory, "functional-observations-" + observationHash + ".json"), observationBytes, cancellationToken).ConfigureAwait(false);
                    await CompositionDocumentStore.WriteBytesAsync(detail, observationBytes, cancellationToken).ConfigureAwait(false);
                    if (!OperatingSystem.IsWindows())
                    {
                        File.SetUnixFileMode(detail, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                    }

                    record = record with { ObservationHash = observationHash.ToUpperInvariant() };
                }

                await CompositionDocumentStore.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
                if (state.AttemptId is not null && state.EnvironmentId is not null && state.CreatedAt is not null && state.ExecutionContext is not null)
                {
                    _ = await TestEnvironment.TestResultWriter.WriteAsync(directory, state, record, cancellationToken).ConfigureAwait(false);
                }

                if (record.CleanupDisposition != "complete")
                {
                    await StateStore.WriteAsync(state with { SelectedTest = record.Selection, TestRun = record, RemainingResources = record.RemainingResources, CleanupDisposition = record.CleanupDisposition }, cancellationToken).ConfigureAwait(false);
                }

                if (archiveMetadata)
                {
                    await CopySelectedMetadataAsync(WorkspaceFor(runId), directory, cancellationToken).ConfigureAwait(false);
                }

                if (completing && !StateStore.Delete(runId))
                {
                    record = record with { ResourceDisposition = "remaining", CleanupDisposition = "incomplete", LaterDiagnosticRuleIds = [.. record.LaterDiagnosticRuleIds.Append("HXR023").Distinct(StringComparer.Ordinal)], RemainingResources = record.RemainingResources with { Unverified = true, DiscoveryFailures = [.. record.RemainingResources.DiscoveryFailures, "state-finalization"] } };
                    await CompositionDocumentStore.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
                    _ = await TestEnvironment.TestResultWriter.WriteAsync(directory, state, record, cancellationToken).ConfigureAwait(false);
                    await StateStore.WriteAsync(state with { TestRun = record, SelectedTest = record.Selection, RemainingResources = record.RemainingResources, CleanupDisposition = record.CleanupDisposition }, cancellationToken).ConfigureAwait(false);
                }

                return record;
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                if (completing && prior?.CleanupDisposition != "complete" && state is not null)
                {
                    // Active tracking survives every incomplete completion publication, including process interruption.
                    using CancellationTokenSource recovery = new(TimeSpan.FromSeconds(5));
                    ModuleTestRunRecord retry = record with { ResourceDisposition = "remaining", CleanupDisposition = "incomplete", LaterDiagnosticRuleIds = [.. record.LaterDiagnosticRuleIds.Append("HXT008").Distinct(StringComparer.Ordinal)], RemainingResources = record.RemainingResources with { Unverified = true, DiscoveryFailures = [.. record.RemainingResources.DiscoveryFailures, "cleanup-verification"] } };
                    try
                    {
                        await StateStore.WriteAsync(state with { TestRun = retry, SelectedTest = retry.Selection, RemainingResources = retry.RemainingResources, CleanupDisposition = "incomplete" }, recovery.Token).ConfigureAwait(false);
                    }
                    catch (Exception trackingFailure) when (IsTeardownFailure(trackingFailure))
                    {
                        // A failed active-state write must not suppress the independent durable cleanup correction.
                    }

                    try
                    {
                        await CompositionDocumentStore.WriteAsync(path, retry, recovery.Token).ConfigureAwait(false);
                        if (state.AttemptId is not null && state.EnvironmentId is not null && state.CreatedAt is not null && state.ExecutionContext is not null)
                        {
                            _ = await TestEnvironment.TestResultWriter.WriteAsync(directory, state, retry, recovery.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception retryFailure) when (IsTeardownFailure(retryFailure))
                    {
                        // Active tracking already records the bounded retry even if publication is still unavailable.
                    }
                }

                throw;
            }
        }
    }

    /// <summary>Observes exact owned leftovers without deleting resources or claiming missing discovery succeeded.</summary>
    /// <param name="runId">The owner identity.</param>
    /// <param name="cancellationToken">The existing observation bound.</param>
    /// <returns>Known resources and explicit discovery failures.</returns>
    internal async Task<CompositionRunResources> ObserveOwnedResourcesAsync(string runId, CancellationToken cancellationToken)
    {
        CompositionRunResources resources;
        try
        {
            resources = await Scanner.FindAsync(runId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            resources = new CompositionRunResources([], [], true) { DiscoveryFailures = ["runtime-resources"] };
        }

        try
        {
            _ = File.GetAttributes(WorkspaceFor(runId));
            resources = resources with { WorkspaceIds = ["workspace:" + runId] };
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // Only confirmed absence removes the known workspace identity.
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            resources = resources with { Unverified = true, DiscoveryFailures = [.. resources.DiscoveryFailures, "workspaces-or-claims"] };
        }

        try
        {
            bool unverifiedClaims = false;
            IReadOnlyList<string> claims = StateStore.Claims.FindOwned(runId, () => unverifiedClaims = true);
            return resources with { ClaimIds = claims, Unverified = resources.Unverified || unverifiedClaims, DiscoveryFailures = [.. resources.DiscoveryFailures, .. unverifiedClaims ? new[] { "claims" } : []] };
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            return resources with { Unverified = true, DiscoveryFailures = [.. resources.DiscoveryFailures, "workspaces-or-claims"] };
        }
    }

    private static async Task<FileStream> OpenOutcomeLockAsync(string path, CancellationToken cancellationToken, bool waitForLock = true)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            bound.Token.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (waitForLock)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), bound.Token).ConfigureAwait(false);
            }
        }
    }

    private static CompositionDownResult WithdrawalRefusal(CompositionRunState? state, bool recordingFailed = false)
    {
        CompositionRunResources remaining = state?.RemainingResources ?? state?.TestRun?.RemainingResources ?? new CompositionRunResources([], [], true);
        ToolDiagnostic diagnostic = Diagnostic("HXR037", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Attachment withdrawal could not be durably recorded; no cleanup started.", "down", "Retry down for the same owner run identity.");
        return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [.. recordingFailed ? new[] { Diagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Owner cleanup state could not be durably recorded; cleanup remains pending.", "evidence", null) } : [], diagnostic]) { RemainingResources = remaining }, remaining);
    }

    private static ToolDiagnostic RecordingFailure() => Diagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Owned cleanup continued, but its terminal metadata or diagnostics could not be retained within the stopping budget.", "evidence", null);

    private static void KillOwnedWithoutWaiting(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // The scanner still discloses whether exact owned resources can be verified.
        }
    }

    private static ToolDiagnostic Diagnostic(string ruleId, ToolPhase phase, ToolFailureCategory category, string message, string field, string? hint) =>
        new(ruleId, phase, category, message, field, hint);

    private static async Task<IReadOnlyList<ToolDiagnostic>> CleanupPreparedWorkspaceAsync(string? workspace, IReadOnlyList<ToolDiagnostic> diagnostics)
    {
        return workspace is null || await CompositionWorkspace.TryDeleteAsync(workspace, CancellationToken.None).ConfigureAwait(false)
            ? diagnostics
            : [.. diagnostics, Diagnostic("HXW009", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, $"Private catalog workspace '{workspace}' could not be removed after refusal.", workspace, "Remove this private workspace before retrying."),];
    }

    private static bool IsTeardownFailure(Exception exception) =>
        exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException
            or Win32Exception or AggregateException or System.Text.Json.JsonException;

    private static CompositionStartResult Failed(string status, ToolExitCode exitCode, IReadOnlyList<ToolDiagnostic> diagnostics, string? runId)
    {
        ToolDiagnostic first = diagnostics[0];
        return new CompositionStartResult(
            new ToolCommandResult(status, ToolOutcome.Passed().Fail(first.Phase, first.Category, first.RuleId, exitCode), diagnostics),
            runId,
            null);
    }

    private static DateTimeOffset? StartTime(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static Process? TryGetRecordedProcess(int processId, DateTimeOffset? startedAt)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            DateTimeOffset? actual = StartTime(process);

            // A recorded identity whose start time differs belongs to a reused process identity: never signal it.
            if (!process.HasExited && startedAt is not null && actual is not null
                && (actual.Value - startedAt.Value).Duration() < TimeSpan.FromSeconds(2))
            {
                Process recorded = process;
                process = null;
                return recorded;
            }

            return null;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or AggregateException)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static string DotnetHostPath()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string candidate = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static bool IsValidReadiness(CompositionRunPlan plan, CompositionReadiness readiness) =>
        string.Equals(readiness.Schema, CompositionReadiness.SupportedSchema, StringComparison.Ordinal)
        && string.Equals(readiness.RunId, plan.RunId, StringComparison.Ordinal)
        && readiness.EventStoreEndpoint is { IsLoopback: true }
        && (plan.UiMarkers.Count == 0 || readiness.UiEndpoint is { IsLoopback: true })
        && readiness.Resources.Count > 0
        && readiness.Resources.All(resource => string.Equals(resource.State, "Healthy", StringComparison.Ordinal));

    private static async Task<IReadOnlyList<ToolDiagnostic>> ReadStartupFailuresAsync(string runId, string workspace, CancellationToken cancellationToken)
    {
        CompositionStartupFailure? failure = await CompositionDocumentStore
            .TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(workspace), cancellationToken)
            .ConfigureAwait(false);
        if (failure is null)
        {
            return [];
        }

        if (!string.Equals(failure.Schema, CompositionStartupFailure.SupportedSchema, StringComparison.Ordinal)
            || !string.Equals(failure.RunId, runId, StringComparison.Ordinal)
            || failure.RuleId is not ("HXR021" or "HXR025" or "HXR026" or "HXR027" or "HXR028" or "HXR031")
            || !IsSafeFailureText(failure.Resource, 128)
            || !IsSafeFailureText(failure.AppId, 63)
            || !IsSafeFailureText(failure.Status, 120)
            || failure.UnreadyResources.Any(resource => !IsSafeFailureText(resource.Name, 128) || !IsSafeFailureText(resource.Detail ?? resource.State, 120))
            || (failure.FailingTask is not null && !IsSafeFailureText(failure.FailingTask, 128))
            || (failure.RuleId == "HXR031" && !CompositionRunPlanFactory.IsRunId(failure.BlockingRunId)))
        {
            return [Diagnostic("HXR022", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The AppHost failure document does not describe this run.", "readiness", "Rebuild the Builds-owned AppHost and retry.")];
        }

        if (failure.RuleId == "HXR031")
        {
            return [Diagnostic("HXR031", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, $"An exclusive runner resource is held by run '{failure.BlockingRunId}'.", "resources", "Stop the named owner or use independent resources; attachment is never implicit.")];
        }

        if (failure.UnreadyResources.Count > 0)
        {
            string task = string.IsNullOrWhiteSpace(failure.FailingTask) ? string.Empty : $" task='{failure.FailingTask}'";
            return
            [
                .. failure.UnreadyResources.Select(resource => Diagnostic(
                    failure.RuleId,
                    ToolPhase.Topology,
                    ToolFailureCategory.TopologyOrLifecycle,
                    $"Unready resource '{resource.Name}' last='{resource.Detail ?? resource.State}'{task}.",
                    resource.Name,
                    "Inspect the preserved startup diagnostics.")),
            ];
        }

        string message = string.IsNullOrWhiteSpace(failure.FailingTask)
            ? $"Readiness stalled at resource '{failure.Resource}' (app ID '{failure.AppId}', status '{failure.Status}')."
            : $"Startup task '{failure.FailingTask}' failed before the deadline (resource '{failure.Resource}', status '{failure.Status}').";
        return [Diagnostic(failure.RuleId, ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, message, failure.FailingTask ?? failure.Resource, "Inspect the preserved startup diagnostics.")];
    }

    private static bool IsSafeFailureText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.' or ':' or ';' or '=' or ' ');

    /// <summary>Reads an original issued identity without trusting incomplete held bindings.</summary>
    /// <param name="environmentBindings">The optional original descriptor bindings.</param>
    /// <param name="name">The identity property to read.</param>
    /// <returns>The original identity, or null when no descriptor was issued.</returns>
    /// <exception cref="InvalidDataException">Original owner bindings are invalid.</exception>
    private static string? BindingIdentity(System.Text.Json.JsonElement? environmentBindings, string name) => environmentBindings switch
    {
        null => null,
        { } bindings when bindings.ValueKind == System.Text.Json.JsonValueKind.Object
            && bindings.TryGetProperty("run", out System.Text.Json.JsonElement run)
            && run.ValueKind == System.Text.Json.JsonValueKind.Object
            && run.TryGetProperty(name, out System.Text.Json.JsonElement identity)
            && identity.ValueKind == System.Text.Json.JsonValueKind.String
            && CompositionRunPlanFactory.IsRunId(identity.GetString()) => identity.GetString(),
        _ => throw new InvalidDataException("The original owner bindings cannot be verified."),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The nonblocking reader is disposed asynchronously in the unconditional finally before advancing to the next file.")]
    private static async Task CopySelectedMetadataAsync(string workspace, string destination, CancellationToken cancellationToken)
    {
        foreach ((string source, string name) in new[]
        {
            (Path.Combine(workspace, "plan.json"), "selected-plan.json"),
            (Path.Combine(workspace, "startup-task-results.json"), "startup-task-results.json"),
            (Path.Combine(workspace, "readiness.json"), "selected-readiness.json"),
            (Path.Combine(workspace, "source-mapping", "mapping.json"), "selected-source-mapping.json"),
        })
        {
            FileStream? stream = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!SelectedNativeTestReport.IsOwnedRegularPath(source, workspace))
                {
                    continue;
                }

                stream = SelectedNativeTestReport.OpenWithoutBlocking(source);
                long length = stream.CanSeek ? stream.Length : 0;
                if (length is <= 0 or > 4194304)
                {
                    continue;
                }

                byte[] bytes = new byte[(int)length];
                await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(bytes);
                if (SafeMetadata(document.RootElement))
                {
                    await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(destination, name), destination, bytes, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or System.Text.Json.JsonException)
            {
                // An absent, unreadable or unsafe observation is never invented.
            }
            finally
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
            }
        }

        // Descriptor, timing and inspected native reports are archived directly from trusted snapshots.
        // Never reread child-writable entry files during terminal recording or cleanup.
    }

    private static bool SafeMetadata(System.Text.Json.JsonElement value) => value.ValueKind switch
    {
        System.Text.Json.JsonValueKind.String => !ManifestSecretDetector.ContainsSecret(value.GetString()!),
        System.Text.Json.JsonValueKind.Array => value.EnumerateArray().All(SafeMetadata),
        System.Text.Json.JsonValueKind.Object => value.EnumerateObject().All(property => !ManifestSecretDetector.ContainsSecret(property.Name) && SafeMetadata(property.Value)),
        System.Text.Json.JsonValueKind.Number or System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False or System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined => true,
        _ => false,
    };

    private static void CopyDiagnostic(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(source);
                if (stream.CanSeek && stream.Length is > 0 and <= 4194304)
                {
                    byte[] bytes = new byte[(int)stream.Length];
                    stream.ReadExactly(bytes);
                    string text = System.Text.Encoding.UTF8.GetString(bytes);
                    bool safe = !ManifestSecretDetector.ContainsSecret(text);
                    if (safe && Path.GetExtension(source) == ".json")
                    {
                        using System.Text.Json.JsonDocument metadata = System.Text.Json.JsonDocument.Parse(bytes);
                        safe = SafeMetadata(metadata.RootElement);
                    }

                    if (safe)
                    {
                        File.WriteAllBytes(destination, bytes);
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            // The summary still names the diagnostics directory when a single copy fails.
        }
    }

    private static IReadOnlyList<ToolDiagnostic> WithDiagnosticsLocation(
        IReadOnlyList<ToolDiagnostic> diagnostics,
        CompositionEffectiveDeadline deadline,
        string path)
    {
        string suffix = " " + CompositionStartupDeadlineResolver.Describe(deadline) + " diagnostics='" + path + "'.";
        return [.. diagnostics.Select(diagnostic => Locate(diagnostic, suffix))];
    }

    private static ToolDiagnostic Locate(ToolDiagnostic diagnostic, string suffix) =>
        diagnostic.Message.Contains("diagnostics='", StringComparison.Ordinal)
            ? diagnostic
            : diagnostic with { Message = diagnostic.Message.TrimEnd() + suffix };

    private static async Task<IReadOnlyList<CompositionResourceReadiness>> ReadProgressResourcesAsync(CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        CompositionReadinessProgress? progress = await CompositionDocumentStore
            .TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(plan.Workspace), cancellationToken)
            .ConfigureAwait(false);
        return progress is null || !string.Equals(progress.Schema, CompositionReadinessProgress.SupportedSchema, StringComparison.Ordinal) || !string.Equals(progress.RunId, plan.RunId, StringComparison.Ordinal) ? [] : [.. progress.Resources.Select((resource, index) => resource with { Name = IsSafeFailureText(resource.Name, 128) ? resource.Name : $"resource-{index + 1}", Detail = IsSafeFailureText(resource.Detail ?? resource.State, 120) ? resource.Detail : "detail withheld: unsafe or oversized text", }),];
    }

    private static IReadOnlyList<int> ProjectProcessIds(IReadOnlyList<CompositionResourceReadiness> resources) =>
        [.. resources.Where(resource => resource.ProcessId is > 0).Select(resource => resource.ProcessId!.Value).Distinct().Order()];

    private static IReadOnlyList<string> Rules(IReadOnlyList<ToolDiagnostic> diagnostics) => [.. diagnostics.Select(diagnostic => diagnostic.RuleId).Where(rule => rule.Length is >= 5 and <= 8 && rule.StartsWith("HX", StringComparison.Ordinal) && rule.All(char.IsAsciiLetterOrDigit)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static bool ValidOutcome(ModuleTestRunRecord record, string runId) => CompositionRunStateStore.ValidOutcome(record, runId);

    private async Task<bool> HasCleanupRequestAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(DiagnosticsDirectory(runId), "cleanup-request.json");
        if (!SelectedNativeTestReport.IsOwnedParent(path, StateStore.Directory))
        {
            throw new IOException("The cleanup request is outside the owned state path policy.");
        }

        using System.Text.Json.JsonDocument? request = await CompositionDocumentStore.ReadIfExistsAsync<System.Text.Json.JsonDocument>(path, cancellationToken).ConfigureAwait(false);
        bool valid = request?.RootElement is { ValueKind: System.Text.Json.JsonValueKind.Object } root
            && root.TryGetProperty("runId", out System.Text.Json.JsonElement owner)
            && owner.ValueKind == System.Text.Json.JsonValueKind.String && owner.GetString() == runId
            && root.TryGetProperty("attemptId", out System.Text.Json.JsonElement attempt)
            && attempt.ValueKind == System.Text.Json.JsonValueKind.String && CompositionRunPlanFactory.IsRunId(attempt.GetString());
        return request is not null && !valid ? throw new IOException("The cleanup request cannot be verified.") : valid;
    }

    private CompositionDownResult IncompleteDown(string runId, CompositionRunResources? known = null, bool recordingFailed = false)
    {
        CompositionRunResources remaining = (known ?? new CompositionRunResources([], [], true)) with
        {
            Unverified = true,
            WorkspaceIds = [.. (known?.WorkspaceIds ?? []).Concat(Directory.Exists(WorkspaceFor(runId)) ? ["workspace:" + runId] : []).Distinct(StringComparer.Ordinal)],
            DiscoveryFailures = [.. known?.DiscoveryFailures ?? [], "cleanup-verification"],
        };
        ToolDiagnostic diagnostic = Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned cleanup could not be verified within its existing allowance.", "down", "Retry down for the same run identity.");
        ToolDiagnostic[] diagnostics = recordingFailed ? [RecordingFailure(), diagnostic] : [diagnostic];
        ToolDiagnostic first = diagnostics[0];
        return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(first.Phase, first.Category, first.RuleId, recordingFailed ? ToolExitCode.EvidenceSchemaOrPolicy : ToolExitCode.TopologyOrLifecycle), diagnostics) { RemainingResources = remaining }, remaining);
    }

    private async Task<bool> TryRecordStoppingOutcomeAsync(string runId, IReadOnlyList<CompositionResourceReadiness> resources, ToolCommandResult result, string disposition, CompositionRunResources remaining, List<ToolDiagnostic> diagnostics, string? terminalCause = null)
    {
        try
        {
            using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
            return await RecordTestOutcomeAsync(runId, resources, result, disposition, remaining, metadata.Token, waitForLock: false, terminalCause: terminalCause, archiveMetadata: false).ConfigureAwait(false) is not null;
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            diagnostics.Add(RecordingFailure());
            return false;
        }
    }

    private bool IsOwnedState(CompositionRunState? state, string runId) =>
        state is { Schema: CompositionRunState.SupportedSchema } && state.RunId == runId && state.Workspace == WorkspaceFor(runId)
        && (state.AttemptId is null || (CompositionRunPlanFactory.IsRunId(state.AttemptId) && state.AttemptId != runId && state.AttemptId != state.EnvironmentId))
        && (state.EnvironmentId is null || (CompositionRunPlanFactory.IsRunId(state.EnvironmentId) && state.EnvironmentId != runId))
        && CompositionRunStateStore.ValidReadiness(state) && CompositionRunStateStore.ValidResources(state.RemainingResources)
        && (state.TestRun is null || ValidOutcome(state.TestRun, runId));

    private async Task<CompositionRunState?> ReadOwnedStateAsync(string runId, CancellationToken cancellationToken)
    {
        CompositionRunState? active = await StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        if (active is not null && (!CompositionRunStateStore.ValidReadiness(active) || !CompositionRunStateStore.ValidResources(active.RemainingResources) || (active.TestRun is { } activeTerminal && !ValidOutcome(activeTerminal, runId))))
        {
            throw new IOException("Recorded resource ownership cannot be verified.");
        }

        CompositionRunState? state = active;
        if (!IsOwnedState(state, runId))
        {
            state = await CompositionDocumentStore.ReadIfExistsAsync<CompositionRunState>(Path.Combine(DiagnosticsDirectory(runId), "owner.json"), cancellationToken).ConfigureAwait(false);
            state ??= await CompositionDocumentStore.ReadIfExistsAsync<CompositionRunState>(StateStore.PathFor(runId), cancellationToken).ConfigureAwait(false);

            if (state is null && active is null)
            {
                return null;
            }

            if (!IsOwnedState(state, runId))
            {
                throw new IOException("Durable owner metadata cannot be verified.");
            }
        }

        ModuleTestRunRecord? terminal = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(DiagnosticsDirectory(runId), "selected-test.json"), cancellationToken).ConfigureAwait(false);
        terminal = terminal is null || ValidOutcome(terminal, runId) ? terminal : throw new IOException("The recorded first outcome cannot be verified.");
        return terminal is null ? state : state! with { SelectedTest = terminal.Selection, TestRun = terminal, RemainingResources = terminal.RemainingResources, CleanupDisposition = terminal.CleanupDisposition };
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The acquired cleanup lock transfers into the configured await-using lifetime after guarded acquisition.")]
    private async Task<CompositionDownResult> DownCoreAsync(string runId, CancellationToken bound, CompositionRunResources? known = null, CompositionRunSession? session = null)
    {
        FileStream? cleanupLock = null;
        bool recordingFailed = false;
        try
        {
            string directory = DiagnosticsDirectory(runId);
            CompositionWorkspace.CreatePrivateDirectory(directory);
            cleanupLock = await OpenOutcomeLockAsync(Path.Combine(directory, "cleanup.lock"), bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            if (exception is OperationCanceledException || bound.IsCancellationRequested)
            {
                return IncompleteDown(runId, known);
            }

            // Unavailable bookkeeping must not prevent exact-owner teardown within the existing allowance.
            recordingFailed = true;
        }

        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanupLifetime = (cleanupLock ?? Stream.Null).ConfigureAwait(false);
        try
        {
            return await DownOwnedCoreAsync(runId, recordingFailed, session, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            return IncompleteDown(runId, known, recordingFailed);
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The acquired provisioning lock transfers immediately into the configured await-using lifetime after guarded acquisition.")]
    private async Task<CompositionDownResult> DownOwnedCoreAsync(string runId, bool recordingFailed, CompositionRunSession? session, CancellationToken bound)
    {
        List<ToolDiagnostic> bookkeeping = recordingFailed ? [RecordingFailure()] : [];
        CompositionRunState? state = await ReadOwnedStateAsync(runId, bound).ConfigureAwait(false);
        if (state is not null && (state.Schema != CompositionRunState.SupportedSchema || state.RunId != runId))
        {
            ToolDiagnostic invalid = Diagnostic("HXR024", ToolPhase.Usage, ToolFailureCategory.Usage, "Recorded ownership does not match the requested run.", "runId", null);
            return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(invalid.Phase, invalid.Category, invalid.RuleId, ToolExitCode.UsageOrManifest), [invalid]), new CompositionRunResources([], [], true) { DiscoveryFailures = ["ownership"] });
        }

        string attemptId = CompositionRunPlanFactory.NewRunId();
        string directory = DiagnosticsDirectory(runId);
        if (state is not null)
        {
            // This marker survives state/workspace deletion and prevents late callbacks restoring retention.
            try
            {
                await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "cleanup-request.json"), new { runId, attemptId }, bound).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                return WithdrawalRefusal(state, recordingFailed: true);
            }
        }

        if (state?.SelectedTest is not null)
        {
            ToolDiagnostic cancelled = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owned test run was stopped.", "down", null);
            ToolCommandResult terminal = new("cancelled", ToolOutcome.Passed().Fail(cancelled.Phase, cancelled.Category, cancelled.RuleId, ToolExitCode.Cancelled), [cancelled]);
            try
            {
                CompositionRunResources lastKnown = state.RemainingResources ?? state.TestRun?.RemainingResources ?? new CompositionRunResources([], [], true);
                _ = await RecordTestOutcomeAsync(runId, state.ReadinessResults, terminal, state.TestRun?.ResourceDisposition ?? "unknown", lastKnown with { Unverified = true }, bound, waitForLock: false, cleanupDisposition: "in-progress", cleanupAttemptId: attemptId).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                return WithdrawalRefusal(state, recordingFailed: true);
            }
        }

        try
        {
            if (state is not null && state.SelectedTest is null)
            {
                await StateStore.WriteAsync(state with { CleanupDisposition = "in-progress", UpdatedAt = DateTimeOffset.UtcNow }, bound).ConfigureAwait(false);
            }

            IReadOnlyList<CompositionAttachment> withdrawn = await CompositionAttachmentStore.WithdrawAsync(directory, runId, "developer-stop", DateTimeOffset.UtcNow, bound).ConfigureAwait(false);
            bookkeeping.AddRange(CompositionAttachmentStore.WithdrawalDiagnostics(withdrawn));
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            return WithdrawalRefusal(state);
        }

        FileStream? provisioning = null;
        try
        {
            CompositionWorkspace.CreatePrivateDirectory(directory);
            provisioning = await OpenOutcomeLockAsync(Path.Combine(directory, "provisioning.lock"), bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            if (bound.IsCancellationRequested)
            {
                CompositionDownResult pending = IncompleteDown(runId, state?.RemainingResources ?? state?.TestRun?.RemainingResources, recordingFailed: bookkeeping.Any(item => item.Category != ToolFailureCategory.None));
                return pending with { Result = pending.Result with { Diagnostics = [.. bookkeeping, .. pending.Result.Diagnostics] } };
            }

            if (!Directory.Exists(directory))
            {
                bookkeeping.Add(RecordingFailure());
            }
            else
            {
                CompositionRunResources pending = await ObserveOwnedResourcesAsync(runId, bound).ConfigureAwait(false);
                ToolDiagnostic stalled = Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned provisioning is still stopping; cleanup remains pending.", "down", "Retry down for the same run identity.");
                return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(stalled.Phase, stalled.Category, stalled.RuleId, ToolExitCode.TopologyOrLifecycle), [.. bookkeeping, stalled]) { RemainingResources = pending }, pending);
            }
        }

        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable provisioningLifetime = (provisioning ?? Stream.Null).ConfigureAwait(false);
        try
        {
            state = await ReadOwnedStateAsync(runId, bound).ConfigureAwait(false) ?? state;
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            CompositionDownResult pending = IncompleteDown(runId, state?.RemainingResources ?? state?.TestRun?.RemainingResources, recordingFailed: bookkeeping.Any(item => item.Category != ToolFailureCategory.None));
            return pending with { Result = pending.Result with { Diagnostics = [.. bookkeeping, .. pending.Result.Diagnostics] } };
        }

        if (session is not null)
        {
            try
            {
                await StopAppHostAsync(session.AppHost, bound).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                KillOwnedWithoutWaiting(session.AppHost);
            }

            session.AppHost.Dispose();
        }

        if (state?.AppHostProcessId is int processId && TryGetRecordedProcess(processId, state.AppHostStartedAt) is Process recorded)
        {
            using (recorded)
            {
                try
                {
                    await SignalStopAsync(recorded, bound, state.SelectedTest is not null).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTeardownFailure(exception))
                {
                    KillOwnedWithoutWaiting(recorded);
                }
            }
        }

        ToolDiagnostic? stopFailure = null;
        try
        {
            stopFailure = (await ReadStartupFailuresAsync(runId, WorkspaceFor(runId), bound).ConfigureAwait(false)).FirstOrDefault(diagnostic => diagnostic.RuleId == "HXR028");
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            bookkeeping.Add(RecordingFailure());
        }

        (CompositionRunResources remaining, bool workspaceRemoved) = await TearDownAsync(runId, null, bound).ConfigureAwait(false);
        if (state?.RemainingResources is { } previous)
        {
            remaining = remaining with
            {
                ContainerIds = (remaining.DiscoveryFailures.Contains("containers", StringComparer.Ordinal) || remaining.DiscoveryFailures.Contains("cleanup-verification", StringComparer.Ordinal)) ? [.. remaining.ContainerIds.Concat(previous.ContainerIds).Distinct(StringComparer.Ordinal)] : remaining.ContainerIds,
                ProcessIds = (remaining.DiscoveryFailures.Contains("processes", StringComparer.Ordinal) || remaining.DiscoveryFailures.Contains("cleanup-verification", StringComparer.Ordinal)) ? [.. remaining.ProcessIds.Concat(previous.ProcessIds).Distinct()] : remaining.ProcessIds,
                ClaimIds = remaining.DiscoveryFailures.Contains("claims", StringComparer.Ordinal) ? [.. remaining.ClaimIds.Concat(previous.ClaimIds).Distinct(StringComparer.Ordinal)] : remaining.ClaimIds,
            };
        }

        bool complete = remaining.IsEmpty && workspaceRemoved && bookkeeping.All(item => item.Category == ToolFailureCategory.None) && stopFailure is null;
        bool stateRemoved = false;
        if (state?.SelectedTest is not null)
        {
            try
            {
                ToolDiagnostic[] incompleteDiagnostics = complete ? [] : [Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned cleanup is incomplete or unverifiable.", "down", "Retry down for the same run identity.")];
                ToolCommandResult preserved = new("cancelled", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled), [.. bookkeeping, .. stopFailure is null ? [] : new ToolDiagnostic[] { stopFailure }, .. incompleteDiagnostics]);
                using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                ModuleTestRunRecord? final = await RecordTestOutcomeAsync(runId, state.ReadinessResults, preserved, complete ? "removed" : "remaining", remaining, metadata.Token, waitForLock: false, cleanupDisposition: complete ? "complete" : "incomplete", cleanupAttemptId: attemptId, archiveMetadata: false).ConfigureAwait(false);
                stateRemoved = final?.CleanupDisposition == "complete";
                if (complete && !stateRemoved)
                {
                    remaining = final?.RemainingResources ?? remaining;
                }

                complete = complete && stateRemoved;
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                bookkeeping.Add(RecordingFailure());
                complete = false;
            }
        }

        if (complete && state?.SelectedTest is null)
        {
            try
            {
                stateRemoved = StateStore.Delete(runId);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                bookkeeping.Add(RecordingFailure());
            }
        }

        if (!stateRemoved && state is not null && state.SelectedTest is null)
        {
            try
            {
                CompositionRunState latest = await StateStore.TryReadAsync(runId, bound).ConfigureAwait(false) ?? state;
                await StateStore.WriteAsync(latest with { RemainingResources = remaining, CleanupDisposition = "incomplete", UpdatedAt = DateTimeOffset.UtcNow }, bound).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                bookkeeping.Add(RecordingFailure());
            }
        }

        if (stateRemoved && complete && bookkeeping.All(item => item.Category == ToolFailureCategory.None))
        {
            ToolDiagnostic completed = Diagnostic("HXI001", ToolPhase.Cleanup, ToolFailureCategory.None, "Runner-owned invocation cleanup completed.", "down", null);
            return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), [.. bookkeeping, completed]) { RemainingResources = remaining }, remaining);
        }

        if (stopFailure is not null && bookkeeping.All(item => item.Category == ToolFailureCategory.None) && remaining.IsEmpty && workspaceRemoved)
        {
            return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(stopFailure.Phase, stopFailure.Category, stopFailure.RuleId, ToolExitCode.TopologyOrLifecycle), [.. bookkeeping, stopFailure]) { RemainingResources = remaining }, remaining);
        }

        ToolDiagnostic incomplete = Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Owned cleanup is incomplete or unverifiable.", "down", "Retry down for the same run identity.");
        ToolDiagnostic first = bookkeeping.FirstOrDefault(item => item.Category != ToolFailureCategory.None) ?? stopFailure ?? incomplete;
        return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(first.Phase, first.Category, first.RuleId, first.RuleId == "HXT008" ? ToolExitCode.EvidenceSchemaOrPolicy : ToolExitCode.TopologyOrLifecycle), [.. bookkeeping, .. stopFailure is null ? [] : new ToolDiagnostic[] { stopFailure }, incomplete]) { RemainingResources = remaining }, remaining);
    }

    private async Task<CompositionStartResult> WaitForReadinessAsync(
        CompositionRunPlan plan,
        Process appHost,
        string signingKey,
        string manifestHash,
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline,
        DateTimeOffset deadlineUtc,
        Func<CancellationToken, Task>? beforeDeadlineObservation,
        CancellationToken cancellationToken)
    {
        bool tasksCompleted = false;
        List<CompositionStartupTaskResult> taskResults = [];
        string? expiredTask = null;
        string? refreshId = null;
        while (CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow) > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await HasCleanupRequestAsync(plan.RunId, cancellationToken).ConfigureAwait(false))
            {
                ToolDiagnostic stopped = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owned run was explicitly stopped during startup.", "down", null);
                return await FailAndTearDownAsync(plan.RunId, appHost, manifestHash, CompositionRunStatus.Cancelled, stopped, ToolExitCode.Cancelled, requestedAt, deadline).ConfigureAwait(false);
            }

            IReadOnlyList<ToolDiagnostic> startupFailure = await ReadStartupFailuresAsync(plan.RunId, plan.Workspace, cancellationToken).ConfigureAwait(false);
            if (startupFailure.Count > 0)
            {
                return await FailAndTearDownAsync(
                    plan.RunId,
                    appHost,
                    manifestHash,
                    CompositionRunStatus.Failed,
                    startupFailure,
                    ToolExitCode.TopologyOrLifecycle,
                    requestedAt,
                    deadline,
                    [],
                    null).ConfigureAwait(false);
            }

            CompositionReadiness? readiness = await CompositionDocumentStore
                .TryReadAsync<CompositionReadiness>(plan.ReadinessPath, cancellationToken)
                .ConfigureAwait(false);
            if (readiness is not null)
            {
                if (!IsValidReadiness(plan, readiness))
                {
                    return await FailAndTearDownAsync(
                        plan.RunId,
                        appHost,
                        manifestHash,
                        CompositionRunStatus.Failed,
                        Diagnostic("HXR022", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The AppHost readiness document does not describe this run.", "readiness", "Rebuild the Builds-owned AppHost and retry."),
                        ToolExitCode.TopologyOrLifecycle,
                        requestedAt,
                        deadline).ConfigureAwait(false);
                }

                if (!tasksCompleted)
                {
                    (string? failingTask, bool deadlineElapsed, string? expired) = await CompositionStartupTasks.RunOwnedDetailedAsync(plan.StartupTasks, deadlineUtc, plan.RunId, cancellationToken, taskResults).ConfigureAwait(false);
                    await CompositionDocumentStore.WriteAsync(Path.Combine(plan.Workspace, "startup-task-results.json"), taskResults, CancellationToken.None).ConfigureAwait(false);
                    if (deadlineElapsed)
                    {
                        expiredTask = expired;
                        break;
                    }

                    if (failingTask is not null)
                    {
                        string? executableError = plan.StartupTasks.FirstOrDefault(task => task.ModuleId + "/" + task.Name == failingTask)?.ExecutableError;
                        string cause = executableError is null ? string.Empty : " Executable: " + executableError;
                        return await FailAndTearDownAsync(
                            plan.RunId,
                            appHost,
                            manifestHash,
                            CompositionRunStatus.Failed,
                            [Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, $"Startup task '{failingTask}' failed before the deadline.{cause}", failingTask, "Inspect the preserved startup diagnostics.")],
                            ToolExitCode.TopologyOrLifecycle,
                            requestedAt,
                            deadline,
                            readiness.Resources,
                            failingTask).ConfigureAwait(false);
                    }

                    tasksCompleted = true;
                    if (plan.StartupTasks.Count > 0)
                    {
                        refreshId = CompositionRunPlanFactory.NewRunId();
                        await CompositionDocumentStore.WriteAsync(
                            CompositionControlRequest.PathFor(plan.Workspace),
                            new CompositionControlRequest(CompositionControlRequest.SupportedSchema, plan.RunId, refreshId, "refresh-readiness"),
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                }

                if (refreshId is not null && !string.Equals(readiness.RefreshId, refreshId, StringComparison.Ordinal))
                {
                    if (appHost.HasExited)
                    {
                        return await FailAndTearDownAsync(plan.RunId, appHost, manifestHash, CompositionRunStatus.Failed, Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The Builds-owned AppHost exited before fresh module readiness.", "apphost", "Inspect the preserved startup diagnostics."), ToolExitCode.TopologyOrLifecycle, requestedAt, deadline).ConfigureAwait(false);
                    }

                    await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (appHost.HasExited)
                {
                    return await FailAndTearDownAsync(
                        plan.RunId,
                        appHost,
                        manifestHash,
                        CompositionRunStatus.Failed,
                        Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The Builds-owned AppHost exited before every resource was healthy.", "apphost", "Inspect the run prerequisites and retry."),
                        ToolExitCode.TopologyOrLifecycle,
                        requestedAt,
                        deadline).ConfigureAwait(false);
                }

                if (_options.BeforeReadyCommitOverride is { } beforeReadyCommit)
                {
                    await beforeReadyCommit(cancellationToken).ConfigureAwait(false);
                }

                if (DateTimeOffset.UtcNow >= deadlineUtc)
                {
                    break;
                }

                double duration = (DateTimeOffset.UtcNow - requestedAt).TotalSeconds;
                readiness = readiness with
                {
                    EffectiveDeadlineSeconds = deadline.TimeoutSeconds,
                    EffectiveDeadlineIsOverride = deadline.IsOverride,
                    EffectiveDeadlineJustification = deadline.Justification,
                    EffectiveDeadlineModuleId = deadline.ModuleId,
                    SelectedTest = plan.SelectedTest,
                    StartupDurationSeconds = duration,
                    StartupTaskResults = taskResults,
                };
                await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, readiness, cancellationToken).ConfigureAwait(false);
                string diagnosticsPath = DiagnosticsDirectory(plan.RunId);
                await WriteStateAsync(
                    plan.RunId,
                    CompositionRunStatus.Ready,
                    manifestHash,
                    appHost.Id,
                    StartTime(appHost),
                    cancellationToken,
                    record: new CompositionRunRecord(duration, readiness.Resources, deadline, diagnosticsPath, ProjectProcessIds(readiness.Resources))).ConfigureAwait(false);
                List<ToolDiagnostic> readyDiagnostics =
                [
                    Diagnostic("HXI010", ToolPhase.Topology, ToolFailureCategory.None, "Every run resource reported healthy. " + CompositionStartupDeadlineResolver.Describe(deadline) + ".", "readiness", null),
                ];
                if (plan.DebugAttach)
                {
                    foreach (CompositionResourceReadiness resource in readiness.Resources.Where(resource => resource.Kind == "project").OrderBy(resource => resource.Name, StringComparer.Ordinal))
                    {
                        string message = resource.ProcessId is > 0
                            ? $"Project '{resource.Name}' launcher process id '{resource.ProcessId}'."
                            : $"Project '{resource.Name}' launcher process id is unavailable; attach information is incomplete.";
                        readyDiagnostics.Add(Diagnostic("HXI010", ToolPhase.Topology, ToolFailureCategory.None, message, resource.Name, null));
                    }
                }

                if (plan.McpCliToolPath is { } mcpCliTool)
                {
                    string mcpCliProject = plan.SourceMapping is { } mcpCliMapping ? PlatformManifestCommand.McpCliProjectPath(mcpCliMapping) : "unavailable";
                    readyDiagnostics.Add(Diagnostic("HXI010", ToolPhase.Topology, ToolFailureCategory.None, $"Run-scoped McpCli Debug build '{mcpCliTool}' of '{mcpCliProject}'; launch it under a debugger, and down deletes it.", PlatformManifestCommand.McpCliModule, null));
                }

                return new CompositionStartResult(
                    new ToolCommandResult("ready", ToolOutcome.Passed(), readyDiagnostics),
                    plan.RunId,
                    new CompositionRunSession(plan, readiness, signingKey, appHost));
            }

            if (appHost.HasExited)
            {
                return await FailAndTearDownAsync(
                    plan.RunId,
                    appHost,
                    manifestHash,
                    CompositionRunStatus.Failed,
                    Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The Builds-owned AppHost exited before every resource was healthy.", "apphost", "Inspect the run prerequisites and retry."),
                    ToolExitCode.TopologyOrLifecycle,
                    requestedAt,
                    deadline).ConfigureAwait(false);
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        long? stoppingTimestamp = _options.SelectedTest is null ? null : Stopwatch.GetTimestamp();
        using CancellationTokenSource? observationBound = stoppingTimestamp is null ? null : TeardownBound();
        CancellationToken observationToken = observationBound?.Token ?? CancellationToken.None;
        IReadOnlyList<ToolDiagnostic> recorded;
        IReadOnlyList<CompositionResourceReadiness> observed = [];
        IReadOnlyList<CompositionResourceReadiness> unready = [];
        try
        {
            if (_options.SelectedTest is not null && beforeDeadlineObservation is not null)
            {
                await beforeDeadlineObservation(observationToken).ConfigureAwait(false);
            }

            recorded = await ReadStartupFailuresAsync(plan.RunId, plan.Workspace, observationToken).ConfigureAwait(false);
            observed = await ReadProgressResourcesAsync(plan, observationToken).ConfigureAwait(false);
            unready = [.. observed.Where(resource => !string.Equals(resource.State, "Healthy", StringComparison.Ordinal))];
        }
        catch (Exception exception) when (_options.SelectedTest is not null && IsTeardownFailure(exception))
        {
            recorded =
            [
                Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run resources did not become healthy within the readiness bound.", "readiness", "Inspect the preserved startup diagnostics."),
                RecordingFailure(),
            ];
        }

        if (recorded.Count == 0)
        {
            if (unready.Count > 0)
            {
                recorded =
                [
                    .. unready.Select(resource => Diagnostic(
                        "HXR021",
                        ToolPhase.Topology,
                        ToolFailureCategory.TopologyOrLifecycle,
                        $"Unready resource '{resource.Name}' last='{resource.Detail ?? resource.State}'.",
                        resource.Name,
                        "Inspect the preserved startup diagnostics.")),
                ];
            }
            else
            {
                string message = expiredTask is null
                    ? "The run resources did not become healthy within the readiness bound."
                    : $"The run resources did not become healthy within the readiness bound; startup task '{expiredTask}' had not finished.";
                recorded = [Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, message, expiredTask ?? "readiness", "Inspect the preserved startup diagnostics.")];
            }
        }

        return await FailAndTearDownAsync(
            plan.RunId,
            appHost,
            manifestHash,
            CompositionRunStatus.Failed,
            recorded,
            ToolExitCode.TopologyOrLifecycle,
            requestedAt,
            deadline,
            observed,
            null,
            stoppingTimestamp,
            observationBound,
            startupTimedOut: true).ConfigureAwait(false);
    }

    private Task<CompositionStartResult> FailAndTearDownAsync(
        string runId,
        Process? appHost,
        string manifestHash,
        CompositionRunStatus status,
        ToolDiagnostic diagnostic,
        ToolExitCode exitCode,
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline) =>
        FailAndTearDownAsync(runId, appHost, manifestHash, status, [diagnostic], exitCode, requestedAt, deadline, [], null);

    private async Task<CompositionStartResult> FailAndTearDownAsync(
        string runId,
        Process? appHost,
        string manifestHash,
        CompositionRunStatus status,
        IReadOnlyList<ToolDiagnostic> diagnostics,
        ToolExitCode exitCode,
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline,
        IReadOnlyList<CompositionResourceReadiness> resources,
        string? failingTask,
        long? initialStoppingTimestamp = null,
        CancellationTokenSource? stoppingBound = null,
        bool startupTimedOut = false)
    {
        long stoppingTimestamp = initialStoppingTimestamp ?? Stopwatch.GetTimestamp();
        using CancellationTokenSource? bound = stoppingBound is null ? TeardownBound() : null;
        CancellationToken boundToken = (stoppingBound ?? bound!).Token;
        List<ToolDiagnostic> bookkeeping = [];
        bool terminalPublished = _options.SelectedTest is null;
        string terminalCause = startupTimedOut ? "startup-timeout" : "startup-failure";
        if (status == CompositionRunStatus.Cancelled)
        {
            terminalCause = "run-cancelled";
        }

        if (_options.SelectedTest is not null)
        {
            string terminalStatus = status == CompositionRunStatus.Cancelled ? "cancelled" : "failed";
            ToolCommandResult terminal = Failed(terminalStatus, exitCode, diagnostics, runId).Result;
            terminalPublished = await TryRecordStoppingOutcomeAsync(runId, resources, terminal, _options.RetainOnFailure && status != CompositionRunStatus.Cancelled ? "retained" : "unknown", new CompositionRunResources([], [], Unverified: true), bookkeeping, terminalCause).ConfigureAwait(false);
        }

        // Teardown is bounded and deliberately not cancellable by the caller's (possibly cancelled) token.
        if (resources.Count == 0)
        {
            try
            {
                resources = await ReadObservedResourcesAsync(runId, boundToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                bookkeeping.Add(RecordingFailure());
            }
        }

        string diagnosticsPath;
        try
        {
            diagnosticsPath = await PreserveDiagnosticsAsync(runId, diagnostics[0].RuleId, requestedAt, deadline, resources, failingTask, boundToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            bookkeeping.Add(RecordingFailure());
            diagnosticsPath = DiagnosticsDirectory(runId);
        }

        IReadOnlyList<ToolDiagnostic> located = WithDiagnosticsLocation(diagnostics, deadline, diagnosticsPath);
        bool cleanupRequested = false;
        try
        {
            cleanupRequested = await HasCleanupRequestAsync(runId, boundToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            // Only a verified exact cleanup request can release local failure retention.
        }

        bool retained = _options.RetainOnFailure && status != CompositionRunStatus.Cancelled && !cleanupRequested;
        int? retainedPid = retained && appHost?.HasExited == false ? appHost.Id : null;
        DateTimeOffset? retainedStart = retainedPid is null ? null : StartTime(appHost!);
        if (_options.SelectedTest is not null)
        {
            await CopySelectedMetadataAsync(WorkspaceFor(runId), diagnosticsPath, boundToken).ConfigureAwait(false);
        }

        bool requirePublication = !(_options.ContinuousIntegrationOverride ?? (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)));
        if (!terminalPublished && requirePublication && !retained && appHost is not null)
        {
            try
            {
                await StopAppHostAsync(appHost, boundToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                KillOwnedWithoutWaiting(appHost);
            }
        }

        (CompositionRunResources remaining, bool workspaceRemoved) = retained || (!terminalPublished && requirePublication)
            ? (await ObserveOwnedResourcesAsync(runId, boundToken).ConfigureAwait(false), false)
            : await TearDownAsync(runId, appHost, boundToken).ConfigureAwait(false);
        bool clean = remaining.IsEmpty && workspaceRemoved;
        string ruleId = clean || retained ? located[0].RuleId : "HXR023";
        string observedCleanup = clean ? "complete" : "incomplete";
        try
        {
            await WriteStateAsync(
                runId,
                status,
                manifestHash,
                retainedPid,
                retainedStart,
                boundToken,
                ruleId,
                new CompositionRunRecord((DateTimeOffset.UtcNow - requestedAt).TotalSeconds, resources, deadline, diagnosticsPath, ProjectProcessIds(resources)),
                cleanupDisposition: _options.SelectedTest is null && !retained ? observedCleanup : null,
                remainingResources: remaining).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            // The failure state is best-effort metadata; the returned diagnostics still report the outcome.
        }

        List<ToolDiagnostic> reported = [.. located, .. bookkeeping];
        if (!clean && !retained)
        {
            reported.Add(Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Run teardown left tagged resources behind.", "down", "Retry down for the same run identity."));
        }

        string text = status == CompositionRunStatus.Cancelled ? "cancelled" : "failed";
        CompositionStartResult result = Failed(text, exitCode, reported, runId);
        if (_options.SelectedTest is not null)
        {
            string disposition = clean ? "removed" : "unknown";
            string cleanupDisposition = clean ? "complete" : "incomplete";
            try
            {
                using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                _ = await RecordTestOutcomeAsync(runId, resources, result.Result, retained ? "retained" : disposition, remaining, metadata.Token, terminalCause: terminalCause, cleanupDisposition: retained ? null : cleanupDisposition, cleanupAttemptId: retained ? null : CompositionRunPlanFactory.NewRunId(), archiveMetadata: false).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                bookkeeping.Add(RecordingFailure());
            }

            result = result with { Result = result.Result with { Diagnostics = [.. located, .. bookkeeping, .. reported.Where(diagnostic => diagnostic.RuleId == "HXR023")] } };
        }

        return result with { StoppingTimestamp = _options.SelectedTest is null ? null : stoppingTimestamp };
    }

    /// <summary>
    /// Stops the AppHost, removes every resource tagged with the run, and deletes the workspace without throwing.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="appHost">The AppHost process owned by this engine, if any.</param>
    /// <param name="bound">The internal teardown bound.</param>
    /// <returns>The resources that remain and whether the workspace was removed.</returns>
    private async Task<(CompositionRunResources Remaining, bool WorkspaceRemoved)> TearDownAsync(string runId, Process? appHost, CancellationToken bound)
    {
        if (appHost is not null)
        {
            try
            {
                await StopAppHostAsync(appHost, bound).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                if (_options.SelectedTest is not null)
                {
                    KillOwnedWithoutWaiting(appHost);
                }
            }

            appHost.Dispose();
        }

        CompositionRunResources remaining;
        try
        {
            remaining = await Scanner.RemoveAsync(runId, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            remaining = new CompositionRunResources([], [], Unverified: true) { DiscoveryFailures = ["cleanup-verification"] };
        }

        bool workspaceRemoved;
        try
        {
            workspaceRemoved = await CompositionWorkspace.TryDeleteAsync(WorkspaceFor(runId), bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            workspaceRemoved = false;
        }

        List<string> failures = [.. remaining.DiscoveryFailures];
        if (remaining.IsEmpty && workspaceRemoved)
        {
            try
            {
                StateStore.Claims.ReleaseOwned(runId);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                failures.Add("claim-release");
            }
        }

        IReadOnlyList<string> claims = [];
        try
        {
            bool unverifiedClaims = false;
            claims = StateStore.Claims.FindOwned(runId, () => unverifiedClaims = true);
            if (unverifiedClaims)
            {
                failures.Add("claims");
            }
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            failures.Add("claims");
        }

        remaining = remaining with { WorkspaceIds = workspaceRemoved ? [] : ["workspace:" + runId], ClaimIds = claims, Unverified = remaining.Unverified || failures.Count > 0, DiscoveryFailures = failures };
        return (remaining, workspaceRemoved);
    }

    private CancellationTokenSource TeardownBound(ModuleTestSelection? selection = null) => new((selection ?? _options.SelectedTest) is { } selected
        ? TimeSpan.FromSeconds(selected.StopTimeoutSeconds)
        : (_options.StopTimeout * 2) + TimeSpan.FromMinutes(2));

    private async Task StopAppHostAsync(Process appHost, CancellationToken cancellationToken)
    {
        try
        {
            if (!appHost.HasExited)
            {
                await appHost.StandardInput.WriteLineAsync(CompositionEnvironment.StopCommand.AsMemory(), cancellationToken).ConfigureAwait(false);
                await appHost.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                appHost.StandardInput.Close();
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ObjectDisposedException or Win32Exception)
        {
            // The AppHost already closed its input; fall through to the bounded wait.
        }

        await WaitOrKillAsync(appHost, cancellationToken).ConfigureAwait(false);
    }

    private async Task SignalStopAsync(Process process, CancellationToken cancellationToken, bool selected = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            _ = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)], null, null),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }

        await WaitOrKillAsync(process, cancellationToken, selected).ConfigureAwait(false);
    }

    private async Task WaitOrKillAsync(Process process, CancellationToken cancellationToken, bool selected = false)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_options.StopTimeout);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (selected || _options.SelectedTest is not null)
            {
                KillOwnedWithoutWaiting(process);
            }
            else
            {
                CompositionProcess.Kill(process);
            }
        }
        catch (InvalidOperationException)
        {
            // The process handle was already released: nothing is left to wait for.
        }
    }

    private Process? StartAppHost(CompositionRunPlan plan, string planPath, string signingKey, IReadOnlyDictionary<string, string> aspireHostEnvironment)
    {
        Dictionary<string, string> environment = new(aspireHostEnvironment, StringComparer.Ordinal)
        {
            [CompositionEnvironment.PlanPath] = planPath,
            [CompositionEnvironment.SigningKey] = signingKey,
            [CompositionEnvironment.RunId] = plan.RunId,
            [CompositionEnvironment.DaprRuntimePath] = plan.DaprHome,
            ["ASPIRE_ALLOW_UNSECURED_TRANSPORT"] = "true",
            ["DOTNET_ENVIRONMENT"] = "Development",
        };
        if (plan.SourceMapping is not null)
        {
            foreach (KeyValuePair<string, string> pair in SourceMappingMaterializer.Environment(plan.SourceMapping, plan.Workspace))
            {
                environment[pair.Key] = pair.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(_options.AppHostLogPath))
        {
            environment[CompositionEnvironment.ResourceLogs] = "1";
        }

        if (_options.PersistAfterParentExit)
        {
            environment[CompositionEnvironment.PersistAfterParentExit] = "1";
        }

        ProcessStartInfo start = CompositionProcess.CreateStartInfo(
            DotnetHostPath(),
            ["exec", _options.AppHostAssemblyPath],
            Path.GetDirectoryName(_options.AppHostAssemblyPath),
            environment);
        Process process = new() { StartInfo = start, EnableRaisingEvents = true };
        string preserved = DiagnosticsDirectory(plan.RunId);
        CompositionWorkspace.CreatePrivateDirectory(StateStore.Directory);
        CompositionWorkspace.CreatePrivateDirectory(preserved);
        CompositionDiagnosticMirror.Attach(process, _options.AppHostLogPath, preserved);
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return null;
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            process.Dispose();
            return null;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private string WorkspaceFor(string runId) => Path.Combine(Path.GetFullPath(_options.WorkspaceRoot), runId);

    private string DiagnosticsDirectory(string runId) => Path.Combine(StateStore.Directory, "diagnostics", runId);

    private async Task<CompositionStartResult> ReportStartupTimeoutAsync(
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline,
        string? runId,
        string manifestHash,
        Process? appHost)
    {
        ToolDiagnostic timeout = Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run resources did not become healthy within the readiness bound.", "readiness", "Inspect the preserved startup diagnostics.");
        if (runId is null && _options.SelectedTest is { } selected)
        {
            runId = CompositionRunPlanFactory.NewRunId();
            manifestHash = selected.ManifestHash;
            using CancellationTokenSource recording = new(TimeSpan.FromSeconds(selected.StopTimeoutSeconds));
            _ = await InitializeStateAsync(runId, manifestHash, requestedAt, deadline, recording.Token).ConfigureAwait(false);
        }

        if (runId is null)
        {
            string path;
            List<ToolDiagnostic> diagnostics = [timeout];
            try
            {
                path = await PreserveDiagnosticsAsync(null, timeout.RuleId, requestedAt, deadline, [], null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsTeardownFailure(exception))
            {
                path = Path.Combine(StateStore.Directory, "diagnostics", "before-run");
                diagnostics.Add(RecordingFailure());
            }

            return Failed("failed", ToolExitCode.TopologyOrLifecycle, WithDiagnosticsLocation(diagnostics, deadline, path), null);
        }

        return await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Failed, [timeout], ToolExitCode.TopologyOrLifecycle, requestedAt, deadline, [], null, startupTimedOut: true).ConfigureAwait(false);
    }

    private async Task<string> PreserveDiagnosticsAsync(
        string? runId,
        string ruleId,
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline,
        IReadOnlyList<CompositionResourceReadiness> resources,
        string? failingTask,
        CancellationToken cancellationToken)
    {
        string directory = runId is null
            ? Path.Combine(StateStore.Directory, "diagnostics", "before-run", Guid.NewGuid().ToString("N"))
            : DiagnosticsDirectory(runId);
        CompositionWorkspace.CreatePrivateDirectory(StateStore.Directory);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        if (runId is not null)
        {
            string workspace = WorkspaceFor(runId);
            CopyDiagnostic(Path.Combine(workspace, "apphost-retained.log"), Path.Combine(directory, "apphost-retained.log"));
            CopyDiagnostic(CompositionStartupFailure.PathFor(workspace), Path.Combine(directory, "startup-failure.json"));
            CopyDiagnostic(Path.Combine(workspace, "readiness.json"), Path.Combine(directory, "readiness.json"));
            CopyDiagnostic(CompositionReadinessProgress.PathFor(workspace), Path.Combine(directory, "readiness-progress.json"));
            if (failingTask is null)
            {
                CompositionStartupFailure? failure = await CompositionDocumentStore
                    .TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(workspace), cancellationToken)
                    .ConfigureAwait(false);
                failingTask = failure?.FailingTask;
            }
        }

        await CompositionDocumentStore.WriteAsync(
            Path.Combine(directory, "summary.json"),
            new CompositionStartupReport(
                CompositionStartupReport.SupportedSchema,
                runId,
                ruleId,
                Math.Max(0, (DateTimeOffset.UtcNow - requestedAt).TotalSeconds),
                deadline.TimeoutSeconds,
                deadline.IsOverride,
                deadline.Justification,
                deadline.ModuleId,
                failingTask,
                resources),
            cancellationToken).ConfigureAwait(false);
        return directory;
    }

    private async Task<IReadOnlyList<CompositionResourceReadiness>> ReadObservedResourcesAsync(string runId, CancellationToken cancellationToken)
    {
        string workspace = WorkspaceFor(runId);
        CompositionStartupFailure? failure = await CompositionDocumentStore
            .TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(workspace), cancellationToken)
            .ConfigureAwait(false);
        if (failure?.UnreadyResources.Count > 0)
        {
            return failure.UnreadyResources;
        }

        CompositionReadinessProgress? progress = await CompositionDocumentStore
            .TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(workspace), cancellationToken)
            .ConfigureAwait(false);
        return progress is null || !string.Equals(progress.Schema, CompositionReadinessProgress.SupportedSchema, StringComparison.Ordinal) || !string.Equals(progress.RunId, runId, StringComparison.Ordinal) ? [] : [.. progress.Resources.Where(resource => !string.Equals(resource.State, "Healthy", StringComparison.Ordinal))];
    }

    private async Task<CompositionStartResult> RefusePreparedRunAsync(string? runId, string? workspace, string manifestHash, DateTimeOffset requestedAt, CompositionEffectiveDeadline deadline, IReadOnlyList<ToolDiagnostic> diagnostics, ToolExitCode exit, string status = "failed")
    {
        if (runId is not null && _options.SelectedTest is not null)
        {
            return await FailAndTearDownAsync(runId, null, manifestHash, CompositionRunStatus.Failed, diagnostics, exit, requestedAt, deadline, [], null).ConfigureAwait(false);
        }

        IReadOnlyList<ToolDiagnostic> reported = await CleanupPreparedWorkspaceAsync(workspace, diagnostics).ConfigureAwait(false);
        if (runId is not null)
        {
            bool removed = !reported.Any(diagnostic => diagnostic.RuleId == "HXW009") && StateStore.Delete(runId);
            if (removed)
            {
                RemoveUnstartedStateDirectory(runId);
            }
            else
            {
                using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                CompositionRunState? state = await ReadOwnedStateAsync(runId, metadata.Token).ConfigureAwait(false);
                if (state is not null)
                {
                    await StateStore.WriteAsync(state with { Status = CompositionRunStatus.Failed, RuleId = diagnostics[0].RuleId, CleanupDisposition = "incomplete", RemainingResources = new CompositionRunResources([], [], true) { WorkspaceIds = Directory.Exists(WorkspaceFor(runId)) ? ["workspace:" + runId] : [], DiscoveryFailures = ["state-finalization"] } }, metadata.Token).ConfigureAwait(false);
                }
            }
        }

        return Failed(status, exit, reported, runId);
    }

    /// <summary>Removes diagnostics left by a refusal that never started resources, including an empty state root.</summary>
    /// <param name="runId">The refused run identity.</param>
    private void RemoveUnstartedStateDirectory(string runId)
    {
        try
        {
            string diagnostics = DiagnosticsDirectory(runId);
            if (Directory.Exists(diagnostics))
            {
                Directory.Delete(diagnostics, recursive: true);
            }

            string? diagnosticsRoot = Path.GetDirectoryName(diagnostics);
            if (diagnosticsRoot is not null && Directory.Exists(diagnosticsRoot) && !Directory.EnumerateFileSystemEntries(diagnosticsRoot).Any())
            {
                Directory.Delete(diagnosticsRoot);
            }

            if (Directory.Exists(StateStore.Directory) && !Directory.EnumerateFileSystemEntries(StateStore.Directory).Any())
            {
                Directory.Delete(StateStore.Directory);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Unselected diagnostic archives may remain after a fully cleaned pre-start refusal.
        }
    }

    private async Task<CompositionRunState> InitializeStateAsync(string runId, string manifestHash, DateTimeOffset requestedAt, CompositionEffectiveDeadline deadline, CancellationToken cancellationToken)
    {
        if (await HasCleanupRequestAsync(runId, cancellationToken).ConfigureAwait(false))
        {
            throw new IOException("Exact cleanup already claimed this owner.");
        }

        string directory = DiagnosticsDirectory(runId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream outcomeLock = await OpenOutcomeLockAsync(Path.Combine(directory, "outcome.lock"), cancellationToken).ConfigureAwait(false);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outcomeLifetime = outcomeLock.ConfigureAwait(false);
        CompositionRunState? existing = await ReadOwnedStateAsync(runId, cancellationToken).ConfigureAwait(false);
        if (await HasCleanupRequestAsync(runId, cancellationToken).ConfigureAwait(false) || existing?.CleanupDisposition == "complete")
        {
            throw new IOException("Exact cleanup already claimed this owner.");
        }

        if (existing is not null)
        {
            return existing;
        }

        CompositionRunState initial = CompositionRunPlanFactory.CreateInitialState(runId, WorkspaceFor(runId), manifestHash, requestedAt) with
        {
            SelectedTest = _options.SelectedTest,
            RetainOnFailure = _options.RetainOnFailure,
            StartupDeadlineUtc = requestedAt + deadline.Timeout,
            EffectiveDeadlineSeconds = deadline.TimeoutSeconds,
            EffectiveDeadlineIsOverride = deadline.IsOverride,
            EffectiveDeadlineJustification = deadline.Justification,
            EffectiveDeadlineModuleId = deadline.ModuleId,
            ExecutionContext = _options.SelectedTest is null ? null : System.Text.Json.JsonSerializer.SerializeToElement(TestEnvironment.TestEnvironmentDescriptorWriter.ExecutionContext()),
        };
        await StateStore.WriteAsync(initial, cancellationToken).ConfigureAwait(false);
        await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "owner.json"), initial, cancellationToken).ConfigureAwait(false);
        return initial;
    }

    private async Task WriteStateAsync(
        string runId,
        CompositionRunStatus status,
        string manifestHash,
        int? processId,
        DateTimeOffset? startedAt,
        CancellationToken cancellationToken,
        string? ruleId = null,
        CompositionRunRecord? record = null,
        string? cleanupDisposition = null,
        CompositionRunResources? remainingResources = null)
    {
        CompositionRunState? previous = await StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        ModuleTestRunRecord? terminal = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(DiagnosticsDirectory(runId), "selected-test.json"), cancellationToken).ConfigureAwait(false);
        if (terminal?.CleanupDisposition == "complete")
        {
            return;
        }

        await StateStore.WriteAsync(
            new CompositionRunState(
                CompositionRunState.SupportedSchema,
                runId,
                status,
                manifestHash,
                WorkspaceFor(runId),
                processId,
                startedAt,
                ruleId,
                DateTimeOffset.UtcNow)
            {
                CreatedAt = previous?.CreatedAt,
                AttemptId = previous?.AttemptId,
                EnvironmentId = previous?.EnvironmentId,
                ExecutionContext = previous?.ExecutionContext,
                StartupDeadlineUtc = previous?.StartupDeadlineUtc,
                RetainOnFailure = previous?.RetainOnFailure ?? _options.RetainOnFailure,
                TestRun = previous?.TestRun,
                CleanupDisposition = cleanupDisposition ?? previous?.CleanupDisposition ?? "not-requested",
                RemainingResources = remainingResources ?? previous?.RemainingResources,
                SelectedTest = _options.SelectedTest,
                StartupDurationSeconds = record?.DurationSeconds,
                ReadinessResults = record is null ? [] : [.. record.ReadinessResults],
                EffectiveDeadlineSeconds = record?.Deadline.TimeoutSeconds ?? previous?.EffectiveDeadlineSeconds,
                EffectiveDeadlineIsOverride = record?.Deadline.IsOverride ?? previous?.EffectiveDeadlineIsOverride ?? false,
                EffectiveDeadlineJustification = record?.Deadline.Justification ?? previous?.EffectiveDeadlineJustification,
                EffectiveDeadlineModuleId = record?.Deadline.ModuleId ?? previous?.EffectiveDeadlineModuleId,
                DiagnosticsPath = record?.DiagnosticsPath,
                ProjectProcessIds = record is null ? [] : [.. record.ProjectProcessIds],
            },
            cancellationToken).ConfigureAwait(false);
    }
}