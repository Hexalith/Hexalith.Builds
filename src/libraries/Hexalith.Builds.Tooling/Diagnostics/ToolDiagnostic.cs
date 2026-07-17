// <copyright file="ToolDiagnostic.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents a stable, metadata-only diagnostic.
/// </summary>
/// <param name="RuleId">The stable rule identifier.</param>
/// <param name="Phase">The phase that produced the diagnostic.</param>
/// <param name="Category">The failure category.</param>
/// <param name="Message">A metadata-only human-readable message.</param>
/// <param name="Field">The optional field identity.</param>
/// <param name="Hint">The optional remediation hint.</param>
/// <param name="Source">The optional repository-relative source path.</param>
/// <param name="Location">The optional source location.</param>
/// <param name="Row">The optional stable row identity.</param>
public sealed record ToolDiagnostic(
    string RuleId,
    ToolPhase Phase,
    ToolFailureCategory Category,
    string Message,
    string? Field = null,
    string? Hint = null,
    string? Source = null,
    string? Location = null,
    string? Row = null);