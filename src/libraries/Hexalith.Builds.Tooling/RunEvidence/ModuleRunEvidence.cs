// <copyright file="ModuleRunEvidence.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents the deterministic metadata-only <c>hexalith.module-run-evidence.v1</c> artifact.
/// </summary>
/// <param name="Schema">The evidence schema identity.</param>
/// <param name="RunId">The invocation-scoped run identity.</param>
/// <param name="Timestamps">The volatile invocation timestamps.</param>
/// <param name="Environment">The captured build and runtime environment metadata.</param>
/// <param name="Invocation">The normalized command invocation metadata.</param>
/// <param name="Topology">The validated module and platform topology metadata.</param>
/// <param name="PhaseOutcomes">The phase outcomes in execution order.</param>
/// <param name="TestCounts">The native test result summary when a test runner reported one.</param>
/// <param name="ArtifactHashes">The repository-relative artifact hash inventory.</param>
/// <param name="FinalStatus">The final invocation status.</param>
/// <param name="Outcome">The first causal tool outcome.</param>
/// <param name="VolatileFields">The fields excluded by semantic evidence comparisons.</param>
public sealed record ModuleRunEvidence(
    string Schema,
    string RunId,
    ModuleRunTimestamps Timestamps,
    ModuleRunEnvironment Environment,
    ModuleRunInvocation Invocation,
    ModuleRunTopology Topology,
    IReadOnlyList<ModuleRunPhaseOutcome> PhaseOutcomes,
    ModuleRunTestCounts TestCounts,
    IReadOnlyDictionary<string, string> ArtifactHashes,
    string FinalStatus,
    ToolOutcome Outcome,
    IReadOnlyList<string> VolatileFields);