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
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "AppHost process ownership transfers to the returned session or to bounded teardown, which disposes it.")]
    public async Task<CompositionStartResult> StartAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string? runId = null;
        Process? appHost = null;
        string manifestHash = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestLoadResult manifestResult = ModuleManifestLoader.Load(manifestPath);
            if (!manifestResult.IsValid)
            {
                return Failed("failed", ToolExitCode.UsageOrManifest, manifestResult.Diagnostics, null);
            }

            ModuleManifest manifest = manifestResult.Manifest!;
            CompositionPrerequisiteResult prerequisites = await CompositionPrerequisiteProbe.ProbeAsync(
                _options.DaprHome,
                _options.DockerCommand,
                cancellationToken).ConfigureAwait(false);
            if (!prerequisites.IsAvailable)
            {
                return Failed("unavailable", ToolExitCode.PrerequisiteUnavailable, prerequisites.Diagnostics, null);
            }

            if (!Path.IsPathFullyQualified(_options.AppHostAssemblyPath) || !File.Exists(_options.AppHostAssemblyPath))
            {
                return Failed(
                    "unavailable",
                    ToolExitCode.PrerequisiteUnavailable,
                    [Diagnostic("HXR014", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "The Builds-owned AppHost is not built.", "apphost", "Build Hexalith.Builds.Module.AppHost before retrying.")],
                    null);
            }

            ExecutableDescriptorLoadResult descriptors = await ExecutableDescriptorLoader.LoadAsync(
                manifest,
                manifestPath,
                _options.DescriptorChildEntryAssemblyPath,
                cancellationToken).ConfigureAwait(false);
            if (!descriptors.IsValid)
            {
                bool unavailable = descriptors.Diagnostics[0].Category == ToolFailureCategory.PrerequisiteUnavailable;
                return Failed(
                    unavailable ? "unavailable" : "failed",
                    unavailable ? ToolExitCode.PrerequisiteUnavailable : ToolExitCode.UsageOrManifest,
                    descriptors.Diagnostics,
                    null);
            }

            (IReadOnlyList<CompositionRunModule> modules, IReadOnlyList<CompositionRunUiMarker> markers) =
                CompositionRunPlanFactory.Bind(manifest, descriptors);
            manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
            runId = CompositionRunPlanFactory.NewRunId();
            string workspace = WorkspaceFor(runId);
            CompositionWorkspace.CreatePrivateDirectory(Path.GetFullPath(_options.WorkspaceRoot));
            CompositionWorkspace.CreatePrivateDirectory(workspace);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(
                runId,
                workspace,
                prerequisites.DaprHome!,
                CompositionPortAllocator.Allocate(modules.Count, _options.EnableSecondEventStoreInstance),
                modules,
                markers);
            await CompositionDaprComponentRenderer.WriteAsync(plan, cancellationToken).ConfigureAwait(false);
            string planPath = Path.Combine(workspace, "plan.json");
            await CompositionDocumentStore.WriteAsync(planPath, plan, cancellationToken).ConfigureAwait(false);
            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, null, null, cancellationToken).ConfigureAwait(false);

            string signingKey = CompositionSigningKey.Create();
            appHost = StartAppHost(plan, planPath, signingKey);
            if (appHost is null)
            {
                return await FailAndTearDownAsync(
                    runId,
                    null,
                    manifestHash,
                    CompositionRunStatus.Failed,
                    Diagnostic("HXR020", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The Builds-owned AppHost could not be started.", "apphost", "Check the local .NET runtime and retry."),
                    ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
            }

            await WriteStateAsync(runId, CompositionRunStatus.Starting, manifestHash, appHost.Id, StartTime(appHost), cancellationToken).ConfigureAwait(false);
            return await WaitForReadinessAsync(plan, appHost, signingKey, manifestHash, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ToolDiagnostic cancelled = Diagnostic("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The invocation was cancelled; bounded teardown ran.", "cancellation", null);
            return runId is null
                ? Failed("cancelled", ToolExitCode.Cancelled, [cancelled], null)
                : await FailAndTearDownAsync(runId, appHost, manifestHash, CompositionRunStatus.Cancelled, cancelled, ToolExitCode.Cancelled).ConfigureAwait(false);
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
                ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
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

    private static async Task<ToolDiagnostic?> ReadStartupFailureAsync(string runId, string workspace, CancellationToken cancellationToken)
    {
        CompositionStartupFailure? failure = await CompositionDocumentStore
            .TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(workspace), cancellationToken)
            .ConfigureAwait(false);
        return failure switch
        {
            null => null,
            CompositionStartupFailure invalid when !string.Equals(invalid.Schema, CompositionStartupFailure.SupportedSchema, StringComparison.Ordinal)
                || !string.Equals(invalid.RunId, runId, StringComparison.Ordinal)
                || invalid.RuleId is not ("HXR025" or "HXR026" or "HXR027" or "HXR028")
                || !IsSafeFailureText(invalid.Resource, 128)
                || !IsSafeFailureText(invalid.AppId, 63)
                || !IsSafeFailureText(invalid.Status, 120) =>
                Diagnostic("HXR022", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The AppHost failure document does not describe this run.", "readiness", "Rebuild the Builds-owned AppHost and retry."),
            CompositionStartupFailure valid => Diagnostic(
                valid.RuleId,
                ToolPhase.Topology,
                ToolFailureCategory.TopologyOrLifecycle,
                $"Readiness stalled at resource '{valid.Resource}' (app ID '{valid.AppId}', status '{valid.Status}').",
                valid.Resource,
                "Inspect the matching AppHost and resource log."),
        };
    }

    private static bool IsSafeFailureText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= maximumLength
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.' or ':' or ';' or '=' or ' ');

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

        ToolDiagnostic? stopFailure = await ReadStartupFailureAsync(runId, WorkspaceFor(runId), bound).ConfigureAwait(false);
        if (stopFailure?.RuleId != "HXR028")
        {
            stopFailure = null;
        }

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
        CancellationToken cancellationToken)
    {
        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < _options.ReadinessTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ToolDiagnostic? startupFailure = await ReadStartupFailureAsync(plan.RunId, plan.Workspace, cancellationToken).ConfigureAwait(false);
            if (startupFailure is not null)
            {
                return await FailAndTearDownAsync(
                    plan.RunId,
                    appHost,
                    manifestHash,
                    CompositionRunStatus.Failed,
                    startupFailure,
                    ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
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
                        ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
                }

                await WriteStateAsync(plan.RunId, CompositionRunStatus.Ready, manifestHash, appHost.Id, StartTime(appHost), cancellationToken).ConfigureAwait(false);
                ToolDiagnostic ready = Diagnostic("HXI010", ToolPhase.Topology, ToolFailureCategory.None, "Every run resource reported healthy.", "readiness", null);
                return new CompositionStartResult(
                    new ToolCommandResult("ready", ToolOutcome.Passed(), [ready]),
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
                    ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
            }

            await Task.Delay(_pollInterval, cancellationToken).ConfigureAwait(false);
        }

        ToolDiagnostic timeoutDiagnostic = await ReadStartupFailureAsync(plan.RunId, plan.Workspace, CancellationToken.None).ConfigureAwait(false)
            ?? Diagnostic("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The run resources did not become healthy within the readiness bound.", "readiness", "Inspect the run prerequisites and retry.");
        return await FailAndTearDownAsync(
            plan.RunId,
            appHost,
            manifestHash,
            CompositionRunStatus.Failed,
            timeoutDiagnostic,
            ToolExitCode.TopologyOrLifecycle).ConfigureAwait(false);
    }

    private async Task<CompositionStartResult> FailAndTearDownAsync(
        string runId,
        Process? appHost,
        string manifestHash,
        CompositionRunStatus status,
        ToolDiagnostic diagnostic,
        ToolExitCode exitCode)
    {
        // Teardown is bounded and deliberately not cancellable by the caller's (possibly cancelled) token.
        using CancellationTokenSource bound = TeardownBound();
        (CompositionRunResources remaining, bool workspaceRemoved) = await TearDownAsync(runId, appHost, bound.Token).ConfigureAwait(false);
        bool clean = remaining.IsEmpty && workspaceRemoved;
        try
        {
            await WriteStateAsync(runId, status, manifestHash, null, null, bound.Token, clean ? diagnostic.RuleId : "HXR023")
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (IsTeardownFailure(exception))
        {
            // The failure state is best-effort metadata; the returned diagnostics still report the outcome.
        }

        List<ToolDiagnostic> diagnostics = [diagnostic];
        if (!clean)
        {
            diagnostics.Add(Diagnostic("HXR023", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Run teardown left tagged resources behind.", "down", "Retry down for the same run identity."));
        }

        string text = status == CompositionRunStatus.Cancelled ? "cancelled" : "failed";
        return Failed(text, exitCode, diagnostics, runId);
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
        CompositionDiagnosticMirror.Attach(process, _options.AppHostLogPath);
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

    private Task WriteStateAsync(
        string runId,
        CompositionRunStatus status,
        string manifestHash,
        int? processId,
        DateTimeOffset? startedAt,
        CancellationToken cancellationToken,
        string? ruleId = null) =>
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
                DateTimeOffset.UtcNow),
            cancellationToken);
}
