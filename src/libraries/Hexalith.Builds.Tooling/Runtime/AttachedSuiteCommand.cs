// <copyright file="AttachedSuiteCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses with distinct stable outcomes read better than nested conditionals.

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Runs <c>hexalith-module test --attach-to</c>: admits a hold for a declared suite, hands the execution request to the
/// live owner command, awaits its runner-owned result and relays the attached outcome and exit code.
/// </summary>
internal static class AttachedSuiteCommand
{
    /// <summary>Executes one attached suite request.</summary>
    /// <param name="options">The runner settings, or null for defaults.</param>
    /// <param name="selections">The declared selections from the same manifest as the owner.</param>
    /// <param name="selectionRefusal">The selection refusal, when selection failed.</param>
    /// <param name="mode">The requested artifact mode.</param>
    /// <param name="ownerRunId">The owner run to attach to.</param>
    /// <param name="request">The hold options.</param>
    /// <param name="cancellationToken">The attached command's own cancellation, which ends only this run.</param>
    /// <returns>The relayed attached result, or a refusal made before any admission or child.</returns>
    internal static async Task<ToolCommandResult> ExecuteAsync(CompositionEngineOptions? options, IReadOnlyList<ModuleTestSelection> selections, string? selectionRefusal, WorkspaceMode mode, string ownerRunId, AttachedSuiteRequest? request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selections);
        if (selections.Count != 1)
        {
            return Usage(selections.Count > 1 ? "An attached run executes exactly one declared suite." : selectionRefusal ?? "Select one declared suite using --profile.");
        }

        ModuleTestSelection selection = selections[0];
        if (selection.Mode != (mode == WorkspaceMode.Source ? "source" : "package"))
        {
            return Usage("The selected suite mode does not match the requested execution mode.");
        }

        if (selection.RequiresOwnerGate())
        {
            return Usage("Attached suites cannot use an owner's retention gate or access observation; run this suite in its own environment.");
        }

        string stateDirectory = options?.StateDirectory ?? CompositionRunStateStore.DefaultDirectory();
        CompositionRunStateStore store = new(stateDirectory);
        if (cancellationToken.IsCancellationRequested)
        {
            return Cancelled("The attached run was cancelled before admission; nothing was attached.");
        }

