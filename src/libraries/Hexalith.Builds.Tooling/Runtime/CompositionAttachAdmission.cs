// <copyright file="CompositionAttachAdmission.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses with distinct stable refusal reasons read better than nested conditionals.

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Evaluates attachment admission, records accepted holds and ends individual attachments.</summary>
public static class CompositionAttachAdmission
{
    private static readonly string[] _jobIdentity = ["repository", "workflowRunId", "jobId"];

    /// <summary>Evaluates one attach request and records the accepted hold.</summary>
    /// <param name="store">The runner-owned state store.</param>
    /// <param name="request">The request.</param>
    /// <param name="now">The runner's UTC clock reading, which is the acceptance time.</param>
    /// <param name="cancellationToken">The allowance.</param>
    /// <returns>The accepted attachment or a stable refusal.</returns>
    public static async Task<ToolCommandResult> AdmitAsync(CompositionRunStateStore store, CompositionAttachRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(request);
        if (!CompositionAttachment.TryParseHold(request.HoldSeconds, out int? requestedHold, out int effectiveHold))
        {
            return Usage("hold-seconds", "The hold must be omitted or a whole number of seconds from 1 to 14400; it is never clamped or defaulted.");
        }

        if (!CompositionRunPlanFactory.IsRunId(request.OwnerRunId) || request.Mode is not ("source" or "package")
            || string.IsNullOrWhiteSpace(request.CompositionIdentity)
            || (request.PresentedRequestId is not null && !CompositionRunPlanFactory.IsRunId(request.PresentedRequestId))
            || (request.EnvironmentId is not null && !CompositionRunPlanFactory.IsRunId(request.EnvironmentId))
            || request.Target is not ("run-owned" or "hosted" or "developer-started"))
        {
            return Usage("request", "The owner run, mode, composition identity, target and any presented identities must be well formed.");
        }

        if (request.Target == "hosted")
        {
            return Refuse("HXR033", "hosted-target", "Hosted environments are not eligible for attachment.");
        }

        if (request.Target == "developer-started")
        {
            return Refuse("HXR033", "developer-started-target", "Separately developer-started environments are not eligible for attachment.");
        }

        CompositionRunState? owner;
        string directory = Path.Combine(store.Directory, "diagnostics", request.OwnerRunId);
        IReadOnlyList<CompositionAttachment> existing;
        try
        {
            owner = await ReadOwnerAsync(store, request.OwnerRunId, directory, cancellationToken).ConfigureAwait(false);
            existing = await CompositionAttachmentStore.ReadAsync(directory, request.OwnerRunId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refuse("HXR033", "owner-unverifiable", "The owner environment metadata cannot be verified.");
        }

        if (owner is null)
        {
            return Refuse("HXR033", "owner-unknown", "No runner-owned environment is tracked for this owner run.");
        }

        if (owner.EnvironmentId is null || owner.AttemptId is null || owner.CreatedAt is null || owner.SelectedTest is null || owner.ExecutionContext is null)
        {
            return Refuse("HXR033", "owner-not-attachable", "The owner state lacks the issued identities needed to verify an attachment.");
        }

        ToolCommandResult? context = CheckContext(owner, request);
        if (context is not null)
        {
            return context;
        }

        ToolCommandResult? ownerState = CheckOwnerState(owner, existing);
        if (ownerState is not null)
        {
            return ownerState;
        }

        if (request.PresentedRequestId is { } presented)
        {
            return Retry(existing.FirstOrDefault(item => item.OriginalAttachmentRequestId == presented || item.AttachedRunId == presented), now);
        }

        if (request.EnvironmentId is not null && request.EnvironmentId != owner.EnvironmentId)
        {
            return Refuse("HXR034", "environment-mismatch", "The requested environment identity does not match the owner.");
        }

        ToolCommandResult? compatibility = CheckCompatibility(owner, request);
        if (compatibility is not null)
        {
            return compatibility;
        }

        try
        {
            return await CompositionAttachmentStore.UpdateCoreAsync<ToolCommandResult>(
                directory,
                request.OwnerRunId,
                async current =>
                {
                    // The owner may have finished or started cleanup since it was first read; decide again under the lock.
                    CompositionRunState? latest = await ReadOwnerAsync(store, request.OwnerRunId, directory, cancellationToken).ConfigureAwait(false);
                    ToolCommandResult? finished = latest is null ? Refuse("HXR033", "owner-unknown", "No runner-owned environment is tracked for this owner run.") : CheckOwnerState(latest, current);
                    if (finished is not null)
                    {
                        return (finished, null);
                    }

                    CompositionAttachment created = new(
                        CompositionRunPlanFactory.NewRunId(),
                        owner.EnvironmentId,
                        owner.RunId,
                        CompositionRunPlanFactory.NewRunId(),
                        requestedHold,
                        now,
                        effectiveHold,
                        now.AddSeconds(effectiveHold));
                    return (Accepted(created, retry: false), [.. current, created]);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refuse("HXR033", "attachment-unrecordable", "The accepted hold could not be durably recorded; nothing was attached.");
        }
    }

    /// <summary>Ends exactly one attachment; the owner and every other attachment are untouched.</summary>
    /// <param name="store">The runner-owned state store.</param>
    /// <param name="ownerRunId">The owner run.</param>
    /// <param name="attachedRunId">The runner-issued attached run identity (or its original request identity).</param>
    /// <param name="outcome">The attached run's reported terminal outcome.</param>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <param name="cancellationToken">The allowance.</param>
    /// <returns>The ended attachment or a stable refusal.</returns>
    public static async Task<ToolCommandResult> EndAsync(CompositionRunStateStore store, string ownerRunId, string attachedRunId, string? outcome, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (outcome is not ("passed" or "failed" or "cancelled"))
        {
            return Usage("outcome", "Ending an attachment requires outcome passed, failed or cancelled.");
        }

        if (!CompositionRunPlanFactory.IsRunId(ownerRunId) || !CompositionRunPlanFactory.IsRunId(attachedRunId))
        {
            return Usage("request", "The owner run and attached run identities must be well formed.");
        }

        string directory = Path.Combine(store.Directory, "diagnostics", ownerRunId);
        try
        {
            if (!CompositionRunStateStore.IsVerified(await store.TryReadAsync(ownerRunId, cancellationToken).ConfigureAwait(false), ownerRunId))
            {
                return Refuse("HXR033", "owner-unknown", "No verified runner-owned environment is tracked for this owner run.");
            }

            CompositionAttachment? ended = await CompositionAttachmentStore.UpdateAsync<CompositionAttachment?>(
                directory,
                ownerRunId,
                current =>
                {
                    CompositionAttachment? target = current.FirstOrDefault(item => item.AttachedRunId == attachedRunId || item.OriginalAttachmentRequestId == attachedRunId);
                    if (target is null)
                    {
                        return (null, null);
                    }

                    if (target.EndedAtUtc is not null)
                    {
                        return (target, null);
                    }

                    CompositionAttachment updated = target with { EndedAtUtc = EndTime(target, now), EndReason = now >= target.ExpiresAtUtc ? "expired" : "terminal-outcome", Outcome = now >= target.ExpiresAtUtc ? "withdrawn" : outcome };
                    return (updated, [.. current.Select(item => ReferenceEquals(item, target) ? updated : item)]);
                },
                cancellationToken).ConfigureAwait(false);
            return ended is null
                ? Refuse("HXR036", "unknown-attachment", "No attachment with this identity was issued for the environment.")
                : new ToolCommandResult("ended", ToolOutcome.Passed(), []) { Attachment = ended };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return Refuse("HXR033", "attachment-unrecordable", "The attachment end could not be durably recorded.");
        }
    }

    private static DateTimeOffset EndTime(CompositionAttachment target, DateTimeOffset now)
    {
        if (now >= target.ExpiresAtUtc)
        {
            return target.ExpiresAtUtc;
        }

        return now < target.AcceptedAtUtc ? target.AcceptedAtUtc : now;
    }

    private static async Task<CompositionRunState?> ReadOwnerAsync(CompositionRunStateStore store, string ownerRunId, string directory, CancellationToken cancellationToken)
    {
        CompositionRunState? tracked = await store.TryReadAsync(ownerRunId, cancellationToken).ConfigureAwait(false);
        if (tracked is null)
        {
            return File.Exists(store.PathFor(ownerRunId)) ? throw new IOException("Owner state cannot be verified.") : null;
        }

        if (!CompositionRunStateStore.IsVerified(tracked, ownerRunId))
        {
            throw new IOException("Owner state cannot be verified.");
        }

        ModuleTestRunRecord? committed = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), cancellationToken).ConfigureAwait(false);
        if (committed is not null && !CompositionRunStateStore.ValidOutcome(committed, ownerRunId))
        {
            throw new IOException("Owner outcome cannot be verified.");
        }

        return committed is null ? tracked : tracked with { TestRun = committed, CleanupDisposition = committed.CleanupDisposition, RemainingResources = committed.RemainingResources };
    }

    private static ToolCommandResult? CheckContext(CompositionRunState owner, CompositionAttachRequest request)
    {
        JsonElement attacher;
        try
        {
            attacher = request.AttacherContext ?? JsonSerializer.SerializeToElement(TestEnvironment.TestEnvironmentDescriptorWriter.ExecutionContext());
        }
        catch (InvalidDataException)
        {
            return Refuse("HXR033", "attacher-context-unverifiable", "The attaching process has incomplete CI identity; it cannot be bound to the owner's job.");
        }

        JsonElement recorded = owner.ExecutionContext!.Value;
        string? ownerKind = Text(recorded, "kind");
        string? attacherKind = Text(attacher, "kind");
        if (ownerKind is not ("local" or "ci"))
        {
            return Refuse("HXR033", "owner-context-unsupported", "The owner's recorded execution context is neither local nor CI.");
        }

        if (ownerKind != attacherKind)
        {
            return Refuse("HXR033", "cross-context", "The attaching run is not in the same execution context as the owner.");
        }

        bool sameJob = ownerKind == "local"
            || (_jobIdentity.All(name => Text(recorded, name) is { } value && value == Text(attacher, name)) && SameAttempt(recorded, attacher));
        return sameJob ? null : Refuse("HXR033", "cross-job", "CI attachment must stay within the owner's workflow run, attempt and job.");
    }

    private static bool SameAttempt(JsonElement recorded, JsonElement attacher) => Text(recorded, "kind") != "ci"
        || (recorded.TryGetProperty("workflowAttempt", out JsonElement left) && attacher.TryGetProperty("workflowAttempt", out JsonElement right)
            && left.ValueKind == JsonValueKind.Number && right.ValueKind == JsonValueKind.Number && left.GetRawText() == right.GetRawText());

    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
        ? value.GetString()
        : null;

    private static ToolCommandResult Retry(CompositionAttachment? lineage, DateTimeOffset now)
    {
        if (lineage is null)
        {
            return Refuse("HXR036", "unknown-attachment-request", "The presented attachment identity was not issued for this environment.");
        }

        string? ended = lineage.EndedAtUtc is not null ? "attachment-ended" : null;
        if (ended is null && now >= lineage.ExpiresAtUtc)
        {
            ended = "attachment-expired";
        }

        if (ended is not null)
        {
            return Refuse("HXR036", ended, ended == "attachment-ended" ? "The attachment lineage has ended; a fresh request needs admission." : "The attachment lineage has expired; its expiry is immutable.");
        }

        return Accepted(lineage, retry: true);
    }

    private static bool IsLocal(CompositionRunState owner) => owner.ExecutionContext is { } context && Text(context, "kind") == "local";

    private static string? FinishedOwnerReason(CompositionRunState owner, IReadOnlyList<CompositionAttachment> attachments)
    {
        if (owner.CleanupDisposition == "complete")
        {
            return "owner-cleaned";
        }

        if (owner.CleanupDisposition is "pending" or "in-progress" or "incomplete")
        {
            return "owner-cleaning";
        }

        if (IsLocal(owner) && attachments.Any(item => item.Outcome == "failed"))
        {
            return null;
        }

        if (owner.TestRun is null)
        {
            return owner.Status switch
            {
                CompositionRunStatus.Ready => null,
                CompositionRunStatus.Starting => "owner-starting",
                CompositionRunStatus.Cancelled => "owner-cancelled",
                CompositionRunStatus.Failed => "owner-failed",
                _ => "owner-finished",
            };
        }

        if (IsLocal(owner) && CompositionRunListing.Retention(owner) == "failure-retained" && owner.TestRun.ResourceDisposition == "retained")
        {
            return null;
        }

        return owner.TestRun.TerminalOutcome.ExitCode switch
        {
            ToolExitCode.Success => "owner-passed",
            ToolExitCode.Cancelled => owner.TestRun.TerminalStatus == "withdrawn" ? "owner-withdrawn" : "owner-cancelled",
            ToolExitCode.UsageOrManifest or ToolExitCode.PrerequisiteUnavailable or ToolExitCode.TopologyOrLifecycle
                or ToolExitCode.ProductOrTest or ToolExitCode.PersistedState or ToolExitCode.EvidenceSchemaOrPolicy => "owner-failed",
            _ => "owner-finished",
        };
    }

    private static ToolCommandResult? CheckOwnerState(CompositionRunState owner, IReadOnlyList<CompositionAttachment> attachments)
    {
        if (!IsLocal(owner))
        {
            return Refuse("HXR035", "owner-ci-holds-unsupported", "CI owners do not wait for holds in this version, so an attachment to a CI owner cannot be honored.");
        }

        string? reason = FinishedOwnerReason(owner, attachments);
        string message = reason == "owner-starting" ? "The owner environment is not ready yet." : "The owner environment is finished, stopped, being cleaned or not failure-retained.";
        return reason is null ? null : Refuse("HXR035", reason, message);
    }

    private static ToolCommandResult? CheckCompatibility(CompositionRunState owner, CompositionAttachRequest request)
    {
        ModuleTestSelection selected = owner.SelectedTest!;
        if (!string.Equals(selected.Mode, request.Mode, StringComparison.Ordinal))
        {
            return Refuse("HXR034", "mode-mismatch", "The requested artifact mode differs from the owner's.");
        }

        if (!string.Equals(selected.ManifestHash, request.CompositionIdentity, StringComparison.OrdinalIgnoreCase))
        {
            return Refuse("HXR034", "composition-mismatch", "The requested composition identity differs from the owner's.");
        }

        if (request.ArtifactIdentities.Count > 0)
        {
            return Refuse("HXR034", "artifact-identity-unverifiable", "The owner records no expected or loaded artifact identities that could match the request.");
        }

        if (owner.ReadinessResults.IsDefaultOrEmpty || owner.ReadinessResults.Any(resource => resource.State != "Healthy")
            || (owner.TestRun is null && owner.Status != CompositionRunStatus.Ready))
        {
            return Refuse("HXR034", "not-ready", "The owner environment is not recorded as ready with every resource healthy.");
        }

        bool isolated = owner.RemainingResources is not { Unverified: true } && owner.EnvironmentId != owner.RunId && owner.AttemptId != owner.EnvironmentId;
        return isolated ? null : Refuse("HXR034", "isolation-unverifiable", "The owner's run-scoped isolation cannot be verified.");
    }

    private static ToolCommandResult Accepted(CompositionAttachment attachment, bool retry) => new(retry ? "attach-retried" : "attached", ToolOutcome.Passed(), [])
    {
        Attachment = attachment,
    };

    private static ToolCommandResult Usage(string field, string message)
    {
        ToolDiagnostic diagnostic = new("HXR032", ToolPhase.Usage, ToolFailureCategory.Usage, message, field);
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.UsageOrManifest), [diagnostic]);
    }

    private static ToolCommandResult Refuse(string ruleId, string reason, string message)
    {
        ToolDiagnostic diagnostic = new(ruleId, ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, message, reason);
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [diagnostic]);
    }
}
