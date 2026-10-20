// <copyright file="PlatformAdoptionValidationResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>The complete bounded static enrollment report.</summary>
/// <param name="Rows">Every requirement result.</param>
/// <param name="Diagnostics">Declaration and adoption failures.</param>
public sealed record PlatformAdoptionValidationResult(IReadOnlyList<PlatformAdoptionRow> Rows, IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>Gets a value indicating whether all static requirements passed; SM-2 remains separately unverified.</summary>
    public bool IsValid => Rows.All(row => row.Passed);
}
