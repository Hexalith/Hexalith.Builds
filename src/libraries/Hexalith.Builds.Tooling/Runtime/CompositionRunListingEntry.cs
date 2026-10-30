// <copyright file="CompositionRunListingEntry.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>A metadata-only view of one tracked environment with explicit legacy unknowns.</summary>
/// <param name="OwnerRunId">The durable owning run.</param>
/// <param name="EnvironmentId">The environment identity, unavailable for legacy states.</param>
/// <param name="AttemptId">The producing attempt, unavailable for legacy states.</param>
/// <param name="CreatedAt">The stable original request time.</param>
/// <param name="AgeSeconds">Age since the original request, unavailable for legacy states.</param>
/// <param name="FailureRunIds">Runs that established failure retention.</param>
/// <param name="Retention">Recorded retention, never inferred from process liveness.</param>
/// <param name="Cleanup">The latest recorded cleanup state.</param>
/// <param name="StartupDeadlineUtc">The original startup deadline.</param>
/// <param name="ExecutionDeadlineUtc">The independent execution deadline, when activated.</param>
/// <param name="AttachedRunIds">Attached runs whose holds are still live.</param>
/// <param name="AttachmentInformation">The attachment observation: <c>none</c>, <c>attached</c>, <c>ended-or-expired</c> or an explicit unknown.</param>
/// <param name="HoldDeadlineUtc">The latest absolute deadline among live holds, which is when deferred cleanup may run.</param>
/// <param name="RemainingResources">The latest recorded resource observation.</param>
public sealed record CompositionRunListingEntry(
    string OwnerRunId,
    string? EnvironmentId,
    string? AttemptId,
    DateTimeOffset? CreatedAt,
    double? AgeSeconds,
    IReadOnlyList<string> FailureRunIds,
    string Retention,
    string Cleanup,
    DateTimeOffset? StartupDeadlineUtc,
    DateTimeOffset? ExecutionDeadlineUtc,
    IReadOnlyList<string> AttachedRunIds,
    string AttachmentInformation,
    DateTimeOffset? HoldDeadlineUtc,
    CompositionRunResources? RemainingResources)
{
    /// <summary>Gets every recorded attachment with its absolute deadlines.</summary>
    public IReadOnlyList<CompositionAttachment> Attachments { get; init; } = [];
}
