// <copyright file="NativeTestReportLoaderTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.TestReports;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies native test-report handling for both supported .NET test platforms.
/// </summary>
public sealed class NativeTestReportLoaderTests
{
    /// <summary>
    /// Verifies VSTest-compatible TRX counters are retained with a content hash.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task VstestTrxWithPassingCountersLoadsAsync()
    {
        NativeTestReportLoadResult result = await LoadAsync(
            "<TestRun><ResultSummary><Counters total=\"2\" passed=\"2\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeTrue();
        NativeTestReport report = result.Report.ShouldNotBeNull();
        report.Total.ShouldBe(2);
        report.Passed.ShouldBe(2);
        report.Sha256.Length.ShouldBe(64);
    }

    /// <summary>
    /// Verifies Microsoft Testing Platform xUnit TRX counters use the same native report contract.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MicrosoftTestingPlatformTrxWithPassingCountersLoadsAsync()
    {
        NativeTestReportLoadResult result = await LoadAsync(
            "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" error=\"0\" timeout=\"0\" aborted=\"0\" notExecuted=\"0\" inconclusive=\"0\" /></ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeTrue();
        result.Report.ShouldNotBeNull().FileName.ShouldBe("results.trx");
    }

    /// <summary>
    /// Verifies non-passing report conditions never become a passing result.
    /// </summary>
    /// <param name="counters">The TRX counters element.</param>
    /// <param name="ruleId">The expected stable rule identifier.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("<Counters total=\"0\" passed=\"0\" failed=\"0\" notExecuted=\"0\" />", "HXT003")]
    [InlineData("<Counters total=\"2\" passed=\"0\" failed=\"0\" notExecuted=\"2\" />", "HXT004")]
    [InlineData("<Counters total=\"2\" passed=\"1\" failed=\"1\" notExecuted=\"0\" />", "HXT005")]
    [InlineData("<Counters total=\"1\" passed=\"2\" failed=\"0\" notExecuted=\"0\" />", "HXT002")]
    [InlineData("<Counters total=\"2\" passed=\"1\" failed=\"0\" notExecuted=\"0\" />", "HXT002")]
    [InlineData("<Counters passed=\"1\" failed=\"0\" notExecuted=\"0\" />", "HXT002")]
    public async Task InvalidOrNonPassingTrxReturnsStableRuleAsync(string counters, string ruleId)
    {
        NativeTestReportLoadResult result = await LoadAsync($"<TestRun><ResultSummary>{counters}</ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe(ruleId);
    }

    /// <summary>
    /// Verifies unrelated XML counters cannot masquerade as a native TRX report.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task UnstructuredCountersAreRejectedAsync()
    {
        NativeTestReportLoadResult result = await LoadAsync(
            "<metadata><Counters total=\"1\" passed=\"1\" failed=\"0\" /></metadata>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT002");
    }

    /// <summary>
    /// Verifies a missing report path fails closed rather than throwing or being ignored.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingReportFailsClosedAsync()
    {
        string missingPath = Path.Combine(Path.GetTempPath(), $"hexalith-builds-missing-{Guid.NewGuid():N}.trx");

        NativeTestReportLoadResult result = await NativeTestReportLoader.LoadAsync(
            missingPath,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT001");
    }

    /// <summary>
    /// Verifies malformed XML, and a duplicated or absent structural element, are all rejected
    /// as invalid rather than partially parsed.
    /// </summary>
    /// <param name="reportContent">The malformed or structurally invalid report content.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("<TestRun><ResultSummary><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary>")]
    [InlineData("<TestRun></TestRun>")]
    [InlineData(
        "<TestRun>"
        + "<ResultSummary><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary>"
        + "<ResultSummary><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary>"
        + "</TestRun>")]
    [InlineData("<TestRun><ResultSummary></ResultSummary></TestRun>")]
    public async Task StructurallyInvalidReportFailsClosedAsync(string reportContent)
    {
        NativeTestReportLoadResult result = await LoadAsync(reportContent).ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT002");
    }

    /// <summary>
    /// Verifies a declared run-level outcome other than "Completed" fails closed even when
    /// per-test counters look clean, since VSTest/MTP record host-level failures there.
    /// </summary>
    /// <param name="outcome">The declared <c>ResultSummary/@outcome</c> value.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("Aborted")]
    [InlineData("Failed")]
    [InlineData("Timeout")]
    [InlineData("InProgress")]
    public async Task NonCompletedRunOutcomeFailsClosedAsync(string outcome)
    {
        NativeTestReportLoadResult result = await LoadAsync(
            $"<TestRun><ResultSummary outcome=\"{outcome}\"><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT006");
    }

    /// <summary>
    /// Verifies a negative counter component cannot cancel out a real failure in the aggregate sum.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task NegativeCounterComponentFailsClosedAsync()
    {
        NativeTestReportLoadResult result = await LoadAsync(
            "<TestRun><ResultSummary><Counters total=\"2\" passed=\"2\" failed=\"1\" error=\"-1\" notExecuted=\"0\" /></ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT002");
    }

    private static async Task<NativeTestReportLoadResult> LoadAsync(string reportContent)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-trx-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(directory);
        string reportPath = Path.Combine(directory, "results.trx");

        try
        {
            await File.WriteAllTextAsync(reportPath, reportContent, TestContext.Current.CancellationToken).ConfigureAwait(true);
            return await NativeTestReportLoader.LoadAsync(reportPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}