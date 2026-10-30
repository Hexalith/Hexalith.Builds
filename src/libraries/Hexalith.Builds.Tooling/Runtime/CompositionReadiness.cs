// <copyright file="CompositionReadiness.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The metadata-only readiness document written by the AppHost once every resource is healthy.
/// </summary>
/// <param name="Schema">The readiness schema identity.</param>
/// <param name="RunId">The run identity the AppHost composed.</param>
/// <param name="EventStoreEndpoint">The EventStore HTTP endpoint.</param>
/// <param name="UiEndpoint">The FrontComposer UI host HTTP endpoint, when the run declares UI markers.</param>
/// <param name="Resources">The deterministically ordered resource readiness records.</param>
public sealed record CompositionReadiness(
    string Schema,
    string RunId,
    Uri EventStoreEndpoint,
    Uri? UiEndpoint,
    IReadOnlyList<CompositionResourceReadiness> Resources)
{
    /// <summary>
    /// The supported readiness schema identity.
    /// </summary>
    public const string SupportedSchema = "hexalith.g4-run-readiness.v1";

    /// <summary>Gets the internal refresh request satisfied by this fresh module-health snapshot.</summary>
    public string? RefreshId { get; init; }

    /// <summary>Gets actual selected-composition test measurements and scoped access, when supported.</summary>
    public CompositionTestObservation? TestObservation { get; init; }

    /// <summary>Gets actual owner-derived consumer scopes after startup preparation; this never grants an administrator role.</summary>
    public CompositionNativeTestScope? NativeTestScope { get; init; }

    /// <summary>Gets the effective deadline in seconds.</summary>
    public double? EffectiveDeadlineSeconds { get; init; }

    /// <summary>Gets a value indicating whether a module declared the deadline.</summary>
    public bool EffectiveDeadlineIsOverride { get; init; }

    /// <summary>Gets the override justification, when one was declared.</summary>
    public string? EffectiveDeadlineJustification { get; init; }

    /// <summary>Gets the module that declared the override.</summary>
    public string? EffectiveDeadlineModuleId { get; init; }

    /// <summary>Gets the elapsed seconds since the start request, when the runner has measured them.</summary>
    public double? StartupDurationSeconds { get; init; }

    /// <summary>Gets the immutable selected module-owned test binding, when present.</summary>
    public ModuleTestSelection? SelectedTest { get; init; }

    /// <summary>Gets actual required startup task observations.</summary>
    public IReadOnlyList<CompositionStartupTaskResult> StartupTaskResults { get; init; } = [];

    /// <summary>Gets the readiness observation time.</summary>
    public DateTimeOffset? ObservedAt { get; init; }
}
