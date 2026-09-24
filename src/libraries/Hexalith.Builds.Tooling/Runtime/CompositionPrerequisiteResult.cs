// <copyright file="CompositionPrerequisiteResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// The fail-closed prerequisite decision for runner-owned composition.
/// </summary>
/// <param name="DaprHome">The verified absolute Dapr home, or null when unavailable.</param>
/// <param name="Diagnostics">The stable diagnostics; empty when every prerequisite is verified.</param>
public sealed record CompositionPrerequisiteResult(string? DaprHome, IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>
    /// Gets a value indicating whether composition may start.
    /// </summary>
    public bool IsAvailable => Diagnostics.Count == 0 && DaprHome is not null;
}