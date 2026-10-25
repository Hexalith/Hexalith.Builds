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
            HashSet<string> unverifiableOwners = new(StringComparer.Ordinal);
            IReadOnlyList<CompositionRunState> states = await store.EnumerateAsync(runId => unverifiableOwners.Add(runId), cancellationToken).ConfigureAwait(false);
            List<CompositionRunListingEntry> entries = [];
            foreach (CompositionRunState tracked in states)
            {
                try
                {
                    string directory = Path.Combine(store.Directory, "diagnostics", tracked.RunId);
                    ModuleTestRunRecord? committed = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), cancellationToken).ConfigureAwait(false);
                    if (committed is not null && !CompositionRunStateStore.ValidOutcome(committed, tracked.RunId))
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                        continue;
                    }

                    CompositionRunState state = committed is null ? tracked : tracked with { TestRun = committed, CleanupDisposition = committed.CleanupDisposition, RemainingResources = committed.RemainingResources };
                    if (committed?.CleanupDisposition == "complete" && tracked.CleanupDisposition != "complete")
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                        state = state with { CleanupDisposition = "incomplete", RemainingResources = committed.RemainingResources with { Unverified = true, DiscoveryFailures = [.. committed.RemainingResources.DiscoveryFailures, "state-finalization"] } };
                    }

                    ModuleTestExecutionTiming? timing = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestExecutionTiming>(Path.Combine(directory, "execution-activation.json"), cancellationToken).ConfigureAwait(false) ?? state.TestRun?.ExecutionTiming;
                    if (timing is not null && (timing.EffectiveSeconds <= 0 || timing.DeadlineUtc - timing.StartedAtUtc != TimeSpan.FromSeconds(timing.EffectiveSeconds)))
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                        continue;
                    }

                    entries.Add(new CompositionRunListingEntry(
                        state.RunId,
                        state.EnvironmentId,
                        state.AttemptId,
                        state.CreatedAt,
                        state.CreatedAt is { } created ? Math.Max(0, (observedAt - created).TotalSeconds) : null,
                        state.TestRun?.TerminalOutcome.ExitCode is not (null or ToolExitCode.Success or ToolExitCode.Cancelled) ? [state.RunId] : [],
                        Retention(state),
                        state.CleanupDisposition,
                        state.StartupDeadlineUtc,
                        timing?.DeadlineUtc,
                        [],
                        "unavailable-admission-unsupported",
                        null,
                        state.RemainingResources ?? state.TestRun?.RemainingResources));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _ = unverifiableOwners.Add(tracked.RunId);
                }
            }

            return (unverifiableOwners.Count > 0 ? Incomplete(unverifiableOwners) : new ToolCommandResult("completed", ToolOutcome.Passed(), [])) with { Environments = entries };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return Incomplete();
        }
    }

    private static ToolCommandResult Incomplete(IEnumerable<string>? unverifiableOwners = null)
    {
        string[] ownerIds = [.. (unverifiableOwners ?? []).Where(CompositionRunPlanFactory.IsRunId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string owners = ownerIds.Length == 0 ? string.Empty : " Unverifiable owner run IDs: " + string.Join(", ", ownerIds) + ".";
        ToolDiagnostic diagnostic = new("HXR024", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "Tracked environment metadata cannot be verified; listing is incomplete." + owners, "state");
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [diagnostic]);
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
