// <copyright file="ToolCommandResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents an execution result safe to render to command output.
/// </summary>
/// <param name="Status">The non-passing or completed invocation status.</param>
/// <param name="Outcome">The first causal invocation outcome.</param>
/// <param name="Diagnostics">The metadata-only diagnostics.</param>
public sealed record ToolCommandResult(
    string Status,
    ToolOutcome Outcome,
    IReadOnlyList<ToolDiagnostic> Diagnostics);