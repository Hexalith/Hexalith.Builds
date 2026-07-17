// <copyright file="ManifestLoadResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents strict module-manifest loading results.
/// </summary>
/// <param name="Manifest">The bound manifest when syntax and schema validation succeeded.</param>
/// <param name="Diagnostics">The deterministically ordered validation diagnostics.</param>
public sealed record ManifestLoadResult(
    ModuleManifest? Manifest,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets a value indicating whether the manifest is valid.
    /// </summary>
    public bool IsValid => Manifest is not null && Diagnostics.Count == 0;
}