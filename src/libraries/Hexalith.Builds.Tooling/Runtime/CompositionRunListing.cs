// <copyright file="CompositionRunListing.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

#pragma warning disable IDE0046 // Guard clauses read better than nested conditionals.

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
                    ModuleTestRunRecord? committed = null;
                    try
                    {
                        committed = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestRunRecord>(Path.Combine(directory, "selected-test.json"), cancellationToken).ConfigureAwait(false);
                        if (committed is not null && !CompositionRunStateStore.ValidOutcome(committed, tracked.RunId))
                        {
                            _ = unverifiableOwners.Add(tracked.RunId);
                            committed = null;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                    }

                    CompositionRunState state = committed is null ? tracked : tracked with { TestRun = committed, CleanupDisposition = committed.CleanupDisposition, RemainingResources = committed.RemainingResources };
                    if (committed?.CleanupDisposition == "complete" && tracked.CleanupDisposition != "complete")
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                        state = state with { CleanupDisposition = "incomplete", RemainingResources = committed.RemainingResources with { Unverified = true, DiscoveryFailures = [.. committed.RemainingResources.DiscoveryFailures, "state-finalization"] } };
                    }

                    ModuleTestExecutionTiming? timing = null;
                    try
                    {
                        timing = await CompositionDocumentStore.ReadIfExistsAsync<ModuleTestExecutionTiming>(Path.Combine(directory, "execution-activation.json"), cancellationToken).ConfigureAwait(false) ?? state.TestRun?.ExecutionTiming;
                        if (timing is not null && (timing.EffectiveSeconds <= 0 || timing.DeadlineUtc - timing.StartedAtUtc != TimeSpan.FromSeconds(timing.EffectiveSeconds)))
                        {
                            _ = unverifiableOwners.Add(tracked.RunId);
                            timing = null;
                        }
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                    }

                    IReadOnlyList<CompositionAttachment> attachments = [];
                    bool legacy = state.EnvironmentId is null || state.CreatedAt is null;
                    try
                    {
                        attachments = await CompositionAttachmentStore.ReadAsync(directory, tracked.RunId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
                    {
                        _ = unverifiableOwners.Add(tracked.RunId);
                    }

                    attachments = [.. attachments.Select(item => item.ObservedAt(observedAt))];
                    CompositionAttachment[] live = [.. attachments.Where(item => item.IsLive(observedAt))];
                    bool local = state.ExecutionContext is { ValueKind: System.Text.Json.JsonValueKind.Object } context && context.TryGetProperty("kind", out System.Text.Json.JsonElement kind) && kind.ValueKind == System.Text.Json.JsonValueKind.String && kind.GetString() == "local";

                    // CI owners do not wait for holds in this version, so their cleanup is never reported as hold-deferred.
                    string cleanup = local && live.Length > 0 && state.TestRun?.TerminalOutcome.ExitCode == ToolExitCode.Success && state.CleanupDisposition == "not-requested" && Retention(state, attachments) != "failure-retained"
                        ? "pending"
                        : state.CleanupDisposition;
                    entries.Add(new CompositionRunListingEntry(
                        state.RunId,
                        state.EnvironmentId,
                        state.AttemptId,
                        state.CreatedAt,
                        state.CreatedAt is { } created ? Math.Max(0, (observedAt - created).TotalSeconds) : null,
                        [.. (state.TestRun?.TerminalOutcome.ExitCode is not (null or ToolExitCode.Success or ToolExitCode.Cancelled) ? new[] { state.RunId } : []).Concat(attachments.Where(item => item.Outcome == "failed").Select(item => item.AttachedRunId))],
                        Retention(state, attachments),
                        cleanup,
                        state.StartupDeadlineUtc,
                        timing?.DeadlineUtc,
                        [.. live.Select(item => item.AttachedRunId)],
                        AttachmentInformation(legacy, live.Length, attachments.Count),
                        live.Length > 0 ? live.Max(item => item.ExpiresAtUtc) : null,
                        state.RemainingResources ?? state.TestRun?.RemainingResources)
                    {
                        Attachments = attachments,
                    });
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
                {
                    _ = unverifiableOwners.Add(tracked.RunId);
                }
            }

            return (unverifiableOwners.Count > 0 ? Incomplete(unverifiableOwners) : new ToolCommandResult("completed", ToolOutcome.Passed(), [])) with { Environments = entries };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidDataException)
        {
            return Incomplete();
        }
    }

    /// <summary>Derives the recorded retention without inferring it from process liveness.</summary>
    /// <param name="state">The merged owner state.</param>
    /// <param name="attachments">The owner's recorded attachments, when available.</param>
    /// <returns>The retention label.</returns>
    internal static string Retention(CompositionRunState state, IReadOnlyList<CompositionAttachment>? attachments = null) => state.TestRun switch
    {
        _ when attachments?.Any(item => item.Outcome == "failed") == true => state.CleanupDisposition == "complete" ? "released" : "failure-retained",
        null => "unavailable",
        _ when state.CleanupDisposition == "complete" && state.TestRun.FailureRetained => "released",
        _ when state.CreatedAt is null => "unavailable",
        { FailureRetained: false } => "not-retained",
        _ => "failure-retained",
    };

    private static ToolCommandResult Incomplete(IEnumerable<string>? unverifiableOwners = null)
    {
        string[] ownerIds = [.. (unverifiableOwners ?? []).Where(CompositionRunPlanFactory.IsRunId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string owners = ownerIds.Length == 0 ? string.Empty : " Unverifiable owner run IDs: " + string.Join(", ", ownerIds) + ".";
        ToolDiagnostic diagnostic = new("HXR024", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "Tracked environment metadata cannot be verified; listing is incomplete." + owners, "state");
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle), [diagnostic]);
    }

    private static string AttachmentInformation(bool legacy, int live, int recorded)
    {
        if (legacy)
        {
            return "unavailable-legacy-state";
        }

        if (live > 0)
        {
            return "attached";
        }

        return recorded > 0 ? "ended-or-expired" : "none";
    }
}
