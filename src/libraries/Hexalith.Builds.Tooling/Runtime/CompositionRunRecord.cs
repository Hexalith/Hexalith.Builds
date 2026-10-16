// <copyright file="CompositionRunRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The startup record stored on run state for every completed start.
/// </summary>
/// <param name="DurationSeconds">The elapsed seconds since the start request.</param>
/// <param name="ReadinessResults">The per-resource readiness results.</param>
/// <param name="Deadline">The effective deadline.</param>
/// <param name="DiagnosticsPath">The diagnostics directory preserved outside the workspace.</param>
/// <param name="ProjectProcessIds">Project process ids reported for debugger attach.</param>
public sealed record CompositionRunRecord(
    double DurationSeconds,
    IReadOnlyList<CompositionResourceReadiness> ReadinessResults,
    CompositionEffectiveDeadline Deadline,
    string? DiagnosticsPath,
    IReadOnlyList<int> ProjectProcessIds);
