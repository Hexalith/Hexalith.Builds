// <copyright file="AttachedSuiteSupervisor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Hosts runner-owned attached suites inside the live owner command. It renews a liveness lease, picks up requests under
/// the attachment lock, launches each child in its own workspace with a narrowed bearer, stops it at the earliest of
/// expiry, withdrawal, its own cancellation or an owner drain, ends its access, records termination and publishes the
/// attached result, always before the owner's resources stop.
/// </summary>
internal sealed class AttachedSuiteSupervisor : IAsyncDisposable
{
    private static readonly TimeSpan _minimumLeaseValidity = TimeSpan.FromSeconds(10);

    private readonly CompositionEngineOptions _options;
    private readonly CompositionRunSession _session;
    private readonly CompositionRunState _owner;
    private readonly string _manifestPath;
    private readonly ModuleTestSelection _ownerSelection;
    private readonly string _directory;
    private readonly TimeSpan _poll;
    private readonly TimeSpan _leaseValidity;
    private readonly CompositionRunResourceScanner _scanner;
    private readonly ConcurrentDictionary<string, AttachedSuiteRun> _runs = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _loopStop = new();
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private readonly SemaphoreSlim _pickupGate = new(1, 1);
    private readonly DateTimeOffset? _processStartedAt;
    private Task _loop = Task.CompletedTask;
    private volatile bool _accepting = true;
    private volatile bool _closed;
    private string? _drainReason;
    private DateTimeOffset _renewedAt = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="AttachedSuiteSupervisor"/> class.</summary>
    /// <param name="options">The owner's runner settings.</param>
    /// <param name="session">The ready owner session, the only holder of the signing key.</param>
    /// <param name="owner">The owner's issued durable identities.</param>
    /// <param name="manifestPath">The owner's manifest, from which attached suites are re-selected.</param>
    /// <param name="ownerSelection">The owner's selected suite.</param>
    internal AttachedSuiteSupervisor(CompositionEngineOptions options, CompositionRunSession session, CompositionRunState owner, string manifestPath, ModuleTestSelection ownerSelection)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        _manifestPath = manifestPath;
        _ownerSelection = ownerSelection ?? throw new ArgumentNullException(nameof(ownerSelection));
        _directory = Path.Combine(options.StateDirectory, "diagnostics", session.RunId);
        _poll = options.HoldPollInterval > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : options.HoldPollInterval;
        _leaseValidity = _poll * 10 > _minimumLeaseValidity ? _poll * 10 : _minimumLeaseValidity;
        _scanner = new CompositionRunResourceScanner(options.DockerCommand) { ProcessDiscoveryOverride = options.AttachedProcessDiscoveryOverride };
        try
        {
            using Process current = Process.GetCurrentProcess();
            _processStartedAt = current.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            _processStartedAt = null;
        }
    }

    /// <summary>Gets the owner run.</summary>
    internal string OwnerRunId => _session.RunId;

    /// <summary>Gets a value indicating whether attached children are running or finalizing.</summary>
    internal bool HasActiveRuns => !_runs.IsEmpty;

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The loop task is started and owned by this instance and ends on its own cancellation token.")]
    public async ValueTask DisposeAsync()
    {
        if (!_runs.IsEmpty || !_closed)
        {
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(Math.Max(_ownerSelection.StopTimeoutSeconds, 5)));
            _ = await DrainAsync("cleanup", bound.Token).ConfigureAwait(false);
        }

        await _loopStop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop ended on its own token.
        }

        try
        {
            File.Delete(AttachedSuiteSupervisorLease.PathFor(_directory));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A closed lease that cannot be deleted is still not live.
        }

        foreach (AttachedSuiteRun run in _runs.Values)
        {
            run.Dispose();
        }

        _loopStop.Dispose();
        _leaseGate.Dispose();
        _pickupGate.Dispose();
    }

    /// <summary>Publishes the open lease and starts picking up requests.</summary>
    /// <param name="cancellationToken">The initial publication allowance.</param>
    /// <returns>The started supervisor task.</returns>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        CompositionWorkspace.CreatePrivateDirectory(_directory);
        await RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
        _loop = Task.Run(() => LoopAsync(), CancellationToken.None);
    }

    /// <summary>Waits until no attached run is active or pending, then closes the lease so no new request can be recorded.</summary>
    /// <param name="cancellationToken">The owner's cancellation; a cancelled wait leaves the runs to the owner's withdrawal and drain.</param>
    /// <returns>The idle wait.</returns>
    internal async Task WaitForIdleAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_runs.IsEmpty && await TryCloseWhenIdleAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await Task.Delay(_poll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Stops every attached run after the owner's durable withdrawal and before any owner resource stops: it closes the
    /// lease, stops children for the withdrawal reason, ends their access, records termination and publishes results.
    /// </summary>
    /// <param name="reason">The environment withdrawal reason.</param>
    /// <param name="bound">The owner's stop allowance; nothing is extended.</param>
    /// <returns>The drain diagnostics.</returns>
    internal async Task<IReadOnlyList<ToolDiagnostic>> DrainAsync(string reason, CancellationToken bound)
    {
        _ = Interlocked.CompareExchange(ref _drainReason, reason, null);
        _accepting = false;
        try
        {
            _ = await CloseLeaseAsync(requireIdle: false, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            // A closed in-memory supervisor no longer picks up requests even when the lease file could not be rewritten.
            _closed = true;
        }

        // Requests recorded before the lease closed are finalized too, so every attacher receives a result.
        try
        {
            await PickUpAsync(force: true, bound).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            // Unpicked requests are left to the external down, which stops tagged leftovers and records them.
        }

        AttachedSuiteRun[] active = [.. _runs.Values];
        foreach (AttachedSuiteRun run in active.Where(run => run.StopReason is null))
        {
            // Stop first so lock contention never lets the child outlive the withdrawal; the record follows.
            _ = run.Signal(reason);
            try
            {
                _ = await AttachedSuiteTermination.RecordOutcomeAsync(_directory, OwnerRunId, run.AttachedRunId, "withdrawn", reason, null, ToolExitCode.Cancelled, "HXC130", bound).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // The stop proceeds regardless: access never continues because a record could not be written.
            }
        }

        try
        {
            await Task.WhenAll(active.Select(run => run.Completion)).WaitAsync(bound).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await ForceTerminateAsync(active.Where(run => !run.Completion.IsCompleted)).ConfigureAwait(false);
        }

        // Every recorded runner-owned run of this owner whose termination failed or whose result is unpublished is retried
        // and reported, not only the runs active now, so owner cleanup stays incomplete while a tagged process survives.
        List<CompositionAttachment> reported = [];
        try
        {
            using CancellationTokenSource reading = new(TimeSpan.FromSeconds(5));
            foreach (CompositionAttachment item in await CompositionAttachmentStore.ReadAsync(_directory, OwnerRunId, reading.Token).ConfigureAwait(false))
            {
                if (item.Execution is null || _runs.ContainsKey(item.AttachedRunId))
                {
                    continue;
                }

                bool retry = item.Termination is null || item.Termination.Failures.Count > 0 || item.Execution.ResultPublishedAtUtc is null;
                CompositionAttachment current = item;
                if (retry)
                {
                    if (current.EndedAtUtc is null)
                    {
                        current = await AttachedSuiteTermination.RecordOutcomeAsync(_directory, OwnerRunId, current.AttachedRunId, "withdrawn", reason, null, ToolExitCode.Cancelled, "HXC130", bound).ConfigureAwait(false) ?? current;
                    }

                    current = await AttachedSuiteTermination.TerminateForeignAsync(_scanner, _options.StateDirectory, _owner, current.AttachedRunId, "owner", bound).ConfigureAwait(false) ?? current;
                }

                if (retry || active.Any(run => run.AttachedRunId == current.AttachedRunId))
                {
                    reported.Add(current);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            // An unverifiable retry is reported as a leftover: cleanup must not be recorded complete.
            return [.. AttachedSuiteTermination.Diagnostics(reported), new ToolDiagnostic("HXR038", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "Attached run termination could not be verified before owner teardown.", "attachments", "Retry down for the same owner run; the original hold expiry is never extended.")];
        }

        return AttachedSuiteTermination.Diagnostics(reported);
    }

    private static bool IsPending(CompositionAttachment item) => item.Execution is { PickedUpAtUtc: null } && item.Termination is null;

    private static (string Outcome, string? Reason, string? Cause, ToolExitCode ExitCode, string? RuleId) Decide(AttachedSuiteRun run, ModuleTestSelection? selection, NativeTestExecutionResult? native)
    {
        string? stop = run.StopReason;
        if (stop == "expired")
        {
            return ("withdrawn", "expired", null, ToolExitCode.Cancelled, "HXC130");
        }

        if (stop == "run-cancelled")
        {
            return ("cancelled", null, "run-cancelled", ToolExitCode.Cancelled, "HXC130");
        }

        if (stop is not null)
        {
            return ("withdrawn", stop, null, ToolExitCode.Cancelled, "HXC130");
        }

        if (selection is null)
        {
            return ("failed", null, "startup-failure", ToolExitCode.UsageOrManifest, "HXC002");
        }

        if (native is null)
        {
            return ("failed", null, "startup-failure", ToolExitCode.ProductOrTest, "HXT009");
        }

        ToolOutcome outcome = native.Result.Outcome;
        if (outcome.ExitCode == ToolExitCode.Success)
        {
            return ("passed", null, "tests-completed", ToolExitCode.Success, null);
        }

        string cause = native.Launched ? "test-failure" : "startup-failure";
        if (native.TerminalCause == "execution-timeout")
        {
            cause = "execution-timeout";
        }

        return ("failed", null, cause, outcome.ExitCode, outcome.RuleId ?? "HXT007");
    }

    private async Task LoopAsync()
    {
        while (!_loopStop.IsCancellationRequested)
        {
            try
            {
                await PassAsync(_loopStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_loopStop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // A transient metadata failure is retried on the next pass; running children keep their own expiry watch.
            }

            try
            {
                await Task.Delay(_poll, _loopStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PassAsync(CancellationToken cancellationToken)
    {
        await RenewLeaseAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<CompositionAttachment> current = await CompositionAttachmentStore.ReadAsync(_directory, OwnerRunId, cancellationToken).ConfigureAwait(false);
        foreach (AttachedSuiteRun run in _runs.Values)
        {
            CompositionAttachment? record = current.FirstOrDefault(item => item.AttachedRunId == run.AttachedRunId);
            if (record is null || run.StopReason is not null)
            {
                continue;
            }

            if (record.EndedAtUtc is not null && record.EndReason != "terminal-outcome")
            {
                // Expiry or an environment withdrawal recorded elsewhere (another terminal's down) stops this child.
                _ = run.Signal(record.EndReason!);
            }
            else if (record.EndedAtUtc is null && record.Execution?.CancelRequestedAtUtc is not null)
            {
                // The attached command's own cancellation ends only this run: stop the child, then record; expiry still wins by time.
                _ = run.Signal("run-cancelled");
                _ = await AttachedSuiteTermination.RecordOutcomeAsync(_directory, OwnerRunId, run.AttachedRunId, "cancelled", null, "run-cancelled", ToolExitCode.Cancelled, "HXC130", cancellationToken).ConfigureAwait(false);
            }
        }

        if (_accepting && current.Any(IsPending))
        {
            await PickUpAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task PickUpAsync(bool force, CancellationToken cancellationToken)
    {
        await _pickupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<CompositionAttachment> picked = await CompositionAttachmentStore.UpdateAsync<List<CompositionAttachment>>(
                _directory,
                OwnerRunId,
                current =>
                {
                    if (!force && !_accepting)
                    {
                        return ([], null);
                    }

                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    List<CompositionAttachment> started = [];
                    List<CompositionAttachment> replacement = [];
                    foreach (CompositionAttachment item in current)
                    {
                        if (IsPending(item) && !_runs.ContainsKey(item.AttachedRunId))
                        {
                            CompositionAttachment updated = item with { Execution = item.Execution! with { AttemptId = CompositionRunPlanFactory.NewRunId(), PickedUpAtUtc = now } };
                            started.Add(updated);
                            replacement.Add(updated);
                        }
                        else
                        {
                            replacement.Add(item);
                        }
                    }

                    return (started, started.Count == 0 ? null : replacement);
                },
                cancellationToken).ConfigureAwait(false);
            foreach (CompositionAttachment item in picked)
            {
                AttachedSuiteRun run = new(item);
                if (_runs.TryAdd(item.AttachedRunId, run))
                {
                    if (Volatile.Read(ref _drainReason) is { } draining)
                    {
                        // A request finalized during a drain never launches a child.
                        _ = run.Signal(draining);
                    }

                    string attachedRunId = item.AttachedRunId;
                    run.Completion = Task.Run(() => RunByIdAsync(attachedRunId), CancellationToken.None);
                }
            }
        }
        finally
        {
            _ = _pickupGate.Release();
        }
    }

    private Task RunByIdAsync(string attachedRunId) => _runs.TryGetValue(attachedRunId, out AttachedSuiteRun? run) ? RunAsync(run) : Task.CompletedTask;

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Every failure of a hosted attached run must still record its outcome, termination and result.")]
    private async Task RunAsync(AttachedSuiteRun run)
    {
        CompositionAttachment hold = run.Hold;
        string attemptId = hold.Execution!.AttemptId!;
        AttachedSuiteIdentity identity = new(hold, attemptId, AttachedSuiteIdentity.WorkspaceFor(_session.Plan.Workspace, hold.AttachedRunId), Path.Combine(_options.StateDirectory, "diagnostics", hold.AttachedRunId));
        NativeTestExecutionResult? native = null;
        ModuleTestExecutionTiming? timing = null;
        ModuleTestSelection? selection = null;
        bool launchRecorded = false;
        using CancellationTokenSource expiryWatch = new();
        Task expiry = WatchExpiryAsync(hold.AttachedRunId, hold.ExpiresAtUtc, expiryWatch.Token);
        try
        {
            // A hold that ended before pickup is only finalized: no workspace, credentials or child are ever prepared for it.
            selection = hold.EndedAtUtc is null ? ResolveSelection(hold.Execution) : null;
            if (selection is not null && run.StopReason is null && DateTimeOffset.UtcNow < hold.ExpiresAtUtc)
            {
                CompositionAttachment? launching = await CompositionAttachmentStore.UpdateOneAsync(
                    _directory,
                    OwnerRunId,
                    hold.AttachedRunId,
                    target => target.EndedAtUtc is null && target.Execution is { LaunchedAtUtc: null } execution
                        ? target with { Execution = execution with { LaunchedAtUtc = DateTimeOffset.UtcNow } }
                        : target,
                    run.StopToken).ConfigureAwait(false);
                launchRecorded = launching is { EndedAtUtc: null, Execution.LaunchedAtUtc: not null };
                if (launchRecorded && run.StopReason is null)
                {
                    CompositionWorkspace.CreatePrivateDirectory(identity.Workspace);
                    CompositionWorkspace.CreatePrivateDirectory(identity.DiagnosticsDirectory);
                    CompositionReadiness readiness = _session.Readiness;
                    if (readiness.NativeEndpointCredentialsPath is { } ownerCredentials)
                    {
                        await WriteAttachedCredentialsAsync(ownerCredentials, identity, run.StopToken).ConfigureAwait(false);
                        readiness = readiness with { NativeEndpointCredentialsPath = identity.CredentialsPath };
                    }

                    CompositionRunSession attached = new(_session.Plan with { SelectedTest = selection, SelectedTests = [selection], ExecutionTiming = null }, readiness, _session.SigningKey, _session.AppHost);
                    native = await NativeTestExecutor.ExecuteAttachedAsync(attached, selection, _manifestPath, _options.NativeDotnetCommand ?? "dotnet", identity, run.Started, null, run.StopToken).ConfigureAwait(false);
                    timing = attached.Plan.ExecutionTiming;
                }
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // A preparation or launch failure fails only this attached run; the decision below records it.
            native = null;
        }
        finally
        {
            await expiryWatch.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await expiry.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The watch ended with the run.
        }

        try
        {
            using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(10));
            (string outcome, string? reason, string? cause, ToolExitCode exitCode, string? ruleId) = Decide(run, selection, native);
            CompositionAttachment? ended = await AttachedSuiteTermination.RecordOutcomeAsync(_directory, OwnerRunId, hold.AttachedRunId, outcome, reason, cause, exitCode, ruleId, metadata.Token).ConfigureAwait(false);
            using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(Math.Max(selection?.StopTimeoutSeconds ?? _ownerSelection.StopTimeoutSeconds, 5)));
            CompositionAttachmentTermination termination = await AttachedSuiteTermination.TerminateAsync(
                _scanner,
                identity.Workspace,
                hold.AttachedRunId,
                run.ProcessId,
                native?.Launched == true || run.ProcessId is not null,
                native?.CredentialExpiresAtUtc,
                "owner",
                stopping.Token).ConfigureAwait(false);
            using CancellationTokenSource publication = new(TimeSpan.FromSeconds(10));
            ended = await AttachedSuiteTermination.RecordTerminationAsync(_directory, OwnerRunId, hold.AttachedRunId, termination, publication.Token).ConfigureAwait(false) ?? ended;
            if (ended?.Termination is not null && ended.Execution?.ResultPublishedAtUtc is null)
            {
                IReadOnlyList<string> rules = ended.Outcome == "passed" ? [] : [.. native?.Result.Diagnostics.Select(item => item.RuleId) ?? [ruleId ?? "HXT009"]];
                _ = await AttachedSuiteTermination.PublishAsync(_options.StateDirectory, _owner, ended, native?.DescriptorSnapshot, timing, native?.ReportBytes, rules, publication.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // An unrecordable termination stays pending; the external down or the attacher stops leftovers and records them.
        }
        finally
        {
            if (_runs.TryRemove(hold.AttachedRunId, out AttachedSuiteRun? removed))
            {
                removed.Dispose();
            }
        }
    }

    private async Task WatchExpiryAsync(string attachedRunId, DateTimeOffset expiresAt, CancellationToken cancellationToken)
    {
        // The runner's UTC clock decides expiry; a timer that fires early waits again rather than ending the hold early.
        while (true)
        {
            TimeSpan remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await Task.Delay(remaining > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : remaining, cancellationToken).ConfigureAwait(false);
        }

        // Stop the child at expiry before taking the attachment lock; the record follows and expiry wins by its timestamp.
        if (_runs.TryGetValue(attachedRunId, out AttachedSuiteRun? run))
        {
            _ = run.Signal("expired");
        }

        try
        {
            using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(10));
            _ = await AttachedSuiteTermination.RecordOutcomeAsync(_directory, OwnerRunId, attachedRunId, "withdrawn", "expired", null, ToolExitCode.Cancelled, "HXC130", metadata.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
        {
            // Expiry is still enforced: the child is stopped and the outcome decision records expiry by time.
        }
    }

    private ModuleTestSelection? ResolveSelection(CompositionAttachedExecution request)
    {
        try
        {
            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([_manifestPath], CancellationToken.None);
            if (!validation.IsValid)
            {
                return null;
            }

            ModuleTestSelection? selection = ModuleTestSelection.Select(validation.Declarations!, request.Profile, _manifestPath, DateTimeOffset.UtcNow);
            bool matches = selection is not null
                && string.Equals(selection.ManifestHash, request.ManifestHash, StringComparison.OrdinalIgnoreCase)
                && string.Equals(selection.EntryHash, request.EntryHash, StringComparison.OrdinalIgnoreCase)
                && selection.Mode == request.Mode
                && ModuleTestSelection.Compatible(_ownerSelection, selection)
                && !selection.RequiresOwnerGate();
            return matches ? selection : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return null;
        }
    }

    private async Task WriteAttachedCredentialsAsync(string ownerCredentials, AttachedSuiteIdentity identity, CancellationToken cancellationToken)
    {
        // The attached channel carries the same endpoint grants bound to the attached run, in its own private file.
        if (!SelectedNativeTestReport.IsOwnedRegularPath(ownerCredentials, _session.Plan.Workspace))
        {
            throw new InvalidDataException("The owner endpoint credential channel is unavailable.");
        }

        JsonNode document = JsonNode.Parse(await File.ReadAllBytesAsync(ownerCredentials, cancellationToken).ConfigureAwait(false)) ?? throw new InvalidDataException("The owner endpoint credential channel is unavailable.");
        if (document is not JsonObject channel || channel["runId"]?.GetValue<string>() != OwnerRunId)
        {
            throw new InvalidDataException("The owner endpoint credential channel is foreign.");
        }

        channel["runId"] = identity.RunId;
        await NativeTestExecutor.WriteSnapshotAsync(identity.CredentialsPath, identity.Workspace, System.Text.Encoding.UTF8.GetBytes(channel.ToJsonString()), cancellationToken).ConfigureAwait(false);
    }

    private async Task RenewLeaseAsync(CancellationToken cancellationToken)
    {
        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            if (_closed || now - _renewedAt < _leaseValidity / 4)
            {
                return;
            }

            _renewedAt = now;
            await CompositionDocumentStore.WriteAsync(
                AttachedSuiteSupervisorLease.PathFor(_directory),
                new AttachedSuiteSupervisorLease(AttachedSuiteSupervisorLease.SupportedSchema, OwnerRunId, Environment.ProcessId, _processStartedAt, now, now + _leaseValidity, "open"),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _leaseGate.Release();
        }
    }

    private async Task<bool> TryCloseWhenIdleAsync(CancellationToken cancellationToken) =>
        await CloseLeaseAsync(requireIdle: true, cancellationToken).ConfigureAwait(false) && _runs.IsEmpty;

    private Task<bool> CloseLeaseAsync(bool requireIdle, CancellationToken cancellationToken) =>
        CompositionAttachmentStore.UpdateCoreAsync<bool>(
            _directory,
            OwnerRunId,
            async current =>
            {
                // Admission checks the lease under this same lock, so closing here leaves no request unseen.
                if (requireIdle && (current.Any(IsPending) || !_runs.IsEmpty))
                {
                    return (false, null);
                }

                await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _closed = true;
                    _accepting = false;
                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    await CompositionDocumentStore.WriteAsync(
                        AttachedSuiteSupervisorLease.PathFor(_directory),
                        new AttachedSuiteSupervisorLease(AttachedSuiteSupervisorLease.SupportedSchema, OwnerRunId, Environment.ProcessId, _processStartedAt, now, now, "closed"),
                        cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    _ = _leaseGate.Release();
                }

                return (true, null);
            },
            cancellationToken);

    private async Task ForceTerminateAsync(IEnumerable<AttachedSuiteRun> unfinished)
    {
        // One shared finite allowance for every unfinished run, stopped in parallel; nothing is renewed per run.
        using CancellationTokenSource forced = new(TimeSpan.FromSeconds(3));
        await Task.WhenAll(unfinished.Select(run => ForceTerminateOneAsync(run.AttachedRunId, run.ProcessId, run.Hold.ExpiresAtUtc, forced.Token))).ConfigureAwait(false);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A forced stop at the end of the owner allowance must report every attached run, whatever failed.")]
    private async Task ForceTerminateOneAsync(string attachedRunId, int? processId, DateTimeOffset expiresAt, CancellationToken forced)
    {
        try
        {
            CompositionAttachmentTermination termination = await AttachedSuiteTermination.TerminateAsync(
                _scanner,
                AttachedSuiteIdentity.WorkspaceFor(_session.Plan.Workspace, attachedRunId),
                attachedRunId,
                processId,
                processId is not null,
                processId is null ? null : expiresAt,
                "owner",
                forced).ConfigureAwait(false);
            CompositionAttachment? recorded = await AttachedSuiteTermination.RecordTerminationAsync(_directory, OwnerRunId, attachedRunId, termination, forced).ConfigureAwait(false);
            if (recorded is { Termination: not null, Execution.ResultPublishedAtUtc: null })
            {
                _ = await AttachedSuiteTermination.PublishAsync(_options.StateDirectory, _owner, recorded, AttachedSuiteTermination.ArchivedDescriptor(Path.Combine(_options.StateDirectory, "diagnostics", attachedRunId)), null, null, AttachedSuiteTermination.FallbackRules(recorded), forced).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // The external down retries tagged leftovers; nothing here reports an unconfirmed stop as complete.
        }
    }
}
