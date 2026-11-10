// <copyright file="AttachedSuiteTermination.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses with distinct stable outcomes read better than nested conditionals.

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Records first outcomes, stops leftover attached work, ends access and publishes attached results.</summary>
internal static class AttachedSuiteTermination
{
    /// <summary>Records an attached run's first outcome; an earlier outcome is never replaced and expiry wins at its deadline.</summary>
    /// <param name="ownerDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner.</param>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="outcome">The proposed first outcome.</param>
    /// <param name="reason">The withdrawal reason when the outcome is <c>withdrawn</c>.</param>
    /// <param name="cause">The v1 cause of an own terminal outcome.</param>
    /// <param name="exitCode">The relayed command exit code.</param>
    /// <param name="ruleId">The first failing rule.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The durable record.</returns>
    internal static Task<CompositionAttachment?> RecordOutcomeAsync(string ownerDirectory, string ownerRunId, string attachedRunId, string outcome, string? reason, string? cause, ToolExitCode exitCode, string? ruleId, CancellationToken cancellationToken) =>
        CompositionAttachmentStore.UpdateOneAsync(
            ownerDirectory,
            ownerRunId,
            attachedRunId,
            target =>
            {
                if (target.EndedAtUtc is not null)
                {
                    return target;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                if (now >= target.ExpiresAtUtc)
                {
                    return Stamp(target.Ended(target.ExpiresAtUtc, "expired", "withdrawn", now), ToolExitCode.Cancelled, "HXC130");
                }

                if (outcome == "withdrawn")
                {
                    return Stamp(target.Ended(now, reason ?? "cleanup", "withdrawn", now), ToolExitCode.Cancelled, "HXC130");
                }

                return Stamp(target.Ended(now, "terminal-outcome", outcome, now, cause), exitCode, ruleId);
            },
            cancellationToken);

    /// <summary>Records the attached run's termination once its outcome is recorded; a failed earlier observation may be replaced by a retry.</summary>
    /// <param name="ownerDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner.</param>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="termination">The observed termination.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The durable record.</returns>
    internal static Task<CompositionAttachment?> RecordTerminationAsync(string ownerDirectory, string ownerRunId, string attachedRunId, CompositionAttachmentTermination termination, CancellationToken cancellationToken) =>
        CompositionAttachmentStore.UpdateOneAsync(
            ownerDirectory,
            ownerRunId,
            attachedRunId,
            target =>
            {
                if (target.EndedAtUtc is null || (target.Termination is { } prior && prior.Failures.Count == 0))
                {
                    return target;
                }

                CompositionAttachment terminated = target with { Termination = termination };
                return terminated.Execution is { } execution ? terminated with { Execution = execution with { ResultPublishedAtUtc = null } } : terminated;
            },
            cancellationToken);

    /// <summary>Stops every process tagged with the attached run, deletes its private access files and reports what survives.</summary>
    /// <param name="scanner">The run-tag scanner.</param>
    /// <param name="workspace">The attachment's private workspace.</param>
    /// <param name="attachedRunId">The attached run tag.</param>
    /// <param name="processId">The launched child, when known.</param>
    /// <param name="reportedSurvivors">Processes whose tree kill could not be confirmed.</param>
    /// <param name="launched">Whether a child may have been launched.</param>
    /// <param name="bearerExpiry">The latest instant the narrowed bearer can be valid, when one may have been issued.</param>
    /// <param name="recorder">The recording component.</param>
    /// <param name="cancellationToken">The stop allowance.</param>
    /// <returns>The termination observation; failures are reported, never hidden.</returns>
    internal static async Task<CompositionAttachmentTermination> TerminateAsync(CompositionRunResourceScanner scanner, string workspace, string attachedRunId, int? processId, IReadOnlyList<int> reportedSurvivors, bool launched, DateTimeOffset? bearerExpiry, string recorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scanner);
        ArgumentNullException.ThrowIfNull(reportedSurvivors);
        IReadOnlyList<int> remaining;
        bool verified;
        try
        {
            (remaining, verified) = await scanner.RemoveTaggedProcessesAsync(attachedRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            (remaining, verified) = ([], false);
        }

        int[] reported = [.. reportedSurvivors.Where(IsRunning)];
        foreach (int survivor in reported)
        {
            TryKill(survivor);
        }

        try
        {
            for (int attempt = 0; attempt < 20 && reported.Any(IsRunning); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The allowance ended; whatever still runs is reported below.
        }

        int[] survivors = [.. remaining.Concat(reported.Where(IsRunning)).Distinct().Order()];
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        List<string> leftovers = [.. survivors.Select(id => "process:" + id.ToString(CultureInfo.InvariantCulture))];
        if (!verified)
        {
            leftovers.Add("process-scan-unverified:" + attachedRunId);
        }

        bool workStopped = survivors.Length == 0 && verified;
        string workId = processId is int child ? "process:" + child.ToString(CultureInfo.InvariantCulture) : "work:" + attachedRunId;
        CompositionTerminationItem work = workStopped
            ? new(workId, "terminated", observedAt, "The attached process tree stopped and no process tagged with the attached run survives.")
            : new(workId, "failed", observedAt, "A process tagged with the attached run survived its stop allowance or its absence could not be verified.");
        if (!launched && workStopped)
        {
            work = new(workId, "not-applicable", observedAt, "No attached child was launched.");
        }

        bool filesExisted = false;
        bool filesDeleted = true;
        foreach (string file in new[] { NativeTestHandoff.EndpointCredentialsFileName, "test-environment.json" })
        {
            string path = Path.Combine(workspace, file);
            try
            {
                if (File.Exists(path))
                {
                    filesExisted = true;
                    File.Delete(path);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Reported below as an undeletable access file.
            }

            if (File.Exists(path))
            {
                filesDeleted = false;
                leftovers.Add("file:" + file);
            }
        }

        string bearer = bearerExpiry is { } expiry ? "bearer valid until " + Utc(expiry) : "no bearer was issued";
        bool accessEnded = workStopped && filesDeleted;
        CompositionTerminationItem access = accessEnded
            ? new("access:" + attachedRunId, "terminated", observedAt, bearer + "; no holder")
            : new("access:" + attachedRunId, "failed", observedAt, bearer + "; a holder or private access file may survive");
        if (bearerExpiry is null && !filesExisted && accessEnded)
        {
            access = new("access:" + attachedRunId, "not-applicable", observedAt, "The runner issued no environment access for this attached run.");
        }

        return new CompositionAttachmentTermination(observedAt, [work], [access], [.. leftovers.Distinct(StringComparer.Ordinal)])
        {
            Recorder = recorder,
            CredentialExpiresAtUtc = bearerExpiry,
        };
    }

    /// <summary>Publishes the attached run's validated result and records the publication.</summary>
    /// <param name="stateDirectory">The runner state directory.</param>
    /// <param name="owner">The owner's durable identities.</param>
    /// <param name="ended">The ended attachment with its recorded termination.</param>
    /// <param name="descriptor">The validated pre-launch attached descriptor, when one was prepared.</param>
    /// <param name="timing">The attached execution clock, when execution was activated.</param>
    /// <param name="report">The inspected native report, when retained.</param>
    /// <param name="ruleIds">The classified rule identities of the first outcome.</param>
    /// <param name="cancellationToken">The publication allowance.</param>
    /// <returns>The published path, or null when publication could not be completed.</returns>
    internal static async Task<string?> PublishAsync(string stateDirectory, CompositionRunState owner, CompositionAttachment ended, byte[]? descriptor, ModuleTestExecutionTiming? timing, byte[]? report, IReadOnlyList<string> ruleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(ended);
        try
        {
            string path = await TestEnvironment.TestResultWriter.WriteAttachedAsync(stateDirectory, owner, ended, ended.Execution?.AttemptId ?? string.Empty, descriptor, timing, report, ruleIds, cancellationToken).ConfigureAwait(false);
            _ = await CompositionAttachmentStore.UpdateOneAsync(
                Path.Combine(stateDirectory, "diagnostics", owner.RunId),
                owner.RunId,
                ended.AttachedRunId,
                target => target.Execution is { ResultPublishedAtUtc: null } execution && Equals(target.Termination, ended.Termination)
                    ? target with { Execution = execution with { ResultPublishedAtUtc = DateTimeOffset.UtcNow } }
                    : target,
                cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Ends an attached suite whose host is gone or did not stop it in time: stops tagged work, ends access, records and publishes.</summary>
    /// <param name="scanner">The run-tag scanner.</param>
    /// <param name="stateDirectory">The runner state directory.</param>
    /// <param name="owner">The owner's durable identities.</param>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="recorder">The recording component: <c>down</c> or <c>attacher</c>.</param>
    /// <param name="cancellationToken">The stop allowance.</param>
    /// <returns>The durable record after termination.</returns>
    internal static async Task<CompositionAttachment?> TerminateForeignAsync(CompositionRunResourceScanner scanner, string stateDirectory, CompositionRunState owner, string attachedRunId, string recorder, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string directory = Path.Combine(stateDirectory, "diagnostics", owner.RunId);
        CompositionAttachment? current = (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.AttachedRunId == attachedRunId);
        if (current?.Execution is null)
        {
            return current;
        }

        current = await CompositionAttachmentStore.UpdateOneAsync(
            directory,
            owner.RunId,
            attachedRunId,
            target =>
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                CompositionAttachment picked = target.Execution is { AttemptId: null } request
                    ? target with { Execution = request with { AttemptId = CompositionRunPlanFactory.NewRunId(), PickedUpAtUtc = now } }
                    : target;

                // The host vanished before it recorded an outcome: the run did not complete and fails, unless expiry already won.
                if (picked.EndedAtUtc is not null)
                {
                    return picked;
                }

                if (now >= picked.ExpiresAtUtc)
                {
                    return Stamp(picked.Ended(picked.ExpiresAtUtc, "expired", "withdrawn", now), ToolExitCode.Cancelled, "HXC130");
                }

                string cause = target.Execution?.LaunchedAtUtc is null ? "startup-failure" : "test-failure";
                return Stamp(picked.Ended(now, "terminal-outcome", "failed", now, cause), ToolExitCode.TopologyOrLifecycle, "HXT009");
            },
            cancellationToken).ConfigureAwait(false);
        if (current?.Execution is null)
        {
            return current;
        }

        if (current.Termination is null || current.Termination.Failures.Count > 0)
        {
            CompositionAttachmentTermination termination = await TerminateAsync(
                scanner,
                AttachedSuiteIdentity.WorkspaceFor(owner.Workspace, attachedRunId),
                attachedRunId,
                null,
                [],
                current.Execution.LaunchedAtUtc is not null,
                current.Execution.LaunchedAtUtc is null ? null : current.ExpiresAtUtc,
                recorder,
                cancellationToken).ConfigureAwait(false);
            current = await RecordTerminationAsync(directory, owner.RunId, attachedRunId, termination, cancellationToken).ConfigureAwait(false) ?? current;
        }

        if (current.Execution?.ResultPublishedAtUtc is null && current.Termination is not null)
        {
            _ = await PublishAsync(stateDirectory, owner, current, ArchivedDescriptor(Path.Combine(stateDirectory, "diagnostics", attachedRunId)), null, null, [current.Execution?.RuleId ?? "HXT009"], cancellationToken).ConfigureAwait(false);
        }

        return (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, cancellationToken).ConfigureAwait(false)).FirstOrDefault(item => item.AttachedRunId == attachedRunId);
    }

    /// <summary>Waits within the stop budget for the live owner to stop its withdrawn attached suites, then stops and records any leftovers itself.</summary>
    /// <param name="scanner">The run-tag scanner.</param>
    /// <param name="stateDirectory">The runner state directory.</param>
    /// <param name="owner">The owner's durable identities.</param>
    /// <param name="wait">The share of the stop budget granted to the live owner.</param>
    /// <param name="bound">The overall stop budget.</param>
    /// <returns>Diagnostics reporting stopped runs and any termination failures, before any owner resource stops.</returns>
    internal static async Task<IReadOnlyList<ToolDiagnostic>> DrainForeignAsync(CompositionRunResourceScanner scanner, string stateDirectory, CompositionRunState owner, TimeSpan wait, CancellationToken bound)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string directory = Path.Combine(stateDirectory, "diagnostics", owner.RunId);
        static bool Pending(CompositionAttachment item) => item.Execution is not null
            && (item.Termination is null || item.Termination.Failures.Count > 0 || item.Execution.ResultPublishedAtUtc is null);
        string[] targets = [.. (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, bound).ConfigureAwait(false)).Where(Pending).Select(item => item.AttachedRunId)];
        if (targets.Length == 0)
        {
            return [];
        }

        if (AttachedSuiteSupervisorLease.Read(directory) is { } lease && lease.OwnerRunId == owner.RunId && lease.HolderRunning())
        {
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(bound);
            waiting.CancelAfter(wait);
            try
            {
                while (true)
                {
                    IReadOnlyList<CompositionAttachment> observed = await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, waiting.Token).ConfigureAwait(false);
                    if (observed.Where(item => targets.Contains(item.AttachedRunId, StringComparer.Ordinal)).All(item => item.Termination is not null && item.Execution?.ResultPublishedAtUtc is not null))
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), waiting.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!bound.IsCancellationRequested)
            {
                // The live owner did not finish within its share; the remaining budget stops leftovers here.
            }
        }

        List<CompositionAttachment> ended = [];
        IReadOnlyList<CompositionAttachment> latest = await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, bound).ConfigureAwait(false);
        foreach (string attachedRunId in targets)
        {
            CompositionAttachment? record = latest.FirstOrDefault(item => item.AttachedRunId == attachedRunId);
            if (record is not null && Pending(record))
            {
                record = await TerminateForeignAsync(scanner, stateDirectory, owner, attachedRunId, "down", bound).ConfigureAwait(false);
            }

            if (record is not null)
            {
                ended.Add(record);
            }
        }

        return Diagnostics(ended);
    }

