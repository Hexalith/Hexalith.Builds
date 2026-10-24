// <copyright file="CompositionRunListing.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Lists durable owner environments without loading mutable manifests or starting resources.</summary>
public static class CompositionRunListing
{
    /// <summary>Reads tracked owner metadata and preserves unknown legacy fields.</summary>
    /// <param name="store">The runner-owned state store.</param>
    /// <param name="observedAt">The listing observation time.</param>
    /// <param name="cancellationToken">The read cancellation.</param>
    /// <returns>A safe command result, including a refusal if tracked metadata cannot be verified.</returns>
    public static async Task<ToolCommandResult> ReadAsync(CompositionRunStateStore store, DateTimeOffset observedAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            IReadOnlyList<CompositionRunState> states = await store.EnumerateAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<CompositionRunListingEntry> entries = [.. states.Select(state => new CompositionRunListingEntry(
                state.RunId,
                state.EnvironmentId,
                state.AttemptId,
                state.CreatedAt,
                state.CreatedAt is { } created ? Math.Max(0, (observedAt - created).TotalSeconds) : null,
                state.TestRun?.TerminalOutcome.ExitCode is not (null or ToolExitCode.Success or ToolExitCode.Cancelled) ? [state.RunId] : [],
                Retention(state),
                state.CleanupDisposition,
                state.StartupDeadlineUtc,
                state.TestRun?.ExecutionTiming?.DeadlineUtc,
                [],
                "unavailable-admission-unsupported",
                null,
                state.RemainingResources ?? state.TestRun?.RemainingResources)),];
            return new ToolCommandResult("completed", ToolOutcome.Passed(), []) { Environments = entries };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ToolDiagnostic diagnostic = new("HXR024", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "Tracked environment metadata cannot be verified; listing is incomplete.", "state");
            return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [diagnostic]);
        }
    }

    private static string Retention(CompositionRunState state) => state.TestRun switch
    {
        null => "unavailable",
        { ResourceDisposition: "retained" } => "failure-retained",
        _ when state.CreatedAt is null => "unavailable",
        { FailureRetained: false } => "not-retained",
        _ when state.CleanupDisposition == "complete" => "released",
        _ => "failure-retained",
    };
}
