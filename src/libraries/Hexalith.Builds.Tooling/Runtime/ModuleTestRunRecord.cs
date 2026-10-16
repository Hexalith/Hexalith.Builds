// <copyright file="ModuleTestRunRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Durable metadata for a selected test; declared candidate identity never claims loaded backend evidence.</summary>
/// <param name="RunId">The runner identity.</param>
/// <param name="OwnerId">The owning invocation identity.</param>
/// <param name="Selection">The immutable source and timing binding.</param>
/// <param name="SourceMappingHash">The selected mapping hash.</param>
/// <param name="DeclaredPlatformCommit">The expected Platform identity, when available.</param>
/// <param name="CheckoutPlatformCommit">The checkout identity, when available.</param>
/// <param name="ObservedCandidateHash">The actually observed candidate hash, absent until supported.</param>
/// <param name="ObservedBackendIdentity">The actually observed backend identity, absent until supported.</param>
/// <param name="QualificationCredit">Whether this record confers qualification credit; currently always false.</param>
/// <param name="Resources">Actual readiness observations only.</param>
/// <param name="TerminalStatus">The first terminal status.</param>
/// <param name="TerminalOutcome">The first causal outcome.</param>
/// <param name="TerminalAtUtc">The first terminal time.</param>
/// <param name="ResourceDisposition">Retained, removed, remaining, or unknown.</param>
/// <param name="RemainingResources">The observed cleanup remainder, including scan uncertainty.</param>
public sealed record ModuleTestRunRecord(
    string RunId,
    string OwnerId,
    ModuleTestSelection Selection,
    string? SourceMappingHash,
    string? DeclaredPlatformCommit,
    string? CheckoutPlatformCommit,
    string? ObservedCandidateHash,
    string? ObservedBackendIdentity,
    bool QualificationCredit,
    IReadOnlyList<CompositionResourceReadiness> Resources,
    string TerminalStatus,
    ToolOutcome TerminalOutcome,
    DateTimeOffset TerminalAtUtc,
    string ResourceDisposition,
    CompositionRunResources RemainingResources)
{
    /// <summary>Gets the hash of a safe retained native report, when produced.</summary>
    public string? ReportHash { get; init; }

    /// <summary>Gets native counts when the existing report loader could validate them.</summary>
    public RunEvidence.ModuleRunTestCounts? TestCounts { get; init; }
}