    /// <summary>Ends a request the owner never picked up, with not-applicable termination, and publishes its result.</summary>
    /// <param name="stateDirectory">The runner state directory.</param>
    /// <param name="owner">The owner's durable identities.</param>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="outcome">The first outcome: <c>failed</c> or <c>cancelled</c>.</param>
    /// <param name="cause">The v1 cause.</param>
    /// <param name="exitCode">The relayed exit code.</param>
    /// <param name="ruleId">The first rule.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The durable record; a request the owner picked up meanwhile is returned unchanged.</returns>
    internal static async Task<CompositionAttachment?> AbandonAsync(string stateDirectory, CompositionRunState owner, string attachedRunId, string outcome, string cause, ToolExitCode exitCode, string ruleId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        string directory = Path.Combine(stateDirectory, "diagnostics", owner.RunId);
        CompositionAttachment? updated = await CompositionAttachmentStore.UpdateOneAsync(
            directory,
            owner.RunId,
            attachedRunId,
            target =>
            {
                if (target.Execution is not { PickedUpAtUtc: null } request || target.Termination is not null)
                {
                    return target;
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                CompositionAttachment picked = (target with { Execution = request with { AttemptId = CompositionRunPlanFactory.NewRunId(), PickedUpAtUtc = now } }).ObservedAt(now);
                picked = picked.EndedAtUtc is null ? Stamp(picked.Ended(now, "terminal-outcome", outcome, now, cause), exitCode, ruleId) : picked;
                return picked with { Termination = CompositionAttachmentTermination.NotApplicable(attachedRunId, now, "attacher") };
            },
            cancellationToken).ConfigureAwait(false);
        if (updated is { Termination.Recorder: "attacher", Execution.ResultPublishedAtUtc: null })
        {
            _ = await PublishAsync(stateDirectory, owner, updated, null, null, null, [ruleId], cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    /// <summary>Reports stopped attached runs and any termination failures as metadata-only diagnostics.</summary>
    /// <param name="ended">The terminated attachments.</param>
    /// <returns>An informational report and, for leftovers, a lifecycle failure.</returns>
    internal static IReadOnlyList<ToolDiagnostic> Diagnostics(IReadOnlyList<CompositionAttachment> ended)
    {
        ArgumentNullException.ThrowIfNull(ended);
        CompositionAttachment[] terminated = [.. ended.Where(item => item.Termination is not null)];
        List<ToolDiagnostic> diagnostics = [];
        if (terminated.Length > 0)
        {
            diagnostics.Add(new ToolDiagnostic("HXI013", ToolPhase.Cleanup, ToolFailureCategory.None, $"Attached runs stopped and their access ended before owner teardown: {string.Join(", ", terminated.Select(item => item.AttachedRunId + "(" + item.Outcome + ")"))}.", "attachments"));
        }

        CompositionAttachment[] failed = [.. terminated.Where(item => item.Termination!.Failures.Count > 0)];
        if (failed.Length > 0)
        {
            diagnostics.Add(new ToolDiagnostic("HXR038", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, $"Attached run work or access could not be confirmed ended; leftovers: {string.Join(", ", failed.SelectMany(item => item.Termination!.LeftoverWorkIds).Distinct(StringComparer.Ordinal))}.", "attachments", "Retry down for the same owner run; the original hold expiry is never extended."));
        }

        return diagnostics;
    }

    /// <summary>Reads an archived attached descriptor only when it still validates.</summary>
    /// <param name="attachedDirectory">The attached run's diagnostics directory.</param>
    /// <returns>The validated descriptor bytes, or null.</returns>
    internal static byte[]? ArchivedDescriptor(string attachedDirectory)
    {
        string path = Path.Combine(attachedDirectory, "test-environment.json");
        if (!File.Exists(path))
        {
            return null;
        }

        TestEnvironment.TestEnvironmentContractReadResult validated = TestEnvironment.TestEnvironmentContractReader.ReadDescriptor(path);
        return validated.IsValid ? System.Text.Encoding.UTF8.GetBytes(validated.Document!.Value.GetRawText()) : null;
    }

    private static CompositionAttachment Stamp(CompositionAttachment ended, ToolExitCode exitCode, string? ruleId)
    {
        if (ended.Execution is not { ExitCode: null } execution)
        {
            return ended;
        }

        string? failedRule = exitCode == ToolExitCode.Success ? null : ruleId;
        return ended with { Execution = execution with { ExitCode = (int)exitCode, RuleId = failedRule } };
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private static void TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited; the rescan decides what survives.
        }
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
