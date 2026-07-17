// <copyright file="ToolOutcome.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents the first causal outcome of a tool invocation.
/// </summary>
/// <param name="ExitCode">The stable process exit code.</param>
/// <param name="Phase">The causal phase.</param>
/// <param name="Category">The causal failure category.</param>
/// <param name="RuleId">The causal rule identifier.</param>
public sealed record ToolOutcome(
    ToolExitCode ExitCode,
    ToolPhase Phase,
    ToolFailureCategory Category,
    string? RuleId)
{
    /// <summary>
    /// Creates a passing outcome.
    /// </summary>
    /// <returns>A passing outcome.</returns>
    public static ToolOutcome Passed() => new(ToolExitCode.Success, ToolPhase.None, ToolFailureCategory.None, null);

    /// <summary>
    /// Records a failure without overwriting an earlier causal failure.
    /// </summary>
    /// <param name="phase">The failure phase.</param>
    /// <param name="category">The failure category.</param>
    /// <param name="ruleId">The stable rule identifier.</param>
    /// <param name="exitCode">The exit code.</param>
    /// <returns>The first causal outcome.</returns>
    public ToolOutcome Fail(
        ToolPhase phase,
        ToolFailureCategory category,
        string ruleId,
        ToolExitCode exitCode) =>
        ExitCode == ToolExitCode.Success
            ? new ToolOutcome(exitCode, phase, category, ruleId)
            : this;
}