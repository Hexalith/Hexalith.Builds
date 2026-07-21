// <copyright file="ModuleRunEvidenceWriteResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents the outcome of writing a module-run evidence artifact.
/// </summary>
/// <param name="Succeeded">A value indicating whether the evidence artifact was written atomically.</param>
/// <param name="Diagnostic">The metadata-only failure diagnostic when writing failed.</param>
public sealed record ModuleRunEvidenceWriteResult(bool Succeeded, ToolDiagnostic? Diagnostic)
{
    /// <summary>
    /// Creates a successful write result.
    /// </summary>
    /// <returns>A successful result.</returns>
    public static ModuleRunEvidenceWriteResult Passed() => new(true, null);

    /// <summary>
    /// Creates a fail-closed evidence write result.
    /// </summary>
    /// <returns>A failed result.</returns>
    public static ModuleRunEvidenceWriteResult Failed() => new(
        false,
        new ToolDiagnostic(
            "HXE160",
            ToolPhase.Evidence,
            ToolFailureCategory.EvidencePolicy,
            "The module-run evidence artifact could not be written.",
            "evidence",
            "Use a writable canonical repository-relative JSON evidence path."));
}