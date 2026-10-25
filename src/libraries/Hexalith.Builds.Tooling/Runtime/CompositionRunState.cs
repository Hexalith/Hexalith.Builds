// <copyright file="CompositionRunState.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Immutable;
using System.Text.Json.Serialization;

/// <summary>
/// Metadata-only, runner-owned state for one run, keyed by its run identity.
/// </summary>
/// <param name="Schema">The state schema identity.</param>
/// <param name="RunId">The run identity.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="ManifestHash">The SHA-256 fingerprint of the manifest bytes.</param>
/// <param name="Workspace">The runner-owned run workspace.</param>
/// <param name="AppHostProcessId">The AppHost process identity, when started.</param>
/// <param name="AppHostStartedAt">The AppHost process start time, used to reject a reused process identity.</param>
/// <param name="RuleId">The stable rule identity of a failure or cancellation.</param>
/// <param name="UpdatedAt">The last update timestamp.</param>
public sealed record CompositionRunState(
    string Schema,
    string RunId,
    CompositionRunStatus Status,
    string ManifestHash,
    string Workspace,
    int? AppHostProcessId,
    DateTimeOffset? AppHostStartedAt,
    string? RuleId,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// The supported state schema identity.
    /// </summary>
    public const string SupportedSchema = "hexalith.g4-run-state.v1";

    /// <summary>Gets the immutable environment request time, unavailable on legacy state.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>Gets the original actual execution context, unavailable on legacy state.</summary>
    public System.Text.Json.JsonElement? ExecutionContext { get; init; }

    /// <summary>Gets the runner-issued attempt identity, unavailable on legacy state.</summary>
    public string? AttemptId { get; init; }

    /// <summary>Gets the runner-issued environment identity, unavailable on legacy state.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Gets the original independent startup deadline.</summary>
    public DateTimeOffset? StartupDeadlineUtc { get; init; }

    /// <summary>Gets a value indicating whether this owner applies local failure retention.</summary>
    public bool RetainOnFailure { get; init; }

    /// <summary>Gets the last observed cleanup remainder, unavailable before inspection.</summary>
    public CompositionRunResources? RemainingResources { get; init; }

    /// <summary>Gets the recorded cleanup disposition.</summary>
    public string CleanupDisposition { get; init; } = "not-requested";

    /// <summary>Gets the elapsed seconds from the start request until this state was recorded.</summary>
    public double? StartupDurationSeconds { get; init; }

    /// <summary>Gets the per-resource readiness results observed for this run.</summary>
    [JsonConverter(typeof(ImmutableArrayJsonConverter<CompositionResourceReadiness>))]
    public ImmutableArray<CompositionResourceReadiness> ReadinessResults { get; init; } = [];

    /// <summary>Gets the effective startup deadline in seconds.</summary>
    public double? EffectiveDeadlineSeconds { get; init; }

    /// <summary>Gets a value indicating whether a module declared the deadline.</summary>
    public bool EffectiveDeadlineIsOverride { get; init; }

    /// <summary>Gets the override justification, when one was declared.</summary>
    public string? EffectiveDeadlineJustification { get; init; }

    /// <summary>Gets the module that declared the selected override.</summary>
    public string? EffectiveDeadlineModuleId { get; init; }

    /// <summary>Gets the diagnostics directory preserved outside the run workspace.</summary>
    public string? DiagnosticsPath { get; init; }

    /// <summary>Gets project process ids reported for debugger attach.</summary>
    [JsonConverter(typeof(ImmutableArrayJsonConverter<int>))]
    public ImmutableArray<int> ProjectProcessIds { get; init; } = [];

    /// <summary>Gets the immutable selected module-owned test binding, when present.</summary>
    public ModuleTestSelection? SelectedTest { get; init; }

    /// <summary>Gets the first terminal selected-test record, preserved through later cancellation and cleanup.</summary>
    public ModuleTestRunRecord? TestRun { get; init; }
}