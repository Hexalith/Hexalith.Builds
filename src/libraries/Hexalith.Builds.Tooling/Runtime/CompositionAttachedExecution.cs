// <copyright file="CompositionAttachedExecution.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses with distinct stable outcomes read better than nested conditionals.

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>A declared suite the live owner runs as runner-owned attached work, recorded with its hold.</summary>
/// <param name="Profile">The declared suite name from the owner's manifest.</param>
/// <param name="Mode">The suite's artifact mode.</param>
/// <param name="ManifestHash">The declaring manifest bytes the attacher selected.</param>
/// <param name="EntryHash">The selected suite entry bytes.</param>
/// <param name="RequestedAtUtc">When the attacher handed the request to the owner.</param>
public sealed record CompositionAttachedExecution(string Profile, string Mode, string ManifestHash, string EntryHash, DateTimeOffset RequestedAtUtc)
{
    /// <summary>The internal execution kind of a runner-owned attached suite.</summary>
    public const string Kind = "attached-suite";

    /// <summary>Gets the attempt identity issued when the owner picked the request up.</summary>
    public string? AttemptId { get; init; }

    /// <summary>Gets when the live owner accepted responsibility for the request.</summary>
    public DateTimeOffset? PickedUpAtUtc { get; init; }

    /// <summary>Gets when the owner launched the attached child, if it did.</summary>
    public DateTimeOffset? LaunchedAtUtc { get; init; }

    /// <summary>Gets when the attaching command asked to cancel its own run.</summary>
    public DateTimeOffset? CancelRequestedAtUtc { get; init; }

    /// <summary>Gets the v1 cause recorded with the first terminal outcome.</summary>
    public string? Cause { get; init; }

    /// <summary>Gets the first failing rule, when the run failed.</summary>
    public string? RuleId { get; init; }

    /// <summary>Gets the relayed command exit code of the first outcome.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Gets when the attached result was durably published.</summary>
    public DateTimeOffset? ResultPublishedAtUtc { get; init; }

    /// <summary>Maps an end reason to its v1 withdrawal cause.</summary>
    /// <param name="reason">The recorded end reason.</param>
    /// <returns>The terminal cause, or null for an own terminal outcome.</returns>
    public static string? WithdrawalCause(string? reason) => reason switch
    {
        "expired" => "hold-expired",
        "owner-cancelled" or "developer-stop" or "cleanup" or "ci-job-completed" or "ci-job-cancelled" => reason,
        _ => null,
    };

    /// <summary>Checks the request bindings and the recorded cause against the first outcome.</summary>
    /// <param name="attachment">The owning attachment.</param>
    /// <returns>Whether the execution record agrees with its hold.</returns>
    public bool IsConsistent(CompositionAttachment attachment)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        bool bindings = Profile is { Length: > 0 and <= 256 } && Profile == Profile.Trim() && !Profile.Any(char.IsControl)
            && Mode is "source" or "package"
            && ManifestHash is { Length: 64 } && ManifestHash.All(char.IsAsciiHexDigit)
            && EntryHash is { Length: 64 } && EntryHash.All(char.IsAsciiHexDigit)
            && (AttemptId is null || (CompositionRunPlanFactory.IsRunId(AttemptId) && AttemptId != attachment.AttachedRunId && AttemptId != attachment.OwnerRunId))
            && (PickedUpAtUtc is null) == (AttemptId is null);
        if (!bindings)
        {
            return false;
        }

        if (attachment.Outcome is null)
        {
            return Cause is null;
        }

        return (attachment.Outcome, Cause) switch
        {
            ("passed", "tests-completed") => true,
            ("failed", "test-failure" or "startup-failure" or "startup-timeout" or "execution-timeout") => true,
            ("cancelled", "run-cancelled") => true,
            ("withdrawn", { } cause) => cause == WithdrawalCause(attachment.EndReason),
            (_, null) => attachment.Outcome is "unknown",
            _ => false,
        };
    }
}
