// <copyright file="CompositionStartupReport.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The structured startup record preserved outside the run workspace.
/// </summary>
/// <param name="Schema">The report schema identity.</param>
/// <param name="RunId">The run identity, when one was allocated.</param>
/// <param name="RuleId">The stable rule identity.</param>
/// <param name="DurationSeconds">The elapsed time since the start request.</param>
/// <param name="EffectiveDeadlineSeconds">The effective deadline in seconds.</param>
/// <param name="EffectiveDeadlineIsOverride">True when a module declared the deadline.</param>
/// <param name="EffectiveDeadlineJustification">The override justification, when declared.</param>
/// <param name="EffectiveDeadlineModuleId">The module that declared the override.</param>
/// <param name="FailingTask">The startup task that failed, when one did.</param>
/// <param name="Resources">The per-resource readiness results.</param>
public sealed record CompositionStartupReport(
    string Schema,
    string? RunId,
    string RuleId,
    double DurationSeconds,
    double EffectiveDeadlineSeconds,
    bool EffectiveDeadlineIsOverride,
    string? EffectiveDeadlineJustification,
    string? EffectiveDeadlineModuleId,
    string? FailingTask,
    IReadOnlyList<CompositionResourceReadiness> Resources)
{
    /// <summary>The supported startup report schema identity.</summary>
    public const string SupportedSchema = "hexalith.g4-startup-report.v1";
}
