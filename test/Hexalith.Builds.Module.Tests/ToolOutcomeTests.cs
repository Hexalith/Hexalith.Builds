// <copyright file="ToolOutcomeTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies stable process outcomes.
/// </summary>
public sealed class ToolOutcomeTests
{
    /// <summary>
    /// Verifies the authorized exit-code map.
    /// </summary>
    [Fact]
    public void ExitCodesMatchAuthorizedContract()
    {
        ((int)ToolExitCode.Success).ShouldBe(0);
        ((int)ToolExitCode.UsageOrManifest).ShouldBe(1);
        ((int)ToolExitCode.PrerequisiteUnavailable).ShouldBe(2);
        ((int)ToolExitCode.TopologyOrLifecycle).ShouldBe(3);
        ((int)ToolExitCode.ProductOrTest).ShouldBe(4);
        ((int)ToolExitCode.PersistedState).ShouldBe(5);
        ((int)ToolExitCode.EvidenceSchemaOrPolicy).ShouldBe(6);
        ((int)ToolExitCode.Cancelled).ShouldBe(130);
    }

    /// <summary>
    /// Verifies later numeric precedence cannot overwrite the first causal failure.
    /// </summary>
    [Fact]
    public void FailRetainsFirstCausalOutcome()
    {
        ToolOutcome outcome = ToolOutcome.Passed()
            .Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, "HXM001", ToolExitCode.UsageOrManifest)
            .Fail(ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "HXE900", ToolExitCode.EvidenceSchemaOrPolicy);

        outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
        outcome.Phase.ShouldBe(ToolPhase.Manifest);
        outcome.RuleId.ShouldBe("HXM001");
    }
}