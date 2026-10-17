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
        string manifestHash = string.Empty;
        try
        {
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

            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));

            // A rejected pinned catalog must not run descriptor child code.
            if (_options.SourceMapping is { Mode: WorkspaceMode.Package } packageMapping)
            {
                runId = CompositionRunPlanFactory.NewRunId();
                preparedWorkspace = WorkspaceFor(runId);
                CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
                CompositionWorkspace.CreatePrivateDirectory(preparedWorkspace);
                await SourceMappingMaterializer.WriteAsync(packageMapping, preparedWorkspace, startupBound.Token).ConfigureAwait(false);
            }

            IReadOnlyList<CompositionRunModule> modules;
            IReadOnlyList<CompositionRunUiMarker> markers;
            string? platformModel = null;
            if (platformComposition)
            {
                SourceMapping mapping = _options.SourceMapping ?? throw new InvalidOperationException("Platform v2 requires a resolved source mapping.");
                runId ??= CompositionRunPlanFactory.NewRunId();
                preparedWorkspace ??= WorkspaceFor(runId);
                CompositionWorkspace.CreatePrivateDirectory(preparedWorkspace);
                await SourceMappingMaterializer.WriteAsync(mapping, preparedWorkspace, startupBound.Token).ConfigureAwait(false);
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
                    IReadOnlyList<ToolDiagnostic> descriptorDiagnostics = await CleanupPreparedWorkspaceAsync(preparedWorkspace, descriptors.Diagnostics).ConfigureAwait(false);

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

                    return Failed(
                        unavailable ? "unavailable" : "failed",
                        descriptorExit,
                        descriptorDiagnostics,
                        null);
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
                        return Failed(
                            "failed",
                            ToolExitCode.TopologyOrLifecycle,
                            await CleanupPreparedWorkspaceAsync(preparedWorkspace, unmappedDiagnostics).ConfigureAwait(false),
                            null);
                    }
                }
            }

            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));
            if (platformComposition)
            {
                PlatformManifestValidationResult current = PlatformManifestValidator.Validate([manifestPath], startupBound.Token);
                if (!current.IsValid)
                {
                    return Failed("failed", ToolExitCode.UsageOrManifest, await CleanupPreparedWorkspaceAsync(preparedWorkspace, current.Diagnostics).ConfigureAwait(false), null);
                }

                string checkedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, startupBound.Token).ConfigureAwait(false)));
                if (!string.Equals(manifestHash, checkedHash, StringComparison.Ordinal))
                {
                    IReadOnlyList<ToolDiagnostic> changed = [Diagnostic("HXP020", ToolPhase.Manifest, ToolFailureCategory.Manifest, $"Platform declaration '{manifestPath}' changed while the run plan was being prepared.", manifestPath, "Retry with a stable declaration file.")];
                    return Failed("failed", ToolExitCode.UsageOrManifest, await CleanupPreparedWorkspaceAsync(preparedWorkspace, changed).ConfigureAwait(false), null);
                }
            }

            runId ??= CompositionRunPlanFactory.NewRunId();
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
                CompositionPortAllocator.Allocate(Math.Max(1, modules.Count), !platformComposition && _options.EnableSecondEventStoreInstance),
                modules,
                markers,
                _options.SourceMapping,
                platformComposition ? [Path.GetFullPath(manifestPath)] : [],
                activeModule,
                platformModel);
            plan = plan with
            {
                SelectedTest = _options.SelectedTest,
                RetainOnFailure = _options.RetainOnFailure,
                EffectiveDeadline = deadline,
                StartupDeadlineUtc = deadlineUtc,
                DebugAttach = _options.DebugAttach,
                StartupTasks = platformComposition ? CompositionStartupTasks.Select(manifestPath, closure, _options.SourceMapping) : [],
                PlatformDeclarationHash = platformComposition ? manifestHash : plan.PlatformDeclarationHash,
            };

            await CompositionDaprComponentRenderer.WriteAsync(plan, startupBound.Token).ConfigureAwait(false);
            string planPath = Path.Combine(workspace, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, startupBound.Token).ConfigureAwait(false);
            string signingKey = CompositionSigningKey.Create();
            if (platformComposition)
            {
                CompositionProcessResult preflight = await CompositionProcess.RunAsync(
                    CompositionProcess.CreateStartInfo(
                        DotnetHostPath(),
                        ["exec", _options.AppHostAssemblyPath, "--validate-platform"],
                        Path.GetDirectoryName(_options.AppHostAssemblyPath),
                        new Dictionary<string, string>(StringComparer.Ordinal)
                        {
                            [CompositionEnvironment.PlanPath] = planPath,
                            [CompositionEnvironment.SigningKey] = signingKey,
                            [CompositionEnvironment.RunId] = plan.RunId,
                            [CompositionEnvironment.DaprRuntimePath] = plan.DaprHome,
                        }),
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

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, null, null, startupBound.Token).ConfigureAwait(false);
            appHost = StartAppHost(plan, planPath, signingKey);
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
            return await WaitForReadinessAsync(plan, appHost, signingKey, manifestHash, requestedAt, deadline, deadlineUtc, null, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_options.SelectedTest is not null && _options.SelectedExecutionOwnerToken is { IsCancellationRequested: false })
            {
                ToolDiagnostic expired = Diagnostic("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, "The selected execution exhausted its original finite lifetime.", "execution", null);
                return runId is null ? Failed("failed", ToolExitCode.ProductOrTest, [expired], null)
                    : await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Failed, expired, ToolExitCode.ProductOrTest, requestedAt, deadline).ConfigureAwait(false);
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
            IReadOnlyList<ToolDiagnostic> diagnostics = await CleanupPreparedWorkspaceAsync(preparedWorkspace, exception.Diagnostics).ConfigureAwait(false);
            return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, diagnostics, null);
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
        try
        {
            await StopAppHostAsync(session.AppHost, bound.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            if (session.Plan.SelectedTest is not null)
            {
                KillOwnedWithoutWaiting(session.AppHost);
            }

            // The tag sweep below still removes the AppHost and everything it started.
        }

        session.AppHost.Dispose();
        return await DownCoreAsync(session.RunId, bound.Token, recordWithdrawal: false).ConfigureAwait(false);
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
        CompositionRunState? state = await StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        using CancellationTokenSource bound = TeardownBound(state?.SelectedTest);
        return await DownCoreAsync(runId, bound.Token).ConfigureAwait(false);
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
    internal async Task<CompositionDownResult> DownWithinBudgetAsync(CompositionRunSession session, CancellationToken stoppingToken)
    {
        try
        {
            await StopAppHostAsync(session.AppHost, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            KillOwnedWithoutWaiting(session.AppHost);

            // Exact ownership scanning still runs when graceful stop or diagnostics fail.
        }

        session.AppHost.Dispose();
        return await DownCoreAsync(session.RunId, stoppingToken, recordWithdrawal: false).ConfigureAwait(false);
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
    /// <returns>The first terminal record.</returns>
    internal async Task<ModuleTestRunRecord?> RecordTestOutcomeAsync(string runId, IReadOnlyList<CompositionResourceReadiness> resources, ToolCommandResult result, string disposition, CompositionRunResources remaining, CancellationToken cancellationToken, NativeTestExecutionResult? native = null, bool waitForLock = true)
    {
        CompositionRunState? state = await StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
        ModuleTestSelection? selection = state?.SelectedTest ?? _options.SelectedTest;
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
            state = await StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
            ModuleTestRunRecord? prior = await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(path, cancellationToken).ConfigureAwait(false) ?? state?.TestRun;
            if (prior is null && result.Outcome.ExitCode == ToolExitCode.Success && _options.SelectedExecutionOwnerToken is { IsCancellationRequested: true })
            {
                ToolDiagnostic withdrawn = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owner withdrew before terminal completion was committed.", "cancellation", null);
                result = new ToolCommandResult("withdrawn", ToolOutcome.Passed().Fail(withdrawn.Phase, withdrawn.Category, withdrawn.RuleId, ToolExitCode.Cancelled), [withdrawn]);
            }

            CompositionReadiness? observed = await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(Path.Combine(WorkspaceFor(runId), "readiness.json"), cancellationToken).ConfigureAwait(false);
            CompositionTestObservation? identity = observed?.RunId == runId && observed.TestObservation?.RunId == runId ? observed.TestObservation : null;
            ModuleTestRunRecord record = prior ?? new ModuleTestRunRecord(runId, runId, selection, _options.SourceMapping?.ContentHash, _options.SourceMapping?.PlatformIdentity?.Commit, _options.SourceMapping?.PlatformIdentity?.CheckoutCommit, identity?.ObservedCandidateHash, identity?.ObservedBackendIdentity, false, resources, result.Status, result.Outcome, DateTimeOffset.UtcNow, disposition, remaining);
            record = record with { ResourceDisposition = disposition, RemainingResources = remaining, Resources = resources.Count > 0 ? resources : record.Resources };
            await CompositionDocumentStore.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
            CompositionWorkspace.CreatePrivateDirectory(directory);
            CopySelectedMetadata(WorkspaceFor(runId), directory);
            if (native?.ReportBytes is { } reportBytes)
            {
                await File.WriteAllBytesAsync(Path.Combine(directory, "native.trx"), reportBytes, cancellationToken).ConfigureAwait(false);
                record = record with
                {
                    ReportHash = Convert.ToHexString(SHA256.HashData(reportBytes)),
                    TestCounts = native.Report is { } report ? new RunEvidence.ModuleRunTestCounts(true, report.Total, report.Passed, report.Failed, report.Skipped) : null,
                };
            }

            if (native?.ObservationBytes is { } observationBytes)
            {
                string detail = Path.Combine(directory, "functional-observations.json");
                await File.WriteAllBytesAsync(detail, observationBytes, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(detail, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }

                record = record with { ObservationHash = Convert.ToHexString(SHA256.HashData(observationBytes)) };
            }

            await CompositionDocumentStore.WriteAsync(path, record, cancellationToken).ConfigureAwait(false);
            if (state is not null)
            {
                await StateStore.WriteAsync(state with { TestRun = record }, cancellationToken).ConfigureAwait(false);
            }

            return record;
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
            : [.. diagnostics, Diagnostic(
                "HXW009",
                ToolPhase.Prerequisite,
                ToolFailureCategory.PrerequisiteUnavailable,
                $"Private catalog workspace '{workspace}' could not be removed after refusal.",
                workspace,
                "Remove this private workspace before retrying."),
            ];
    }

    private static bool IsTeardownFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or OperationCanceledException or InvalidOperationException
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
            || failure.RuleId is not ("HXR021" or "HXR025" or "HXR026" or "HXR027" or "HXR028")
            || !IsSafeFailureText(failure.Resource, 128)
            || !IsSafeFailureText(failure.AppId, 63)
            || !IsSafeFailureText(failure.Status, 120)
            || failure.UnreadyResources.Any(resource => !IsSafeFailureText(resource.Name, 128) || !IsSafeFailureText(resource.Detail ?? resource.State, 120))
            || (failure.FailingTask is not null && !IsSafeFailureText(failure.FailingTask, 128)))
        {
            return [Diagnostic("HXR022", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The AppHost failure document does not describe this run.", "readiness", "Rebuild the Builds-owned AppHost and retry.")];
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

    private static void CopySelectedMetadata(string workspace, string destination)
    {
        foreach ((string source, string name) in new[]
        {
            (Path.Combine(workspace, "plan.json"), "selected-plan.json"),
            (Path.Combine(workspace, "readiness.json"), "selected-readiness.json"),
            (Path.Combine(workspace, "source-mapping", "mapping.json"), "selected-source-mapping.json"),
        })
        {
            try
            {
                if (File.Exists(source))
                {
                    byte[] bytes = File.ReadAllBytes(source);
                    using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(bytes);
                    if (SafeMetadata(document.RootElement))
                    {
                        File.WriteAllBytes(Path.Combine(destination, name), bytes);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
            {
                // An absent, unreadable or unsafe observation is never invented.
            }
        }
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
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
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
        return progress is null
            || !string.Equals(progress.Schema, CompositionReadinessProgress.SupportedSchema, StringComparison.Ordinal)
            || !string.Equals(progress.RunId, plan.RunId, StringComparison.Ordinal)
            ? []
            : [
                .. progress.Resources.Select((resource, index) => resource with
                {
                    Name = IsSafeFailureText(resource.Name, 128) ? resource.Name : $"resource-{index + 1}",
                    Detail = IsSafeFailureText(resource.Detail ?? resource.State, 120) ? resource.Detail : "detail withheld: unsafe or oversized text",
                }),
            ];
    }

    private static IReadOnlyList<int> ProjectProcessIds(IReadOnlyList<CompositionResourceReadiness> resources) =>
        [.. resources.Where(resource => resource.ProcessId is > 0).Select(resource => resource.ProcessId!.Value).Distinct().Order()];

    private async Task TryRecordStoppingOutcomeAsync(string runId, IReadOnlyList<CompositionResourceReadiness> resources, ToolCommandResult result, string disposition, CompositionRunResources remaining, List<ToolDiagnostic> diagnostics, CancellationToken bound)
    {
        try
        {
            _ = await RecordTestOutcomeAsync(runId, resources, result, disposition, remaining, bound, waitForLock: false).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            diagnostics.Add(RecordingFailure());
        }
    }

    private async Task<bool> DeleteStateAfterCleanupAsync(string runId, bool selected, CancellationToken cancellationToken)
    {
        if (!selected)
        {
            return StateStore.Delete(runId);
        }

        FileStream outcomeLock = await OpenOutcomeLockAsync(Path.Combine(DiagnosticsDirectory(runId), "outcome.lock"), cancellationToken, waitForLock: false).ConfigureAwait(false);
        await using (outcomeLock.ConfigureAwait(false))
        {
            return StateStore.Delete(runId);
        }
    }

    private async Task<CompositionDownResult> DownCoreAsync(string runId, CancellationToken bound, bool recordWithdrawal = true)
    {
        List<ToolDiagnostic> bookkeeping = [];
        CompositionRunState? state = null;
        try
        {
            state = await StateStore.TryReadAsync(runId, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            bookkeeping.Add(RecordingFailure());
        }

        if (recordWithdrawal && state?.SelectedTest is not null)
        {
            ToolDiagnostic withdrawn = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owned test run was explicitly stopped.", "down", null);
            ToolCommandResult terminal = new("withdrawn", ToolOutcome.Passed().Fail(withdrawn.Phase, withdrawn.Category, withdrawn.RuleId, ToolExitCode.Cancelled), [withdrawn]);
            await TryRecordStoppingOutcomeAsync(runId, state.ReadinessResults, terminal, "unknown", new CompositionRunResources([], [], Unverified: true), bookkeeping, bound).ConfigureAwait(false);
        }

        if (state?.AppHostProcessId is int processId && TryGetRecordedProcess(processId, state.AppHostStartedAt) is Process recorded)
        {
            using (recorded)
            {
                try
                {
                    await SignalStopAsync(recorded, bound, state?.SelectedTest is not null).ConfigureAwait(false);
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

        if (recordWithdrawal && state?.SelectedTest is not null)
        {
            ToolDiagnostic withdrawn = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owned test run was explicitly stopped.", "down", null);
            ToolCommandResult terminal = new("withdrawn", ToolOutcome.Passed().Fail(withdrawn.Phase, withdrawn.Category, withdrawn.RuleId, ToolExitCode.Cancelled), [withdrawn]);
            await TryRecordStoppingOutcomeAsync(runId, state.ReadinessResults, terminal, remaining.IsEmpty && workspaceRemoved ? "removed" : "unknown", remaining, bookkeeping, bound).ConfigureAwait(false);
        }

        // Run state is kept while anything may remain, so a retried down still finds the recorded AppHost.
        bool stateRemoved = false;
        try
        {
            stateRemoved = bookkeeping.Count == 0 && remaining.IsEmpty && workspaceRemoved && await DeleteStateAfterCleanupAsync(runId, state?.SelectedTest is not null, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            bookkeeping.Add(RecordingFailure());
        }

        if (bookkeeping.Count > 0)
        {
            ToolDiagnostic firstRecordingFailure = bookkeeping[0];
            return new CompositionDownResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, firstRecordingFailure.RuleId, ToolExitCode.EvidenceSchemaOrPolicy), bookkeeping), remaining);
        }

        if (stateRemoved && stopFailure is null)
        {
            ToolDiagnostic completed = Diagnostic("HXI001", ToolPhase.Cleanup, ToolFailureCategory.None, "Runner-owned invocation cleanup completed.", "down", null);
            return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), [completed]), remaining);
        }

        if (stateRemoved)
        {
            return new CompositionDownResult(
                new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, stopFailure!.RuleId, ToolExitCode.TopologyOrLifecycle), [stopFailure]),
                remaining);
        }

        ToolDiagnostic incomplete = Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Run teardown left tagged resources behind.", "down", "Retry down for the same run identity.");
        ToolDiagnostic first = stopFailure ?? incomplete;
        return new CompositionDownResult(
            new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, first.RuleId, ToolExitCode.TopologyOrLifecycle), stopFailure is null ? [incomplete] : [stopFailure, incomplete]),
            remaining);
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
        string? expiredTask = null;
        string? refreshId = null;
        while (CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow) > TimeSpan.Zero)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                    (string? failingTask, bool deadlineElapsed, string? expired) = await CompositionStartupTasks.RunOwnedDetailedAsync(plan.StartupTasks, deadlineUtc, plan.RunId, cancellationToken).ConfigureAwait(false);
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
                };
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
            observationBound).ConfigureAwait(false);
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
        CancellationTokenSource? stoppingBound = null)
    {
        long stoppingTimestamp = initialStoppingTimestamp ?? Stopwatch.GetTimestamp();
        using CancellationTokenSource? bound = stoppingBound is null ? TeardownBound() : null;
        CancellationToken boundToken = (stoppingBound ?? bound!).Token;
        List<ToolDiagnostic> bookkeeping = [];
        if (_options.SelectedTest is not null)
        {
            string terminalStatus = status == CompositionRunStatus.Cancelled ? "withdrawn" : "failed";
            ToolCommandResult terminal = Failed(terminalStatus, exitCode, diagnostics, runId).Result;
            await TryRecordStoppingOutcomeAsync(runId, resources, terminal, _options.RetainOnFailure && status != CompositionRunStatus.Cancelled ? "retained" : "unknown", new CompositionRunResources([], [], Unverified: true), bookkeeping, boundToken).ConfigureAwait(false);
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
        bool retained = _options.RetainOnFailure && status != CompositionRunStatus.Cancelled;
        int? retainedPid = retained && appHost?.HasExited == false ? appHost.Id : null;
        DateTimeOffset? retainedStart = retainedPid is null ? null : StartTime(appHost!);
        if (_options.SelectedTest is not null)
        {
            CopySelectedMetadata(WorkspaceFor(runId), diagnosticsPath);
        }

        (CompositionRunResources remaining, bool workspaceRemoved) = retained
            ? (new CompositionRunResources([], [], Unverified: true), false)
            : await TearDownAsync(runId, appHost, boundToken).ConfigureAwait(false);
        bool clean = remaining.IsEmpty && workspaceRemoved;
        string ruleId = clean || retained ? located[0].RuleId : "HXR023";
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
                new CompositionRunRecord((DateTimeOffset.UtcNow - requestedAt).TotalSeconds, resources, deadline, diagnosticsPath, ProjectProcessIds(resources))).ConfigureAwait(false);
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
        if (status == CompositionRunStatus.Cancelled && _options.SelectedTest is not null)
        {
            text = "withdrawn";
        }

        CompositionStartResult result = Failed(text, exitCode, reported, runId);
        if (_options.SelectedTest is not null)
        {
            string disposition = clean ? "removed" : "unknown";
            await TryRecordStoppingOutcomeAsync(runId, resources, result.Result, retained ? "retained" : disposition, remaining, bookkeeping, boundToken).ConfigureAwait(false);
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
            remaining = new CompositionRunResources([], [], Unverified: true);
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

    private Process? StartAppHost(CompositionRunPlan plan, string planPath, string signingKey)
    {
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
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

        return await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Failed, timeout, ToolExitCode.TopologyOrLifecycle, requestedAt, deadline).ConfigureAwait(false);
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
        return progress is null
            || !string.Equals(progress.Schema, CompositionReadinessProgress.SupportedSchema, StringComparison.Ordinal)
            || !string.Equals(progress.RunId, runId, StringComparison.Ordinal)
            ? []
            : [.. progress.Resources.Where(resource => !string.Equals(resource.State, "Healthy", StringComparison.Ordinal))];
    }

    private Task WriteStateAsync(
        string runId,
        CompositionRunStatus status,
        string manifestHash,
        int? processId,
        DateTimeOffset? startedAt,
        CancellationToken cancellationToken,
        string? ruleId = null,
        CompositionRunRecord? record = null) =>
        StateStore.WriteAsync(
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
                SelectedTest = _options.SelectedTest,
                StartupDurationSeconds = record?.DurationSeconds,
                ReadinessResults = record is null ? [] : [.. record.ReadinessResults],
                EffectiveDeadlineSeconds = record?.Deadline.TimeoutSeconds,
                EffectiveDeadlineIsOverride = record?.Deadline.IsOverride ?? false,
                EffectiveDeadlineJustification = record?.Deadline.Justification,
                EffectiveDeadlineModuleId = record?.Deadline.ModuleId,
                DiagnosticsPath = record?.DiagnosticsPath,
                ProjectProcessIds = record is null ? [] : [.. record.ProjectProcessIds],
            },
            cancellationToken);
}
