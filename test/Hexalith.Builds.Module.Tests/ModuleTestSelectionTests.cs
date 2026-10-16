// <copyright file="ModuleTestSelectionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Exercises declared selection, actual Python children, timing and evidence without services.</summary>
public sealed class ModuleTestSelectionTests
{
    /// <summary>Rejects unsupported selections before starting any resource.</summary>
    /// <param name="scenario">The invalid input.</param>
    /// <returns>The command assertion.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("executor")]
    [InlineData("lifetime")]
    [InlineData("stop")]
    [InlineData("mode")]
    [InlineData("dependency")]
    [InlineData("secret")]
    [InlineData("filter")]
    [InlineData("attachment")]
    public async Task InvalidSelectionNeverStartsAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        switch (scenario)
        {
            case "duplicate": workspace.Document["modules"].ShouldNotBeNull()[0].ShouldNotBeNull()["tests"].ShouldNotBeNull().AsArray().Add(workspace.Document["modules"].ShouldNotBeNull()[0].ShouldNotBeNull()["tests"].ShouldNotBeNull()[0].ShouldNotBeNull().DeepClone()); break;
            case "executor": workspace.Change("modules.0.tests.0.executor", "\"shell\""); break;
            case "lifetime": workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "14401"); break;
            case "stop": workspace.Change("modules.0.tests.0.stopTimeoutSeconds", "901"); break;
            case "mode": workspace.Change("modules.0.tests.0.mode", "\"package\""); break;
            case "dependency": workspace.Change("modules.0.tests.0.entry", "\"references/dep/runner.py\""); break;
            case "secret": workspace.Change("modules.0.tests.0.password", "\"do-not-retain\""); break;
            default: scenario.ShouldBeOneOf("missing", "unknown", "filter", "attachment"); break;
        }

        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                throw new InvalidOperationException("No invalid selection may start.");
            },
        };
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), scenario switch { "missing" => null, "unknown" => "absent", _ => "verify" }, scenario == "filter" ? "anything" : null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", runId: scenario == "attachment" ? CompositionTestFiles.RunId : null, compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        starts.ShouldBe(0);
    }

    /// <summary>Runs the fixed Python executor and checks the complete command/evidence lifecycle.</summary>
    /// <param name="scenario">The child or cleanup behavior.</param>
    /// <param name="expectedExit">The expected command exit.</param>
    /// <param name="expectedCleanup">The expected owned cleanup count.</param>
    /// <param name="disposition">The truthful final resource disposition.</param>
    /// <returns>The process and evidence assertions.</returns>
    [Theory]
    [InlineData("pass", 0, 1, "removed")]
    [InlineData("observed-identities", 0, 1, "removed")]
    [InlineData("nonzero", 4, 0, "retained")]
    [InlineData("missing", 4, 0, "retained")]
    [InlineData("failed", 4, 0, "retained")]
    [InlineData("skipped", 4, 0, "retained")]
    [InlineData("secret", 6, 0, "retained")]
    [InlineData("app-secret", 6, 0, "retained")]
    [InlineData("dapr-secret", 6, 0, "retained")]
    [InlineData("entry-drift", 4, 0, "retained")]
    [InlineData("manifest-drift", 4, 0, "retained")]
    [InlineData("ci-failure", 4, 1, "removed")]
    [InlineData("cleanup-throws", 3, 1, "unknown")]
    [InlineData("cleanup-unverified", 3, 1, "unknown")]
    [InlineData("cancel", 130, 1, "removed")]
    [InlineData("timeout", 4, 0, "retained")]
    public async Task PythonCommandBindsRealReportAndCleanupAsync(string scenario, int expectedExit, int expectedCleanup, string disposition)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, scenario);
        string manifest = workspace.Save();
        using Process appHost = new();
        using CancellationTokenSource owner = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int cleanups = 0;
        CompositionRunPlan? selectedPlan = null;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            ContinuousIntegrationOverride = scenario == "ci-failure",
            SelectedPlatformStartOverride = async (_, selected, _) =>
            {
                selectedPlan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected, SourceMapping = Mapping(workspace.Root), Modules = [], UiMarkers = [] };
                if (scenario == "entry-drift")
                {
                    await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "\n# changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
                }

                if (scenario == "manifest-drift")
                {
                    await File.AppendAllTextAsync(manifest, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
                }

                if (scenario == "cancel")
                {
                    await owner.CancelAsync().ConfigureAwait(true);
                }

                CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, selectedPlan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned-resource", "process", "Healthy")]);
                if (scenario is "app-secret" or "dapr-secret")
                {
                    string accessPath = Path.Combine(selectedPlan.Workspace, "platform-components", "retention", "access.json");
                    CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(accessPath)!);
                    await File.WriteAllTextAsync(accessPath, JsonSerializer.Serialize(Enumerable.Range(0, 3).ToDictionary(index => "service" + index, _ => new { APP_API_TOKEN = "fixture-app-credential", DAPR_API_TOKEN = "fixture-dapr-credential" })), TestContext.Current.CancellationToken).ConfigureAwait(true);
                    readiness = readiness with { TestObservation = new CompositionTestObservation { RunId = selectedPlan.RunId, ModuleId = "memories", ProfileHash = "offline", ProfileJson = "offline", AccessPath = accessPath } };
                }

                if (scenario == "observed-identities")
                {
                    readiness = readiness with { TestObservation = new CompositionTestObservation { RunId = selectedPlan.RunId, ModuleId = "sample", ProfileHash = "offline", ProfileJson = "offline", ObservedCandidateHash = new string('a', 64), ObservedBackendIdentity = "offline fixture backend" } };
                }

                CompositionWorkspace.CreatePrivateDirectory(selectedPlan.Workspace);
                await CompositionDocumentStore.WriteAsync(Path.Combine(selectedPlan.Workspace, "plan.json"), selectedPlan, TestContext.Current.CancellationToken).ConfigureAwait(true);
                await CompositionDocumentStore.WriteAsync(selectedPlan.ReadinessPath, readiness, TestContext.Current.CancellationToken).ConfigureAwait(true);
                await SourceMappingMaterializer.WriteAsync(selectedPlan.SourceMapping, selectedPlan.Workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), selectedPlan.RunId, new CompositionRunSession(selectedPlan, readiness, "private-signing-key-value", appHost));
            },
            PlatformDownOverride = (_, _) =>
            {
                cleanups++;
                if (scenario == "cleanup-throws")
                {
                    throw new IOException("scan unavailable");
                }

                CompositionTestFiles.Delete(selectedPlan!.Workspace);
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], [], Unverified: scenario == "cleanup-unverified")));
            },
        };
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, "evidence/result.json", ToolOutputFormat.Json, writer, owner.Token, "child.dll", compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe(expectedExit, writer.ToString());
        cleanups.ShouldBe(expectedCleanup);
        byte[] evidence = await File.ReadAllBytesAsync(Path.Combine(workspace.Root, "evidence/result.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleRunEvidenceArtifactValidator.TryValidate(evidence, out _).ShouldBeTrue(System.Text.Encoding.UTF8.GetString(evidence));
        using JsonDocument document = JsonDocument.Parse(evidence);
        JsonElement selectedRun = document.RootElement.GetProperty("selectedTestRun");
        selectedRun.GetProperty("resourceDisposition").GetString().ShouldBe(disposition);
        selectedRun.GetProperty("qualificationCredit").GetBoolean().ShouldBeFalse();
        document.RootElement.GetProperty("invocation").GetProperty("command").GetString().ShouldNotBeNull().ShouldContain("--profile verify");
        System.Text.Encoding.UTF8.GetString(evidence).ShouldNotContain("private-signing-key-value");
        if (scenario == "observed-identities")
        {
            selectedRun.GetProperty("observedCandidateHash").GetString().ShouldBe(new string('a', 64));
            selectedRun.GetProperty("observedBackendIdentity").GetString().ShouldBe("offline fixture backend");
        }

        if (scenario == "pass")
        {
            document.RootElement.GetProperty("testCounts").GetProperty("passed").GetInt32().ShouldBe(1);
            File.Exists(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "native.trx")).ShouldBeTrue();
            foreach (string snapshot in new[] { "selected-plan.json", "selected-readiness.json", "selected-source-mapping.json" })
            {
                File.Exists(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, snapshot)).ShouldBeTrue();
            }
        }

        if (scenario is "secret" or "app-secret" or "dapr-secret")
        {
            File.Exists(Path.Combine(selectedPlan!.Workspace, "native-tests/native.trx")).ShouldBeFalse();
            File.Exists(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "native.trx")).ShouldBeFalse();
        }
    }

    /// <summary>Checks the maximum explicit lifetime and finite separate stop bound without waiting hours.</summary>
    [Fact]
    public void LongLifetimeAndExpiredSelectionRemainBound()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        DateTimeOffset request = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        PlatformManifestValidationResult validated = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        validated.IsValid.ShouldBeTrue();
        ModuleTestSelection selected = ModuleTestSelection.Select(validated.Declarations!, "verify", manifest, request).ShouldNotBeNull();
        (selected.ExecutionDeadlineUtc - request).TotalSeconds.ShouldBe(14400);
        selected.StopTimeoutSeconds.ShouldBe(900);
        selected.CredentialExpiryUtc.ShouldBe(selected.ExecutionDeadlineUtc);
        selected.MatchesSource(manifest).ShouldBeTrue();
        NativeTestExecutor.CreateArguments("vstest", "/repo/Test.csproj", "/run/results").ShouldContain("test");
    }

    /// <summary>Refuses expired owned execution before even launching its Python entry.</summary>
    /// <returns>The expiry assertion.</returns>
    [Fact]
    public async Task ExpiredSelectionNeverInvokesChildAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        PlatformManifestValidationResult validated = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        ModuleTestSelection expired = ModuleTestSelection.Select(validated.Declarations!, "verify", manifest, DateTimeOffset.UtcNow.AddDays(-1)).ShouldNotBeNull();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = expired, SourceMapping = Mapping(workspace.Root) };
        CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]);
        using Process process = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-signing-key-value", process), new PersistedProfileNativeTests("runner.py", "python3"), manifest, TestContext.Current.CancellationToken, selection: expired).ConfigureAwait(true);
        result.Result.Outcome.RuleId.ShouldBe("HXT009");
        Directory.Exists(Path.Combine(plan.Workspace, "native-tests")).ShouldBeFalse();
    }

    /// <summary>A parsed declaration cannot be rebound to different file bytes at selection.</summary>
    [Fact]
    public void DriftBetweenValidationAndSelectionRefuses()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        PlatformManifestValidationResult parsed = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "1");
        _ = workspace.Save();
        ModuleTestSelection.Select(parsed.Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldBeNull();
    }

    /// <summary>Rejects a symlink even when its target stays within the module.</summary>
    [Fact]
    public void SymlinkEntriesAreNotOwnedTests()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        _ = File.CreateSymbolicLink(Path.Combine(workspace.Root, "linked.py"), Path.Combine(workspace.Root, "runner.py"));
        workspace.Change("modules.0.tests.0.entry", "\"linked.py\"");
        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).IsValid.ShouldBeFalse();
    }

    /// <summary>EOF preserves a retained environment while an explicit stop still withdraws it.</summary>
    /// <returns>The listener assertion.</returns>
    [Fact]
    public async Task RetainedInputEofSurvivesUntilExplicitStopAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RetainOnFailure = true };
        Hexalith.Builds.ModuleHosts.AppHost.StopSignal eof = Hexalith.Builds.ModuleHosts.AppHost.StopSignal.Listen(plan, new MemoryStream());
        await using (eof.ConfigureAwait(true))
        {
            await Task.Delay(30, TestContext.Current.CancellationToken).ConfigureAwait(true);
            eof.IsStopRequested.ShouldBeFalse();
        }

        Hexalith.Builds.ModuleHosts.AppHost.StopSignal stop = Hexalith.Builds.ModuleHosts.AppHost.StopSignal.Listen(plan, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(CompositionEnvironment.StopCommand + "\n")));
        await using (stop.ConfigureAwait(true))
        {
            await Task.Delay(30, TestContext.Current.CancellationToken).ConfigureAwait(true);
            stop.IsStopRequested.ShouldBeTrue();
        }
    }

    /// <summary>Explicit down after source drift cleans exact ownership and preserves the earlier failure.</summary>
    /// <returns>The durable outcome assertion.</returns>
    [Fact]
    public async Task ExplicitDownAfterManifestDriftPreservesFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selected, DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        const string runId = CompositionTestFiles.RunId;
        await engine.StateStore.WriteAsync(new CompositionRunState(CompositionRunState.SupportedSchema, runId, CompositionRunStatus.Ready, selected.ManifestHash, Path.Combine(options.WorkspaceRoot, runId), null, null, null, DateTimeOffset.UtcNow) { SelectedTest = selected }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult failure = new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
        _ = await engine.RecordTestOutcomeAsync(runId, [], failure, "retained", new CompositionRunResources([], [], Unverified: true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(manifest, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter writer = new();
        await using ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Down, manifest, null, null, null, ToolOutputFormat.Json, writer, TestContext.Current.CancellationToken, "child.dll", runId, options).ConfigureAwait(true);
        exit.ShouldBe(0, writer.ToString());
        ModuleTestRunRecord record = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(options.StateDirectory, "diagnostics", runId, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        record.TerminalStatus.ShouldBe("failed");
        record.TerminalOutcome.RuleId.ShouldBe("HXT007");
        record.ResourceDisposition.ShouldBe("removed");
        (await engine.StateStore.TryReadAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
    }

    /// <summary>A partial startup failure retains a live local process double until exact owned down.</summary>
    /// <returns>The retention and cleanup assertions.</returns>
    [Fact]
    public async Task PartialStartupFailureRetainsOwnedProcessAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        workspace.Change("modules.0.tests.0.stopTimeoutSeconds", "3");
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selected, RetainOnFailure = true, StopTimeout = TimeSpan.FromSeconds(1), DockerCommand = CompositionTestFiles.CreateStubDocker(workspace.Root) };
        CompositionEngine engine = new(options);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
        using Process process = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-c", "import time; time.sleep(60)"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId }) };
        process.Start().ShouldBeTrue();
        try
        {
            System.Reflection.MethodInfo method = typeof(CompositionEngine).GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                .Single(method => method.Name == "FailAndTearDownAsync" && method.GetParameters().Length == 8);
            ToolDiagnostic diagnostic = new("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "partial startup failure", "readiness");
            Task<CompositionStartResult> task = (Task<CompositionStartResult>)method.Invoke(engine, [plan.RunId, process, selected.ManifestHash, CompositionRunStatus.Failed, diagnostic, ToolExitCode.TopologyOrLifecycle, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null)])!;
            CompositionStartResult failed = await task.ConfigureAwait(true);
            failed.Result.Outcome.RuleId.ShouldBe("HXR021");
            process.HasExited.ShouldBeFalse();
            CompositionRunState state = (await engine.StateStore.TryReadAsync(plan.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            state.AppHostProcessId.ShouldBe(process.Id);
            state.TestRun.ShouldNotBeNull().ResourceDisposition.ShouldBe("retained");
            CompositionDownResult down = await engine.DownAsync(plan.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            process.Refresh();
            process.HasExited.ShouldBeTrue();
            ModuleTestRunRecord record = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(options.StateDirectory, "diagnostics", plan.RunId, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            record.TerminalOutcome.RuleId.ShouldBe("HXR021");
            record.ResourceDisposition.ShouldBe("removed");
        }
        finally
        {
            CompositionProcess.Kill(process);
        }
    }

    private static SourceMapping Mapping(string root) => new(WorkspaceMode.Source, root, "sample", [new SourceMappingEntry("sample", "source", root)], false);

    private static CompositionEngineOptions Options(string root) => new("unused-apphost", "unused-child")
    {
        SourceMapping = Mapping(root),
        WorkspaceRoot = Path.Combine(root, "workspace"),
        StateDirectory = Path.Combine(root, "state"),
        ContinuousIntegrationOverride = false,
    };

    private static void Declare(PlatformManifestTestWorkspace workspace, string scenario = "pass")
    {
        workspace.Document["modules"].ShouldNotBeNull()[0].ShouldNotBeNull()["tests"] = JsonNode.Parse("""
            [{"name":"verify","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":14400,"stopTimeoutSeconds":900,"mode":"source"}]
            """);
        if (scenario == "timeout")
        {
            workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "2");
        }

        string report = scenario switch
        {
            "failed" => "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"0\" failed=\"1\" /></ResultSummary></TestRun>",
            "skipped" => "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"2\" passed=\"1\" failed=\"0\" notExecuted=\"1\" /></ResultSummary></TestRun>",
            _ => "<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>",
        };
        string secretSuffix = scenario switch
        {
            "secret" => " + os.environ['HEXALITH_G4_ACCESS_TOKEN']",
            "app-secret" => " + 'fixture-app-credential'",
            "dapr-secret" => " + 'fixture-dapr-credential'",
            _ => string.Empty,
        };
        string script = "import os, pathlib, time\n"
            + "import helper, base64, json, datetime\n"
            + "assert helper.value == 'owned-helper'\n"
            + "assert all(pathlib.Path(os.environ[key]).is_file() for key in ['HEXALITH_G4_PLAN_PATH', 'HEXALITH_G4_READINESS_PATH', 'HEXALITH_G4_SOURCE_MAPPING_PATH'])\n"
            + "claims = json.loads(base64.urlsafe_b64decode(os.environ['HEXALITH_G4_ACCESS_TOKEN'].split('.')[1] + '=='))\n"
            + "assert claims['exp'] == int(datetime.datetime.fromisoformat(os.environ['HEXALITH_G4_CREDENTIAL_EXPIRY_UTC']).timestamp())\n"
            + "assert claims['domains'] == 'sample'\n"
            + "assert os.environ['HEXALITH_G4_TEST_OWNER'] == 'sample'\n"
            + "assert os.environ['HEXALITH_G4_DOMAINS'] == 'sample'\n"
            + "assert 0 <= (datetime.datetime.fromisoformat(os.environ['HEXALITH_G4_CREDENTIAL_EXPIRY_UTC']) - datetime.datetime.fromisoformat(os.environ['HEXALITH_G4_EXECUTION_DEADLINE_UTC'])).total_seconds() < 1\n"
            + (scenario == "timeout" ? "time.sleep(30)\n" : string.Empty)
            + (scenario == "missing" ? string.Empty : "pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text(" + JsonSerializer.Serialize(report) + secretSuffix + ")\n")
            + "raise SystemExit(" + (scenario is "nonzero" or "ci-failure" ? "7" : "0") + ")\n";
        File.WriteAllText(Path.Combine(workspace.Root, "runner.py"), script);
        File.WriteAllText(Path.Combine(workspace.Root, "helper.py"), "value = 'owned-helper'\n");
    }
}