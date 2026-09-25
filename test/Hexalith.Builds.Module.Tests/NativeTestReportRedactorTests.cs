// <copyright file="NativeTestReportRedactorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies retained native reports carry no host, user, absolute-path, or captured-output values.
/// </summary>
public sealed class NativeTestReportRedactorTests
{
    private const string _report =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>"
        + "<TestRun id=\"8bda5350-0e0e-4aac-a968-20135146d1d0\" name=\"builder@HOST-1 2026-09-24\" runUser=\"builder\" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
        + "<TestSettings name=\"default\" id=\"1\"><Deployment runDeploymentRoot=\"_HOST-1_2026-09-24\" /></TestSettings>"
        + "<Results><UnitTestResult testName=\"Native.RunHandoffTests.Completes\" computerName=\"HOST-1\" outcome=\"Passed\">"
        + "<Output><StdOut>connected to http://127.0.0.1:20012/</StdOut></Output></UnitTestResult></Results>"
        + "<TestDefinitions><UnitTest name=\"Native.RunHandoffTests.Completes\" storage=\"/home/builder/repo/bin/Native.dll\">"
        + "<TestMethod codeBase=\"C:\\Users\\builder\\repo\\bin\\Native.dll\" className=\"Native.RunHandoffTests\" name=\"Completes\" /></UnitTest></TestDefinitions>"
        + "<ResultSummary outcome=\"Completed\"><Counters total=\"1\" executed=\"1\" passed=\"1\" failed=\"0\" />"
        + "<RunInfos><RunInfo computerName=\"HOST-1\" outcome=\"Warning\"><Text>banner</Text></RunInfo></RunInfos>"
        + "<Output><StdOut>[xUnit.net] adapter banner</StdOut></Output></ResultSummary></TestRun>";

    /// <summary>Checks host, user, path, and output values are removed while outcomes and counts remain.</summary>
    /// <returns>A task that completes after the loaded redacted report is checked.</returns>
    [Fact]
    public async Task RedactionKeepsOutcomesAndRemovesMachineIdentityAsync()
    {
        byte[] redacted = NativeTestReportRedactor.Redact(Encoding.UTF8.GetBytes(_report)).ShouldNotBeNull();
        string text = Encoding.UTF8.GetString(redacted);

        foreach (string removed in new[] { "HOST-1", "builder", "runUser", "computerName", "runDeploymentRoot", "StdOut", "RunInfos", "/home/", "C:\\" })
        {
            text.ShouldNotContain(removed);
        }

        text.ShouldContain("name=\"native\"");
        text.ShouldContain("storage=\"Native.dll\"");
        text.ShouldContain("codeBase=\"Native.dll\"");
        text.ShouldContain("testName=\"Native.RunHandoffTests.Completes\"");
        text.ShouldNotContain("\r");

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string path = Path.Combine(root, "native.trx");
            await File.WriteAllBytesAsync(path, redacted, TestContext.Current.CancellationToken).ConfigureAwait(true);
            NativeTestReport report = (await NativeTestReportLoader.LoadAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).Report.ShouldNotBeNull();
            report.Total.ShouldBe(1);
            report.Passed.ShouldBe(1);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks a report that is not well-formed XML is left to the loader to reject.</summary>
    [Fact]
    public void MalformedReportIsNotRedacted() =>
        NativeTestReportRedactor.Redact(Encoding.UTF8.GetBytes("<TestRun>")).ShouldBeNull();
}
