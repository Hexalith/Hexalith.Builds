// <copyright file="ModuleRunPhaseOutcome.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Captures one phase result without replacing the command's first causal outcome.
/// </summary>
/// <param name="Phase">The completed or failed phase.</param>
/// <param name="Category">The stable failure category.</param>
/// <param name="RuleId">The optional stable rule identifier.</param>
public sealed record ModuleRunPhaseOutcome(ToolPhase Phase, ToolFailureCategory Category, string? RuleId);