// <copyright file="NativeTestExecutorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the runner-owned native test step never hides the native exit status or report semantics.
/// </summary>
public sealed class NativeTestExecutorTests
{
    private static readonly NativeTestReportLoadResult _passingReport = new(new NativeTestReport("native.trx", new string('A', 64), 2, 2, 0, 0), null);

    private static readonly byte[] _reportBytes = Encoding.UTF8.GetBytes("<TestRun><ResultSummary outcome=\"Completed\" /></TestRun>");

    /// <summary>Checks each platform uses its own native report switch and no shared option.</summary>
    [Fact]
    public void ArgumentsSelectTheNativeReportOfEachPlatform()
    {
        NativeTestExecutor.CreateArguments("vstest", "/repo/Tests.csproj", "/run/native-tests")
            .ShouldBe(["test", "/repo/Tests.csproj", "--logger", "trx;LogFileName=native.trx", "--results-directory", "/run/native-tests"]);
        NativeTestExecutor.CreateArguments("mtp", "/repo/Tests.csproj", "/run/native-tests")
            .ShouldBe(["test", "--project", "/repo/Tests.csproj", "--results-directory", "/run/native-tests", "--report-xunit-trx", "--report-xunit-trx-filename", "native.trx"]);
        _ = Should.Throw<ArgumentOutOfRangeException>(() => NativeTestExecutor.CreateArguments("nunit", "/repo/Tests.csproj", "/run"));
    }

