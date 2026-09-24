// <copyright file="ExecutableDescriptorLoadResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Contains executable descriptor bindings or metadata-only diagnostics.
/// </summary>
/// <param name="Modules">The validated module bindings.</param>
/// <param name="Ui">The optional validated FrontComposer binding.</param>
/// <param name="Diagnostics">The ordered validation failures.</param>
public sealed record ExecutableDescriptorLoadResult(
    IReadOnlyList<ExecutableModuleDescriptor> Modules,
    ExecutableUiDescriptor? Ui,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets a value indicating whether every descriptor was accepted.
    /// </summary>
    public bool IsValid => Diagnostics.Count == 0;
}
