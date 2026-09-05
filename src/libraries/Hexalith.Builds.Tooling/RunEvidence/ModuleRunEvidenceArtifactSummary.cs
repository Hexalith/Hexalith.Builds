// <copyright file="ModuleRunEvidenceArtifactSummary.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Represents the safe completion facts extracted from a schema-valid run-evidence artifact.
/// </summary>
/// <param name="FinalStatus">The reported completion status.</param>
/// <param name="ExitCode">The reported stable process exit code.</param>
/// <param name="Command">The normalized public command surface the artifact recorded.</param>
/// <param name="ManifestPath">The repository-relative manifest path the artifact recorded.</param>
/// <param name="ManifestHash">The manifest byte hash the artifact recorded.</param>
/// <param name="Profile">The optional qualification profile the artifact recorded.</param>
/// <param name="FixturePath">The repository-relative fixture path the artifact recorded.</param>
/// <param name="FixtureHash">The fixture byte hash the artifact recorded.</param>
/// <param name="FilterHash">The exact UTF-8 filter hash the artifact recorded.</param>
/// <param name="TestsReported">Whether a supported test runner supplied a report.</param>
/// <param name="TestsPassed">The passed test count.</param>
/// <param name="TestsFailed">The failed test count.</param>
internal sealed record ModuleRunEvidenceArtifactSummary(
    string FinalStatus,
    int ExitCode,
    string Command,
    string? ManifestPath,
    string? ManifestHash,
    string? Profile,
    string? FixturePath,
    string? FixtureHash,
    string? FilterHash,
    bool TestsReported,
    int TestsPassed,
    int TestsFailed);