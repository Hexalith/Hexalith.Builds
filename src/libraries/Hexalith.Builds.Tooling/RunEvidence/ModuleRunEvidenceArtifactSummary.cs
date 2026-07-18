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
internal sealed record ModuleRunEvidenceArtifactSummary(string FinalStatus, int ExitCode);