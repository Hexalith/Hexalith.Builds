// <copyright file="CompositionAttachmentTermination.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The runner's recorded termination of one attachment's owned work and environment access.</summary>
/// <param name="ObservedAtUtc">When the runner recorded the termination.</param>
/// <param name="OwnedWork">The owned process and task observations.</param>
/// <param name="EnvironmentAccess">The environment access observations.</param>
/// <param name="LeftoverWorkIds">Surviving processes or undeletable access files; never extended or hidden.</param>
public sealed record CompositionAttachmentTermination(
    DateTimeOffset ObservedAtUtc,
    IReadOnlyList<CompositionTerminationItem> OwnedWork,
    IReadOnlyList<CompositionTerminationItem> EnvironmentAccess,
    IReadOnlyList<string> LeftoverWorkIds)
{
    /// <summary>Gets which runner component recorded the termination: <c>owner</c>, <c>down</c>, <c>attacher</c> or <c>hold-only</c>.</summary>
    public string Recorder { get; init; } = "owner";

    /// <summary>Gets the expiry of the bearer handed only to the attached child, when one was minted.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }

    /// <summary>Gets the failed item identities.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Failures => [.. OwnedWork.Concat(EnvironmentAccess).Where(item => item.Status == "failed").Select(item => item.Id)];

    /// <summary>Creates the termination of a hold that never owned runner work or access.</summary>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="observedAt">The observation time.</param>
    /// <param name="recorder">The recording component.</param>
    /// <returns>The not-applicable termination.</returns>
    public static CompositionAttachmentTermination NotApplicable(string attachedRunId, DateTimeOffset observedAt, string recorder = "hold-only") => new(
        observedAt,
        [new CompositionTerminationItem("work:" + attachedRunId, "not-applicable", observedAt, "The runner owned no work for this attachment.")],
        [new CompositionTerminationItem("access:" + attachedRunId, "not-applicable", observedAt, "The runner issued no environment access for this attachment.")],
        [])
    {
        Recorder = recorder,
    };

    /// <inheritdoc/>
    public bool Equals(CompositionAttachmentTermination? other) => other is not null
        && ObservedAtUtc == other.ObservedAtUtc && Recorder == other.Recorder && CredentialExpiresAtUtc == other.CredentialExpiresAtUtc
        && OwnedWork.SequenceEqual(other.OwnedWork) && EnvironmentAccess.SequenceEqual(other.EnvironmentAccess)
        && LeftoverWorkIds.SequenceEqual(other.LeftoverWorkIds, StringComparer.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(ObservedAtUtc, Recorder, OwnedWork.Count, EnvironmentAccess.Count, LeftoverWorkIds.Count);

    /// <summary>Checks that the termination can be projected into the v1 result without ambiguity.</summary>
    /// <returns>Whether identities are unique and every leftover is reported by a failed item.</returns>
    public bool IsConsistent()
    {
        CompositionTerminationItem[] items = [.. OwnedWork ?? [], .. EnvironmentAccess ?? []];
        return OwnedWork is { Count: > 0 } && EnvironmentAccess is { Count: > 0 } && LeftoverWorkIds is not null
            && items.All(item => item?.IsConsistent() == true)
            && items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == items.Length
            && LeftoverWorkIds.All(id => id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl))
            && LeftoverWorkIds.Distinct(StringComparer.Ordinal).Count() == LeftoverWorkIds.Count
            && (LeftoverWorkIds.Count == 0 || items.Any(item => item.Status == "failed"))
            && Recorder is "owner" or "down" or "attacher" or "hold-only";
    }
}