        CompositionRunState? owner = null;
        try
        {
            owner = CompositionRunPlanFactory.IsRunId(ownerRunId) ? await store.TryReadAsync(ownerRunId, cancellationToken).ConfigureAwait(false) : null;
            if (owner is { SelectedTest: { } ownerSuite } && !ModuleTestSelection.Compatible(ownerSuite, selection))
            {
                return Refuse("HXR034", "suite-incompatible", "The attached suite does not share the owner's module, mode, composition, runtime or resource requirements.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // Admission refuses unverifiable owner metadata with its stable reason.
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachRequest admission = new(ownerRunId, selection.Mode, selection.ManifestHash)
        {
            HoldSeconds = request?.HoldSeconds,
            PresentedRequestId = request?.PresentedRequestId,
            AttacherContext = request?.AttacherContext,
            Execution = new CompositionAttachedExecution(selection.Name, selection.Mode, selection.ManifestHash, selection.EntryHash, now),
        };
        ToolCommandResult admitted;
        try
        {
            admitted = await CompositionAttachAdmission.AdmitAsync(store, admission, now, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled("The attached run was cancelled before admission completed.");
        }

        if (admitted.Attachment is not { Execution: not null } accepted)
        {
            return admitted;
        }

        // The owner's issued identities are immutable; a cleanup completing after admission still drains this request.
        owner = await store.TryReadAsync(ownerRunId, CancellationToken.None).ConfigureAwait(false) ?? owner;
        if (owner is null)
        {
            return Refuse("HXR033", "owner-unknown", "The owner environment disappeared after admission.");
        }

        CompositionAttachment final = await AwaitAsync(options, stateDirectory, owner, accepted, selection.StopTimeoutSeconds, cancellationToken).ConfigureAwait(false);
        return Relay(stateDirectory, final);
    }

    private static async Task<CompositionAttachment> AwaitAsync(CompositionEngineOptions? options, string stateDirectory, CompositionRunState owner, CompositionAttachment accepted, int stopTimeoutSeconds, CancellationToken cancellationToken)
    {
        string directory = Path.Combine(stateDirectory, "diagnostics", owner.RunId);
        string id = accepted.AttachedRunId;
        TimeSpan configured = options?.HoldPollInterval ?? TimeSpan.FromSeconds(1);
        TimeSpan poll = configured > TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : configured;
        DateTimeOffset pickupDeadline = DateTimeOffset.UtcNow + (options?.AttachedPickupTimeout ?? TimeSpan.FromSeconds(30));
        TimeSpan hostGrace = TimeSpan.FromSeconds(2) + (poll * 4);
        TimeSpan stopAllowance = TimeSpan.FromSeconds(Math.Max(stopTimeoutSeconds, 1) + 45);
        CompositionRunResourceScanner scanner = new(options?.DockerCommand ?? "docker") { ProcessDiscoveryOverride = options?.AttachedProcessDiscoveryOverride };
        DateTimeOffset? cancelGiveUp = null;
        DateTimeOffset? hostLostAt = null;
        DateTimeOffset? terminatedAt = null;
        CompositionAttachment current = accepted;
        while (true)
        {
            try
            {
                current = (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, CancellationToken.None).ConfigureAwait(false)).FirstOrDefault(item => item.AttachedRunId == id) ?? current;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                // A transient read failure is retried; the owner's record remains authoritative.
            }

            DateTimeOffset now = DateTimeOffset.UtcNow;
            if ((current.Termination is not null && current.Execution?.ResultPublishedAtUtc is not null)
                || now > current.ExpiresAtUtc + stopAllowance + stopAllowance)
            {
                // The finite hold and stop allowances bound this wait; an unpublished result is reported, never assumed.
                return current;
            }

            AttachedSuiteSupervisorLease? lease = AttachedSuiteSupervisorLease.Read(directory);
            bool hostRunning = lease is not null && lease.OwnerRunId == owner.RunId && lease.HolderRunning();
            hostLostAt = hostRunning ? null : hostLostAt ?? now;
            terminatedAt = current.Termination is null ? null : terminatedAt ?? now;
            try
            {
                if (current.Execution?.PickedUpAtUtc is null)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        current = await AttachedSuiteTermination.AbandonAsync(stateDirectory, owner, id, "cancelled", "run-cancelled", ToolExitCode.Cancelled, "HXC130", CancellationToken.None).ConfigureAwait(false) ?? current;
                    }
                    else if (now >= pickupDeadline || (hostLostAt is { } lost && now - lost > hostGrace))
                    {
                        // The owner never accepted responsibility: the run fails without ever having a child or access.
                        current = await AttachedSuiteTermination.AbandonAsync(stateDirectory, owner, id, "failed", "startup-failure", ToolExitCode.TopologyOrLifecycle, "HXR033", CancellationToken.None).ConfigureAwait(false) ?? current;
                    }
                }
                else
                {
                    if (cancellationToken.IsCancellationRequested && cancelGiveUp is null)
                    {
                        cancelGiveUp = now + stopAllowance;
                        _ = await CompositionAttachmentStore.UpdateOneAsync(
                            directory,
                            owner.RunId,
                            id,
                            target => target.EndedAtUtc is null && target.Execution is { CancelRequestedAtUtc: null } execution
                                ? target with { Execution = execution with { CancelRequestedAtUtc = DateTimeOffset.UtcNow } }
                                : target,
                            CancellationToken.None).ConfigureAwait(false);
                    }

                    bool hostLost = hostLostAt is { } lostAt && now - lostAt > hostGrace;
                    bool overdue = (cancelGiveUp is { } giveUp && now > giveUp) || now > current.ExpiresAtUtc + stopAllowance
                        || (terminatedAt is { } recorded && now - recorded > TimeSpan.FromSeconds(15));
                    if (hostLost || overdue)
                    {
                        // The host vanished or never finished: this command stops its own tagged work, ends access and publishes.
                        using CancellationTokenSource stopping = new(stopAllowance);
                        current = await AttachedSuiteTermination.TerminateForeignAsync(scanner, stateDirectory, owner, id, "attacher", stopping.Token).ConfigureAwait(false) ?? current;
                        if (current.Termination is not null && current.Execution?.ResultPublishedAtUtc is null)
                        {
                            return current;
                        }
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or OperationCanceledException)
            {
                // Recording is retried on the next pass within the same finite allowances.
            }

            await Task.Delay(poll, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static ToolCommandResult Relay(string stateDirectory, CompositionAttachment final)
    {
        string resultPath = Path.Combine(stateDirectory, "diagnostics", final.AttachedRunId, "test-result.json");
        bool published = File.Exists(resultPath) && TestEnvironment.TestEnvironmentContractReader.ReadResult(resultPath).IsValid;
        CompositionAttachmentTermination? termination = final.Termination;
        string outcome = final.Outcome ?? "failed";
        string cause = final.Execution?.Cause ?? "unknown";
        string work = termination is { OwnedWork.Count: > 0 } ? termination.OwnedWork[0].Status : "pending";
        string access = termination is { EnvironmentAccess.Count: > 0 } ? termination.EnvironmentAccess[0].Status : "pending";
        List<ToolDiagnostic> diagnostics = [new("HXI013", ToolPhase.Test, ToolFailureCategory.None, $"Attached run {final.AttachedRunId} of owner {final.OwnerRunId} recorded {outcome} ({cause}); owned work {work}, environment access {access}.", "attachments")];
        if (termination is { } observed && observed.Failures.Count > 0)
        {
            diagnostics.Add(new ToolDiagnostic("HXR038", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, $"Attached run work or access could not be confirmed ended; leftovers: {string.Join(", ", observed.LeftoverWorkIds)}.", "attachments", "Run down for the owner; the original hold expiry is never extended."));
        }

        if (!published)
        {
            diagnostics.Add(new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The attached result could not be published or verified.", "evidence"));
        }

        int recordedExit = final.Execution?.ExitCode ?? (int)ToolExitCode.ProductOrTest;
        ToolExitCode failedExit = Enum.IsDefined(typeof(ToolExitCode), recordedExit) && recordedExit is not ((int)ToolExitCode.Success or (int)ToolExitCode.Cancelled)
            ? (ToolExitCode)recordedExit
            : ToolExitCode.ProductOrTest;
        ToolOutcome relayed = outcome switch
        {
            "passed" when published => ToolOutcome.Passed(),
            "passed" => ToolOutcome.Passed().Fail(ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "HXT008", ToolExitCode.EvidenceSchemaOrPolicy),
            "failed" => ToolOutcome.Passed().Fail(ToolPhase.Test, Category(failedExit), final.Execution?.RuleId ?? "HXT007", failedExit),
            _ => ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled),
        };
        string status = outcome;
        if (outcome == "passed")
        {
            status = published ? "completed" : "failed";
        }

        return new ToolCommandResult(status, relayed, diagnostics)
        {
            RunId = final.AttachedRunId,
            Attachment = final,
            TestResultPath = published ? resultPath : null,
        };
    }

    private static ToolFailureCategory Category(ToolExitCode exit) => exit switch
    {
        ToolExitCode.UsageOrManifest => ToolFailureCategory.Usage,
        ToolExitCode.PrerequisiteUnavailable => ToolFailureCategory.PrerequisiteUnavailable,
        ToolExitCode.TopologyOrLifecycle => ToolFailureCategory.TopologyOrLifecycle,
        ToolExitCode.EvidenceSchemaOrPolicy => ToolFailureCategory.EvidencePolicy,
        ToolExitCode.PersistedState => ToolFailureCategory.PersistedState,
        ToolExitCode.Success or ToolExitCode.ProductOrTest or ToolExitCode.Cancelled => ToolFailureCategory.ProductOrTest,
        _ => ToolFailureCategory.ProductOrTest,
    };

    private static ToolCommandResult Usage(string message)
    {
        ToolDiagnostic diagnostic = new("HXC002", ToolPhase.Usage, ToolFailureCategory.Usage, message, "profile");
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.UsageOrManifest), [diagnostic]);
    }

    private static ToolCommandResult Refuse(string ruleId, string reason, string message)
    {
        ToolDiagnostic diagnostic = new(ruleId, ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, message, reason);
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [diagnostic]);
    }

    private static ToolCommandResult Cancelled(string message)
    {
        ToolDiagnostic diagnostic = new("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, message, "attachments");
        return new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.Cancelled), [diagnostic]);
    }
}
