// <copyright file="CompositionRunState.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

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
}