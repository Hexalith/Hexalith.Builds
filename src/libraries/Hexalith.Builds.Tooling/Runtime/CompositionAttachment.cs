// <copyright file="CompositionAttachment.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses read better than nested conditionals.

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Text.Json.Nodes;

/// <summary>One accepted attachment hold, bound to its owner environment and immutable once accepted.</summary>
/// <param name="OriginalAttachmentRequestId">The runner-issued lineage identity of the original request.</param>
/// <param name="EnvironmentId">The owner's environment identity.</param>
/// <param name="OwnerRunId">The owning run.</param>
/// <param name="AttachedRunId">The runner-issued attached run identity.</param>
/// <param name="RequestedHoldSeconds">The explicit request, or null when the request omitted a hold.</param>
/// <param name="AcceptedAtUtc">The acceptance time; the hold starts here.</param>
/// <param name="EffectiveHoldSeconds">The effective hold in whole seconds.</param>
/// <param name="ExpiresAtUtc">The absolute exclusive expiry.</param>
public sealed record CompositionAttachment(
    string OriginalAttachmentRequestId,
    string EnvironmentId,
    string OwnerRunId,
    string AttachedRunId,
    int? RequestedHoldSeconds,
    DateTimeOffset AcceptedAtUtc,
    int EffectiveHoldSeconds,
    DateTimeOffset ExpiresAtUtc)
{
    /// <summary>The hold used when a request omits one.</summary>
    public const int DefaultHoldSeconds = 1800;

    /// <summary>The longest accepted explicit hold.</summary>
    public const int MaximumHoldSeconds = 14400;

    /// <summary>Gets the recorded actual end time of this attachment, when it ended before being observed as expired.</summary>
    public DateTimeOffset? EndedAtUtc { get; init; }

    /// <summary>Gets the recorded end reason, when ended.</summary>
    public string? EndReason { get; init; }

    /// <summary>Gets the first terminal outcome, or null while the attachment is active.</summary>
    public string? Outcome { get; init; }

    /// <summary>Parses a hold request. Only an absent request means omission; nothing is clamped or defaulted on error.</summary>
    /// <param name="raw">The raw request, or null when omitted.</param>
    /// <param name="requested">The explicit whole seconds, or null for omission.</param>
    /// <param name="effective">The effective whole seconds.</param>
    /// <returns>Whether the request is valid.</returns>
    public static bool TryParseHold(string? raw, out int? requested, out int effective)
    {
        requested = null;
        effective = DefaultHoldSeconds;
        if (raw is null)
        {
            return true;
        }

        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out int seconds) || seconds is < 1 or > MaximumHoldSeconds)
        {
            return false;
        }

        requested = seconds;
        effective = seconds;
        return true;
    }

    /// <summary>Projects an unrecorded expiry without changing durable metadata.</summary>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <returns>The attachment with its observed first outcome.</returns>
    public CompositionAttachment ObservedAt(DateTimeOffset now) => EndedAtUtc is null && now >= ExpiresAtUtc
        ? this with { EndedAtUtc = ExpiresAtUtc, EndReason = "expired", Outcome = "withdrawn" }
        : this;

    /// <summary>Checks the internal consistency of a persisted attachment.</summary>
    /// <returns>Whether the immutable bindings agree.</returns>
    public bool IsConsistent() => CompositionRunPlanFactory.IsRunId(OriginalAttachmentRequestId)
        && CompositionRunPlanFactory.IsRunId(AttachedRunId) && CompositionRunPlanFactory.IsRunId(OwnerRunId)
        && CompositionRunPlanFactory.IsRunId(EnvironmentId)
        && AttachedRunId != OwnerRunId && OriginalAttachmentRequestId != AttachedRunId
        && EffectiveHoldSeconds is >= 1 and <= MaximumHoldSeconds
        && (RequestedHoldSeconds is null ? EffectiveHoldSeconds == DefaultHoldSeconds : RequestedHoldSeconds == EffectiveHoldSeconds)
        && ExpiresAtUtc - AcceptedAtUtc == TimeSpan.FromSeconds(EffectiveHoldSeconds)
        && (EndedAtUtc is null) == (EndReason is null)
        && HasConsistentOutcome()
        && (EndedAtUtc is null || (EndedAtUtc >= AcceptedAtUtc && EndReason is "terminal-outcome" or "expired" or "owner-cancelled" or "developer-stop" or "cleanup" or "ci-job-completed" or "ci-job-cancelled"));

    /// <summary>Gets whether the hold is still live at a time: not ended and before its exclusive expiry.</summary>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <returns>Whether the hold blocks cleanup.</returns>
    public bool IsLive(DateTimeOffset now) => EndedAtUtc is null && now < ExpiresAtUtc;

    /// <summary>Gets the observed state at a time.</summary>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <returns><c>live</c>, <c>expired</c> or <c>ended</c>.</returns>
    public string StateAt(DateTimeOffset now)
    {
        if (EndedAtUtc is not null)
        {
            return "ended";
        }

        return now >= ExpiresAtUtc ? "expired" : "live";
    }

    /// <summary>Projects this hold into the v1 test environment contract shape.</summary>
    /// <param name="now">The runner's UTC clock reading, used to report passive expiry.</param>
    /// <returns>The contract hold object.</returns>
    public JsonObject ToContract(DateTimeOffset now)
    {
        JsonObject end = new() { ["state"] = "active" };
        if (EndedAtUtc is { } ended)
        {
            end = new JsonObject { ["state"] = "ended", ["endedAt"] = Utc(ended), ["reason"] = EndReason };
        }
        else if (now >= ExpiresAtUtc)
        {
            end = new JsonObject { ["state"] = "ended", ["endedAt"] = Utc(ExpiresAtUtc), ["reason"] = "expired" };
        }

        JsonObject hold = new()
        {
            ["originalAttachmentRequestId"] = OriginalAttachmentRequestId,
            ["environmentId"] = EnvironmentId,
            ["ownerRunId"] = OwnerRunId,
            ["attachedRunId"] = AttachedRunId,
        };
        if (RequestedHoldSeconds is { } requested)
        {
            hold["requestedHoldSeconds"] = requested;
        }

        hold["acceptedAt"] = Utc(AcceptedAtUtc);
        hold["effectiveHoldSeconds"] = EffectiveHoldSeconds;
        hold["expiresAt"] = Utc(ExpiresAtUtc);
        hold["end"] = end;
        return hold;
    }

    /// <summary>Normalizes outcomes omitted by supported 2.6 terminal records before verification.</summary>
    /// <returns>The compatible first outcome without changing its end time or reason.</returns>
    internal CompositionAttachment NormalizeLegacyOutcome() => (EndedAtUtc, EndReason, Outcome) switch
    {
        (not null, "expired", null) => this with { Outcome = "withdrawn" },
        (not null, "terminal-outcome", null) => this with { Outcome = "unknown" },
        _ => this,
    };

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private bool HasConsistentOutcome() => (EndedAtUtc, EndReason, Outcome) switch
    {
        (null, _, null) => true,
        (not null, "terminal-outcome", null or "unknown" or "passed" or "failed" or "cancelled") => true,
        (not null, not "terminal-outcome", "withdrawn") => true,
        _ => false,
    };
}