    /// <summary>Checks a passing native report and zero exit bind counts and bytes.</summary>
    [Fact]
    public void PassingReportAndExitBindCounts()
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), _passingReport, _reportBytes, ["run-token"]);

        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        result.Report.ShouldNotBeNull().Passed.ShouldBe(2);
        result.ReportBytes.ShouldBe(_reportBytes);
    }

    /// <summary>Checks a clean report cannot override a nonzero native exit status.</summary>
    [Fact]
    public void NonzeroExitFailsEvenWithPassingReport()
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 2, string.Empty, false), _passingReport, _reportBytes, ["run-token"]);

        AssertNotPassing(result, "HXT007", ToolExitCode.ProductOrTest);
    }

    /// <summary>Checks native report failures keep their stable loader rule.</summary>
    /// <param name="ruleId">The loader rule.</param>
    [Theory]
    [InlineData("HXT001")]
    [InlineData("HXT002")]
    [InlineData("HXT003")]
    [InlineData("HXT004")]
    [InlineData("HXT005")]
    [InlineData("HXT006")]
    public void InvalidReportKeepsLoaderRule(string ruleId)
    {
        NativeTestReportLoadResult invalid = new(null, new ToolDiagnostic(ruleId, ToolPhase.Test, ToolFailureCategory.ProductOrTest, "invalid", "report"));

        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), invalid, _reportBytes, ["run-token"]);

        AssertNotPassing(result, ruleId, ToolExitCode.ProductOrTest);
    }

    /// <summary>Checks a timed-out or unstarted native process never passes.</summary>
    [Fact]
    public void TimeoutAndUnavailableProcessNeverPass()
    {
        AssertNotPassing(
            NativeTestExecutor.Evaluate(new CompositionProcessResult(true, -1, string.Empty, true), _passingReport, _reportBytes, ["run-token"]),
            "HXT007",
            ToolExitCode.ProductOrTest);
        AssertNotPassing(
            NativeTestExecutor.Evaluate(new CompositionProcessResult(false, -1, string.Empty, false), _passingReport, _reportBytes, ["run-token"]),
            "HXR030",
            ToolExitCode.PrerequisiteUnavailable);
    }

    /// <summary>Checks a report carrying handoff or credential material is rejected and not retained.</summary>
    /// <param name="content">The seeded report content.</param>
    [Theory]
    [InlineData("<TestRun>run-token-value</TestRun>")]
    [InlineData("<TestRun><StdOut>Authorization: Bearer abcdefghijklmnop</StdOut></TestRun>")]
    [InlineData("<TestRun><StdOut>password=hunter22</StdOut></TestRun>")]
    public void SecretBearingReportIsRejectedAndDropped(string content)
    {
        NativeTestExecutionResult result = NativeTestExecutor.Evaluate(
            new CompositionProcessResult(true, 0, string.Empty, false),
            _passingReport,
            Encoding.UTF8.GetBytes(content),
            ["run-token-value"]);

        AssertNotPassing(result, "HXT008", ToolExitCode.EvidenceSchemaOrPolicy);
    }

    /// <summary>Checks ordinary native report metadata is not mistaken for credential material.</summary>
    [Fact]
    public void ReportMetadataWithoutCredentialsIsRetained()
    {
        byte[] report = Encoding.UTF8.GetBytes(
            "<TestRun id=\"1\" runUser=\"builder\" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
            + "<TestDefinitions><UnitTest name=\"AuthenticatedCommandCompletesAsync\" storage=\"/repo/artifacts/Native.dll\">"
            + "<TestMethod codeBase=\"/repo/artifacts/Native.dll\" className=\"Native.RunHandoffTests\" name=\"AuthenticatedCommandCompletesAsync\" />"
            + "</UnitTest></TestDefinitions><ResultSummary outcome=\"Completed\"><Counters total=\"2\" passed=\"2\" failed=\"0\" /></ResultSummary></TestRun>");

        NativeTestExecutor.Evaluate(new CompositionProcessResult(true, 0, string.Empty, false), _passingReport, report, ["run-token"])
            .Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    /// <summary>Checks the handoff exposes endpoints and identity without the signing key.</summary>
    [Fact]
    public void HandoffCarriesRunScopedEndpointsAndIdentity()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), new Uri("http://127.0.0.1:20013/"), []);

            IReadOnlyDictionary<string, string> handoff = NativeTestHandoff.Create(plan, readiness, "access-token");

            handoff[NativeTestHandoff.RunId].ShouldBe(plan.RunId);
            handoff[NativeTestHandoff.EventStoreUrl].ShouldBe("http://127.0.0.1:20012/");
            handoff.ContainsKey(NativeTestHandoff.PeerEventStoreUrl).ShouldBeFalse();
            NativeTestHandoff.Create(plan with { Ports = plan.Ports with { SecondEventStoreHttp = 20027 } }, readiness, "access-token")[NativeTestHandoff.PeerEventStoreUrl]
                .ShouldBe("http://127.0.0.1:20027/");
            handoff[NativeTestHandoff.UiUrl].ShouldBe("http://127.0.0.1:20013/");
            handoff[NativeTestHandoff.Tenant].ShouldBe(plan.TenantNamespace);
            handoff[NativeTestHandoff.ResourceNamespace].ShouldBe(plan.ResourceNamespace);
            handoff[NativeTestHandoff.Domains].ShouldBe("p0-inventory,p0-orders");
            handoff[NativeTestHandoff.AccessToken].ShouldBe("access-token");
            handoff.Keys.ShouldNotContain(CompositionEnvironment.SigningKey);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks the retained report sits beside the requested evidence artifact.</summary>
    [Fact]
    public void RetainedReportPathFollowsEvidencePath()
    {
        NativeTestExecutor.RetainedReportPath("evidence/g4/full.json", "vstest").ShouldBe("evidence/g4/full.vstest.trx");
        NativeTestExecutor.RetainedReportPath("full.json", "mtp").ShouldBe("full.mtp.trx");
    }

    /// <summary>Prepared scopes reject invalid identities before launch, while the valid control actually executes.</summary>
    /// <param name="mutation">The independently invalid owner scope.</param>
    /// <returns>The refusal and never-executed entry assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture mutation is unknown.</exception>
    [Theory]
    [InlineData("valid")]
    [InlineData("foreign-run")]
    [InlineData("foreign-module")]
    [InlineData("wildcard-domain")]
    [InlineData("wildcard-tenant")]
    [InlineData("absent-synthetic-tenant")]
    public async Task PreparedScopeGatesChildExecutionAsync(string mutation)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Document["modules"]![0]!["tests"] = JsonNode.Parse("""
            [{"name":"verify","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":60,"stopTimeoutSeconds":30,"mode":"source"}]
            """);
        string marker = Path.Combine(workspace.Root, "child-executed");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "import pathlib\npathlib.Path(__file__).with_name('child-executed').touch()\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        PlatformManifestValidationResult validated = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        validated.IsValid.ShouldBeTrue();
        ModuleTestSelection selection = ModuleTestSelection.Select(validated.Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        SourceMapping mapping = new(WorkspaceMode.Source, workspace.Root, "sample", [new SourceMappingEntry("sample", "source", workspace.Root)], false);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection, SourceMapping = mapping, Modules = [], UiMarkers = [] };
        plan = await TestEnvironmentProvisioningFixture.PrepareAsync(plan, manifest, workspace.Root).ConfigureAwait(true);
        CompositionNativeTestScope scope = new(plan.RunId, "sample", new string('a', 64), ["sample"], [plan.TenantNamespace]);
        scope = mutation switch
        {
            "valid" => scope,
            "foreign-run" => scope with { RunId = "foreign-run" },
            "foreign-module" => scope with { ModuleId = "foreign-module" },
            "wildcard-domain" => scope with { Domains = ["sample*"] },
            "wildcard-tenant" => scope with { Tenants = [plan.TenantNamespace, "*"] },
            "absent-synthetic-tenant" => scope with { Tenants = ["other-tenant"] },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        CompositionReadiness preliminary = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned-resource", "process", "Healthy")]);
        CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(preliminary, "sample") with { NativeTestScope = scope };
        using Process process = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);
        if (mutation == "valid")
        {
            File.Exists(marker).ShouldBeTrue();

            // The valid scope launches the marker-only child; its intentionally missing TRX remains a test failure.
            result.Result.Outcome.RuleId.ShouldBe("HXT002");
        }
        else
        {
            AssertNotPassing(result, "HXT009", ToolExitCode.ProductOrTest);
            File.Exists(marker).ShouldBeFalse();
        }
    }

    private static void AssertNotPassing(NativeTestExecutionResult result, string ruleId, ToolExitCode exitCode)
    {
        result.Result.Outcome.ExitCode.ShouldBe(exitCode);
        result.Result.Outcome.RuleId.ShouldBe(ruleId);
        result.Result.Diagnostics.ShouldHaveSingleItem().RuleId.ShouldBe(ruleId);
        result.Report.ShouldBeNull();
        result.ReportBytes.ShouldBeNull();
    }
}