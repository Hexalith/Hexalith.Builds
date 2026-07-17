// <copyright file="NativeTestReportLoadResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestReports;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents a fail-closed native test-report loading result.
/// </summary>
/// <param name="Report">The normalized report when all test-report gates passed.</param>
/// <param name="Diagnostic">The stable diagnostic when the report cannot represent a pass.</param>
public sealed record NativeTestReportLoadResult(NativeTestReport? Report, ToolDiagnostic? Diagnostic)
{
    /// <summary>
    /// Gets a value indicating whether the report may contribute to a passing test outcome.
    /// </summary>
    public bool IsValid => Report is not null && Diagnostic is null;
}