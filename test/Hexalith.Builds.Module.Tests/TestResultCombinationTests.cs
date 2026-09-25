// <copyright file="TestResultCombinationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies how a persisted profile, its native test step, and teardown combine into the public test result.
/// </summary>
public sealed class TestResultCombinationTests
{
    private static readonly ToolCommandResult _profilePassed = new(
        "completed",
        ToolOutcome.Passed(),
        [new ToolDiagnostic("HXI004", ToolPhase.Test, ToolFailureCategory.None, "profile passed", "profile")]);

    private static readonly ToolCommandResult _cleanupPassed = new("completed", ToolOutcome.Passed(), []);

    private static readonly ToolCommandResult _cleanupFailed = Failure("HXL010", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, ToolExitCode.TopologyOrLifecycle);

    /// <summary>Checks a profile without native tests keeps its own passing result.</summary>
    [Fact]
    public void ProfileWithoutNativeTestsKeepsItsResult() =>
        ModuleCommandExecutionService.CombineTestResults(_profilePassed, null, _cleanupPassed).ShouldBeSameAs(_profilePassed);

    /// <summary>Checks a passing native step completes the invocation with both diagnostics.</summary>
    [Fact]
    public void PassingNativeTestsComplete()
    {
        NativeTestExecutionResult native = new(
            new ToolCommandResult("completed", ToolOutcome.Passed(), [new ToolDiagnostic("HXI005", ToolPhase.Test, ToolFailureCategory.None, "native passed", "nativeTests")]),
            new NativeTestReport("native.trx", new string('A', 64), 2, 2, 0, 0),
            [1]);

        ToolCommandResult result = ModuleCommandExecutionService.CombineTestResults(_profilePassed, native, _cleanupPassed);

        result.Status.ShouldBe("completed");
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXI004", "HXI005"]);
    }

    /// <summary>Checks a failing or secret-bearing native step fails the invocation with its own rule.</summary>
    /// <param name="ruleId">The native rule.</param>
    /// <param name="exitCode">The native exit code.</param>
    [Theory]
    [InlineData("HXT007", ToolExitCode.ProductOrTest)]
    [InlineData("HXT005", ToolExitCode.ProductOrTest)]
    [InlineData("HXT008", ToolExitCode.EvidenceSchemaOrPolicy)]
    public void FailingNativeTestsFailTheInvocation(string ruleId, ToolExitCode exitCode)
    {
        NativeTestExecutionResult native = new(Failure(ruleId, ToolPhase.Test, ToolFailureCategory.ProductOrTest, exitCode), null, null);

        ToolCommandResult result = ModuleCommandExecutionService.CombineTestResults(_profilePassed, native, _cleanupPassed);

        result.Status.ShouldBe("failed");
        result.Outcome.ExitCode.ShouldBe(exitCode);
        result.Outcome.RuleId.ShouldBe(ruleId);
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXI004", ruleId]);
    }

    /// <summary>Checks a teardown failure after passing native tests is the first causal failure.</summary>
    [Fact]
    public void CleanupFailureAfterPassingNativeTestsFails()
    {
        NativeTestExecutionResult native = new(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new NativeTestReport("native.trx", new string('A', 64), 2, 2, 0, 0), [1]);

        ToolCommandResult result = ModuleCommandExecutionService.CombineTestResults(_profilePassed, native, _cleanupFailed);

        result.Status.ShouldBe("failed");
        result.Outcome.RuleId.ShouldBe("HXL010");
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXI004", "HXL010"]);
    }

    /// <summary>Checks a native failure stays causal when teardown also fails.</summary>
    [Fact]
    public void NativeFailureStaysCausalWhenCleanupAlsoFails()
    {
        NativeTestExecutionResult native = new(Failure("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest), null, null);

        ToolCommandResult result = ModuleCommandExecutionService.CombineTestResults(_profilePassed, native, _cleanupFailed);

        result.Outcome.RuleId.ShouldBe("HXT007");
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXI004", "HXT007", "HXL010"]);
    }

    private static ToolCommandResult Failure(string ruleId, ToolPhase phase, ToolFailureCategory category, ToolExitCode exitCode) =>
        new("failed", ToolOutcome.Passed().Fail(phase, category, ruleId, exitCode), [new ToolDiagnostic(ruleId, phase, category, "failed", "step")]);
}
