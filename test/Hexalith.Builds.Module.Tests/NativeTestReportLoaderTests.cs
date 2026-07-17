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
    public async Task InvalidOrNonPassingTrxReturnsStableRuleAsync(string counters, string ruleId)
    {
        NativeTestReportLoadResult result = await LoadAsync($"<TestRun><ResultSummary>{counters}</ResultSummary></TestRun>")
            .ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe(ruleId);
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