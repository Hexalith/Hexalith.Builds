// <copyright file="PlatformManifestValidationResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents atomic enrollment validation: any diagnostic suppresses the entire declaration set.
/// </summary>
/// <param name="Declarations">All validated modules, or null when any file is invalid.</param>
/// <param name="Diagnostics">Deterministically ordered file, field and reason diagnostics.</param>
public sealed record PlatformManifestValidationResult(
    IReadOnlyList<PlatformModuleDeclaration>? Declarations,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>Gets a value indicating whether the entire input set is usable.</summary>
    public bool IsValid => Declarations is not null && Diagnostics.Count == 0;
}