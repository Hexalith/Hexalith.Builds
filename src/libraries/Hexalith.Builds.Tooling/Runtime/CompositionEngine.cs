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
        DateTimeOffset requestedAt = DateTimeOffset.UtcNow;
        CompositionEffectiveDeadline deadline = CompositionStartupDeadlineResolver.Resolve(null, _options.ReadinessTimeout);
        DateTimeOffset deadlineUtc;

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

            ManifestLoadResult manifestResult = platformComposition ? new ManifestLoadResult(null, []) : ModuleManifestLoader.Load(manifestPath);
            if (!platformComposition && !manifestResult.IsValid)
            {
                return Failed("failed", ToolExitCode.UsageOrManifest, manifestResult.Diagnostics, null);
            }

            ModuleManifest manifest = manifestResult.Manifest!;
            deadline = CompositionStartupDeadlineResolver.Resolve(platformDeclarations?.Declarations, _options.ReadinessTimeout);
            deadlineUtc = requestedAt + deadline.Timeout;
            ToolDiagnostic? aspire = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                _options.AspireCommand,
                _options.AspireProbeTimeout,
                cancellationToken).ConfigureAwait(false);
            if (aspire is not null)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, [aspire], null);
            }

            if (await TimeoutIfDueAsync(deadlineUtc, requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false) is CompositionStartResult aspireDeadline)
            {
                return aspireDeadline;
            }

            CompositionPrerequisiteResult prerequisites = await CompositionPrerequisiteProbe.ProbeAsync(
                _options.DaprHome,
                _options.DockerCommand,
                cancellationToken).ConfigureAwait(false);
            if (!prerequisites.IsAvailable)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, prerequisites.Diagnostics, null);
            }

            if (await TimeoutIfDueAsync(deadlineUtc, requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false) is CompositionStartResult prerequisiteDeadline)
            {
                return prerequisiteDeadline;
            }

            if (!Path.IsPathFullyQualified(_options.AppHostAssemblyPath) || !File.Exists(_options.AppHostAssemblyPath))
            {
                return Failed(
                    "unavailable",
                    ToolExitCode.PrerequisiteUnavailable,
                    [Diagnostic("HXR014", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "The Builds-owned AppHost is not built.", "apphost", "Build Hexalith.Builds.Module.AppHost before retrying.")],
                    null);
            }

            // A rejected pinned catalog must not run descriptor child code.
            if (_options.SourceMapping is { Mode: WorkspaceMode.Package } packageMapping)
            {
                runId = CompositionRunPlanFactory.NewRunId();
                preparedWorkspace = WorkspaceFor(runId);
                CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
                CompositionWorkspace.CreatePrivateDirectory(preparedWorkspace);
                await SourceMappingMaterializer.WriteAsync(packageMapping, preparedWorkspace, cancellationToken).ConfigureAwait(false);
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
                await SourceMappingMaterializer.WriteAsync(mapping, preparedWorkspace, cancellationToken).ConfigureAwait(false);
                try
                {
                    platformModel = await (_options.PlatformModelBuildOverride?.Invoke(mapping, preparedWorkspace, cancellationToken)
                        ?? PlatformManifestCommand.BuildModelAsync(mapping, preparedWorkspace, cancellationToken)).ConfigureAwait(false);
                }
                catch (InvalidOperationException exception)
                {
                    IReadOnlyList<ToolDiagnostic> refusal = [Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, PlatformManifestCommand.BoundedOutput(exception.Message, string.Empty), Path.Combine(mapping.PlatformIdentity?.PlatformPath ?? mapping.Entries.Single(entry => entry.Identity == "Hexalith.Platform").Path!, "src", "Hexalith.Platform.Composition", "Hexalith.Platform.Composition.csproj"), "Correct the named model-build input; no resources were started.")];
                    return Failed("failed", ToolExitCode.TopologyOrLifecycle, await CleanupPreparedWorkspaceAsync(preparedWorkspace, refusal).ConfigureAwait(false), null);
                }

                if (await TimeoutIfDueAsync(deadlineUtc, requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false) is CompositionStartResult modelDeadline)
                {
                    return modelDeadline;
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
                    cancellationToken,
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

            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
            if (platformComposition)
            {
                PlatformManifestValidationResult current = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
                if (!current.IsValid)
                {
                    return Failed("failed", ToolExitCode.UsageOrManifest, await CleanupPreparedWorkspaceAsync(preparedWorkspace, current.Diagnostics).ConfigureAwait(false), null);
                }

                string checkedHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
                if (!string.Equals(manifestHash, checkedHash, StringComparison.Ordinal))
                {
                    IReadOnlyList<ToolDiagnostic> changed = [Diagnostic("HXP020", ToolPhase.Manifest, ToolFailureCategory.Manifest, $"Platform declaration '{manifestPath}' changed while the run plan was being prepared.", manifestPath, "Retry with a stable declaration file.")];
                    return Failed("failed", ToolExitCode.UsageOrManifest, await CleanupPreparedWorkspaceAsync(preparedWorkspace, changed).ConfigureAwait(false), null);
                }

                platformDeclarations = current;
            }

            runId ??= CompositionRunPlanFactory.NewRunId();
            string workspace = preparedWorkspace ?? WorkspaceFor(runId);
            if (preparedWorkspace is null)
            {
                CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
                CompositionWorkspace.CreatePrivateDirectory(workspace);
                if (_options.SourceMapping is not null)
                {
                    await SourceMappingMaterializer.WriteAsync(_options.SourceMapping, workspace, cancellationToken).ConfigureAwait(false);
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
                PlatformManifestCommand.ActiveModule(platformDeclarations?.Declarations),
                platformModel);
            plan = plan with
            {
                EffectiveDeadline = deadline,
                StartupDeadlineUtc = deadlineUtc,
                DebugAttach = _options.DebugAttach,
                StartupTasks = platformComposition ? CompositionStartupTasks.Select(manifestPath, platformDeclarations?.Declarations) : [],
                PlatformDeclarationHash = platformComposition ? manifestHash : plan.PlatformDeclarationHash,
            };

            await CompositionDaprComponentRenderer.WriteAsync(plan, cancellationToken).ConfigureAwait(false);
            string planPath = Path.Combine(workspace, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, cancellationToken).ConfigureAwait(false);
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
                    TimeSpan.FromMinutes(5),
                    cancellationToken).ConfigureAwait(false);
                if (preflight.ExitCode != 0)
                {
                    IReadOnlyList<ToolDiagnostic> refusal = [Diagnostic("HXP020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, PlatformManifestCommand.BoundedOutput(preflight.Output, preflight.ErrorOutput), "platform", "Supply the named owner input; no resources were started.")];
                    return Failed("failed", ToolExitCode.TopologyOrLifecycle, await CleanupPreparedWorkspaceAsync(workspace, refusal).ConfigureAwait(false), null);
                }
            }

            if (await TimeoutIfDueAsync(deadlineUtc, requestedAt, deadline, runId, manifestHash, appHost).ConfigureAwait(false) is CompositionStartResult launchDeadline)
            {
                return launchDeadline;
            }

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, null, null, cancellationToken).ConfigureAwait(false);
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

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, appHost.Id, StartTime(appHost), cancellationToken).ConfigureAwait(false);
            return await WaitForReadinessAsync(plan, appHost, signingKey, manifestHash, requestedAt, deadline, deadlineUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ToolDiagnostic cancelled = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The invocation was cancelled; bounded teardown ran.", "cancellation", null);
            return runId is null
                ? Failed("cancelled", ToolExitCode.Cancelled, [cancelled], null)
                : await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Cancelled, cancelled, ToolExitCode.Cancelled, requestedAt, deadline).ConfigureAwait(false);
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
            // The tag sweep below still removes the AppHost and everything it started.
        }

        session.AppHost.Dispose();
        return await DownCoreAsync(session.RunId, bound.Token).ConfigureAwait(false);
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
        using CancellationTokenSource bound = TeardownBound();
        return await DownCoreAsync(runId, bound.Token).ConfigureAwait(false);
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
            or Win32Exception or AggregateException;

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

    private static async Task<IReadOnlyList<CompositionResourceReadiness>> ReadUnreadyProgressAsync(CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        CompositionReadinessProgress? progress = await CompositionDocumentStore
            .TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(plan.Workspace), cancellationToken)
            .ConfigureAwait(false);
        return progress is null
            || !string.Equals(progress.Schema, CompositionReadinessProgress.SupportedSchema, StringComparison.Ordinal)
            || !string.Equals(progress.RunId, plan.RunId, StringComparison.Ordinal)
            ? []
            : [
                .. progress.Resources.Where(resource =>
                    !string.Equals(resource.State, "Healthy", StringComparison.Ordinal)
                    && IsSafeFailureText(resource.Name, 128)
                    && IsSafeFailureText(resource.Detail ?? resource.State, 120)),
            ];
    }

    private static IReadOnlyList<int> ProjectProcessIds(IReadOnlyList<CompositionResourceReadiness> resources) =>
        [.. resources.Where(resource => resource.ProcessId is > 0).Select(resource => resource.ProcessId!.Value).Distinct().Order()];

    private async Task<CompositionDownResult> DownCoreAsync(string runId, CancellationToken bound)
    {
        CompositionRunState? state = await StateStore.TryReadAsync(runId, bound).ConfigureAwait(false);
        if (state?.AppHostProcessId is int processId && TryGetRecordedProcess(processId, state.AppHostStartedAt) is Process recorded)
        {
            using (recorded)
            {
                try
                {
                    await SignalStopAsync(recorded, bound).ConfigureAwait(false);
                }
                catch (Exception exception) when (IsTeardownFailure(exception))
                {
                    // The tag sweep below still removes the AppHost and everything it started.
                }
            }
        }

        ToolDiagnostic? stopFailure = (await ReadStartupFailuresAsync(runId, WorkspaceFor(runId), bound).ConfigureAwait(false))
            .FirstOrDefault(diagnostic => diagnostic.RuleId == "HXR028");

        (CompositionRunResources remaining, bool workspaceRemoved) = await TearDownAsync(runId, null, bound).ConfigureAwait(false);

        // Run state is kept while anything may remain, so a retried down still finds the recorded AppHost.
        bool stateRemoved = remaining.IsEmpty && workspaceRemoved && StateStore.Delete(runId);
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
        CancellationToken cancellationToken)
    {
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

                (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync(plan.StartupTasks, deadlineUtc, cancellationToken).ConfigureAwait(false);
                if (deadlineElapsed)
                {
                    break;
                }

                if (failingTask is not null)
                {
                    return await FailAndTearDownAsync(
                        plan.RunId,
                        appHost,
                        manifestHash,
                        CompositionRunStatus.Failed,
                        [Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, $"Startup task '{failingTask}' failed before the deadline.", failingTask, "Inspect the preserved startup diagnostics.")],
                        ToolExitCode.TopologyOrLifecycle,
                        requestedAt,
                        deadline,
                        readiness.Resources,
                        failingTask).ConfigureAwait(false);
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

                double duration = (DateTimeOffset.UtcNow - requestedAt).TotalSeconds;
                readiness = readiness with
                {
                    EffectiveDeadlineSeconds = deadline.TimeoutSeconds,
                    EffectiveDeadlineIsOverride = deadline.IsOverride,
                    EffectiveDeadlineJustification = deadline.Justification,
                    EffectiveDeadlineModuleId = deadline.ModuleId,
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
                    foreach (CompositionResourceReadiness resource in readiness.Resources.Where(resource => resource.ProcessId is > 0).OrderBy(resource => resource.Name, StringComparer.Ordinal))
                    {
                        readyDiagnostics.Add(Diagnostic("HXI010", ToolPhase.Topology, ToolFailureCategory.None, $"Project '{resource.Name}' process id '{resource.ProcessId}'.", resource.Name, null));
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

        IReadOnlyList<ToolDiagnostic> recorded = await ReadStartupFailuresAsync(plan.RunId, plan.Workspace, CancellationToken.None).ConfigureAwait(false);
        IReadOnlyList<CompositionResourceReadiness> unready = await ReadUnreadyProgressAsync(plan, CancellationToken.None).ConfigureAwait(false);
        if (recorded.Count == 0)
        {
            recorded = unready.Count == 0
                ? [Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run resources did not become healthy within the readiness bound.", "readiness", "Inspect the preserved startup diagnostics.")]
                : [
                    .. unready.Select(resource => Diagnostic(
                        "HXR021",
                        ToolPhase.Topology,
                        ToolFailureCategory.TopologyOrLifecycle,
                        $"Unready resource '{resource.Name}' last='{resource.Detail ?? resource.State}'.",
                        resource.Name,
                        "Inspect the preserved startup diagnostics.")),
                ];
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
            unready,
            null).ConfigureAwait(false);
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
        string? failingTask)
    {
        // Teardown is bounded and deliberately not cancellable by the caller's (possibly cancelled) token.
        using CancellationTokenSource bound = TeardownBound();
        if (resources.Count == 0)
        {
            resources = await ReadObservedResourcesAsync(runId, bound.Token).ConfigureAwait(false);
        }

        string diagnosticsPath;
        try
        {
            diagnosticsPath = await PreserveDiagnosticsAsync(runId, diagnostics[0].RuleId, requestedAt, deadline, resources, failingTask, bound.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnosticsPath = DiagnosticsDirectory(runId);
        }

        IReadOnlyList<ToolDiagnostic> located = WithDiagnosticsLocation(diagnostics, deadline, diagnosticsPath);
        (CompositionRunResources remaining, bool workspaceRemoved) = await TearDownAsync(runId, appHost, bound.Token).ConfigureAwait(false);
        bool clean = remaining.IsEmpty && workspaceRemoved;
        string ruleId = clean ? located[0].RuleId : "HXR023";
        try
        {
            await WriteStateAsync(
                runId,
                status,
                manifestHash,
                null,
                null,
                bound.Token,
                ruleId,
                new CompositionRunRecord((DateTimeOffset.UtcNow - requestedAt).TotalSeconds, resources, deadline, diagnosticsPath, ProjectProcessIds(resources))).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            // The failure state is best-effort metadata; the returned diagnostics still report the outcome.
        }

        List<ToolDiagnostic> reported = [.. located];
        if (!clean)
        {
            reported.Add(Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Run teardown left tagged resources behind.", "down", "Retry down for the same run identity."));
        }

        string text = status == CompositionRunStatus.Cancelled ? "cancelled" : "failed";
        return Failed(text, exitCode, reported, runId);
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
                // The tag sweep below still removes the AppHost and everything it started.
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

    private CancellationTokenSource TeardownBound() => new((_options.StopTimeout * 2) + TimeSpan.FromMinutes(2));

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

    private async Task SignalStopAsync(Process process, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            _ = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo("kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)], null, null),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }

        await WaitOrKillAsync(process, cancellationToken).ConfigureAwait(false);
    }

    private async Task WaitOrKillAsync(Process process, CancellationToken cancellationToken)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(_options.StopTimeout);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompositionProcess.Kill(process);
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

    private async Task<CompositionStartResult?> TimeoutIfDueAsync(
        DateTimeOffset deadlineUtc,
        DateTimeOffset requestedAt,
        CompositionEffectiveDeadline deadline,
        string? runId,
        string manifestHash,
        Process? appHost)
    {
        if (DateTimeOffset.UtcNow < deadlineUtc)
        {
            return null;
        }

        ToolDiagnostic timeout = Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run resources did not become healthy within the readiness bound.", "readiness", "Inspect the preserved startup diagnostics.");
        if (runId is null)
        {
            string path = await PreserveDiagnosticsAsync(null, timeout.RuleId, requestedAt, deadline, [], null, CancellationToken.None).ConfigureAwait(false);
            return Failed("failed", ToolExitCode.TopologyOrLifecycle, WithDiagnosticsLocation([timeout], deadline, path), null);
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