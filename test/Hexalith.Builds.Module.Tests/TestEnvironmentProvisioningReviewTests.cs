// <copyright file="TestEnvironmentProvisioningReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

public sealed partial class TestEnvironmentProvisioningTests
{
    /// <summary>Batch diagnostics distinguish attempts from launches, including pre-launch middle-entry refusal.</summary>
    /// <param name="refusesBeforeLaunch">Whether the middle entry loses descriptor proof before launch.</param>
    /// <returns>The actual child and attempted-entry assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedBatchIdentifiesAttemptedAndNotAttemptedEntriesAsync(bool refusesBeforeLaunch)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        JsonArray tests = workspace.Document["modules"]![0]!["tests"]!.AsArray();
        JsonNode third = tests[1]!.DeepClone();
        third["name"] = "third";
        tests.Add(third);
        string mutation = refusesBeforeLaunch
            ? "\nif os.environ['HEXALITH_G4_TEST_NAME'] == 'first': (path.parent / 'source-mapping' / 'catalog-versions.json').unlink()\n"
            : "\nif os.environ['HEXALITH_G4_TEST_NAME'] == 'second': raise SystemExit(7)\n";
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), mutation, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using Process host = new();
        CompositionEngineOptions options = ReadyOptions(workspace.Root, manifest, host);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", manifest, "--profile", "first", "--profile", "second", "--profile", "third", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.ProductOrTest, output.ToString());
        output.ToString().ShouldContain("attempted: first, second; failed: second; not attempted: third");
        string[] children = await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "children.jsonl"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        children.Length.ShouldBe(refusesBeforeLaunch ? 1 : 2);
        using JsonDocument first = JsonDocument.Parse(children[0]);
        first.RootElement.GetProperty("name").GetString().ShouldBe("first");
        if (refusesBeforeLaunch)
        {
            output.ToString().ShouldContain("Verified catalog identity is unavailable or inconsistent.");
        }
    }

    /// <summary>Later entries cannot inherit earlier functional observation files.</summary>
    /// <returns>The actual selected child assertion.</returns>
    [Fact]
    public async Task NextEntryDoesNotInheritFunctionalObservationsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "\nassert not (pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).parent / 'functional-observations.json').exists()\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        using Process host = new();
        CompositionRunSession session = (await ReadyOptions(workspace.Root, manifest, host).SelectedPlatformStartOverride!(manifest, selected, TestContext.Current.CancellationToken).ConfigureAwait(true)).Session!;
        string results = Path.Combine(session.Plan.Workspace, "native-tests");
        CompositionWorkspace.CreatePrivateDirectory(results);
        await File.WriteAllTextAsync(Path.Combine(results, "functional-observations.json"), "prior-entry-observations", TestContext.Current.CancellationToken).ConfigureAwait(true);
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new(selected.Entry, selected.Executor), manifest, TestContext.Current.CancellationToken, selection: selected).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        File.Exists(Path.Combine(workspace.Root, "children.jsonl")).ShouldBeTrue();
    }

    /// <summary>The combined clock includes stopping allowances and activation cannot renew it.</summary>
    /// <returns>The cancellation assertions.</returns>
    [Fact]
    public async Task BatchBudgetActivatesOnceAndIncludesInterEntryAllowancesAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        IReadOnlyList<ModuleTestSelection> selections = ModuleTestSelection.SelectBatch(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, ["first", "second"], manifest, DateTimeOffset.UtcNow);
        ModuleTestSelection.CombinedBudgetSeconds(selections).ShouldBe(7);
        using ModuleTestExecutionBudget budget = new(TestContext.Current.CancellationToken);
        budget.Restrict(1);
        await Task.Delay(1100, TestContext.Current.CancellationToken).ConfigureAwait(true);
        budget.Token.IsCancellationRequested.ShouldBeFalse();
        budget.Activate(1);
        await Task.Delay(650, TestContext.Current.CancellationToken).ConfigureAwait(true);
        budget.Token.IsCancellationRequested.ShouldBeFalse();
        budget.Activate(10);
        budget.Restrict(10);
        await Task.Delay(550, TestContext.Current.CancellationToken).ConfigureAwait(true);
        budget.Token.IsCancellationRequested.ShouldBeTrue();
    }

    /// <summary>Resource mixing and overflow refuse during selection with distinct reasons.</summary>
    /// <param name="scenario">The incompatible selection.</param>
    [Theory]
    [InlineData("retention")]
    [InlineData("overflow")]
    public void BatchResourceMixAndOverflowRefuseDuringSelection(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        JsonArray tests = workspace.Document["modules"]![0]!["tests"]!.AsArray();
        List<string> names = [];
        if (scenario == "retention")
        {
            workspace.Change("modules.0.identity.moduleId", "\"memories\"");
            tests[0]!["name"] = "functional-retention";
            names.AddRange(["functional-retention", "second"]);
        }
        else
        {
            JsonNode template = tests[0]!.DeepClone();
            tests.Clear();
            for (int index = 0; index < 300; index++)
            {
                JsonNode entry = template.DeepClone();
                string name = "suite-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
                entry["name"] = name;
                entry["executionTimeoutSeconds"] = 14400;
                entry["stopTimeoutSeconds"] = 900;
                tests.Add(entry);
                names.Add(name);
            }
        }

        string manifest = workspace.Save();
        IReadOnlyList<PlatformModuleDeclaration> declarations;
        if (scenario == "overflow")
        {
            // Schema-valid batches are smaller. Exercise the selector's defensive arithmetic guard directly.
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(manifest));
            declarations = [new PlatformModuleDeclaration(manifest, document.RootElement.GetProperty("modules")[0].Clone()) { SourceHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(manifest))) }];
        }
        else
        {
            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
            validation.IsValid.ShouldBeTrue();
            declarations = validation.Declarations!;
        }

        ModuleTestSelection.SelectBatch(declarations, names, manifest, DateTimeOffset.UtcNow, out string? refusal).ShouldBeEmpty();
        refusal.ShouldNotBeNull().ShouldContain(scenario == "retention" ? "incompatible" : "finite limit");
    }

    /// <summary>Both native .NET platforms receive a run-private artifacts directory.</summary>
    /// <param name="executor">The declared native platform.</param>
    /// <returns>The actual stand-in argument assertions.</returns>
    [Theory]
    [InlineData("vstest")]
    [InlineData("mtp")]
    public async Task SelectedDotnetChildrenUsePrivateArtifactsAsync(string executor)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The stand-in uses a POSIX script.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        workspace.Change("modules.0.tests.0.executor", JsonSerializer.Serialize(executor));
        workspace.Change("modules.0.tests.0.entry", "\"suite.csproj\"");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "suite.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        using Process host = new();
        CompositionRunSession session = (await ReadyOptions(workspace.Root, manifest, host).SelectedPlatformStartOverride!(manifest, selected, TestContext.Current.CancellationToken).ConfigureAwait(true)).Session!;
        string command = Path.Combine(workspace.Root, "native-dotnet");
        CompositionTestFiles.WriteScript(command, "printf '%s\\n' \"$@\" > native-arguments.txt\nprintf '%s' '<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\"/></ResultSummary></TestRun>' > \"$HEXALITH_G4_TEST_REPORT_PATH\"\n");
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new(selected.Entry, selected.Executor), manifest, TestContext.Current.CancellationToken, command, selected).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        string[] arguments = await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "native-arguments.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        int marker = Array.IndexOf(arguments, "--artifacts-path");
        marker.ShouldBeGreaterThanOrEqualTo(0);
        arguments[marker + 1].ShouldBe(Path.Combine(session.Plan.Workspace, "test-builds"));
    }

    /// <summary>Outcome recording ignores child timing files and cleanup preserves batch membership.</summary>
    /// <returns>The durable clock and membership assertions.</returns>
    [Fact]
    public async Task TerminalTimingNeverReadsChildFileAndDownKeepsMembershipAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        IReadOnlyList<ModuleTestSelection> selections = ModuleTestSelection.SelectBatch(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, ["first", "second"], manifest, DateTimeOffset.UtcNow);
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selections[0], SelectedTests = selections };
        CompositionEngine engine = new(options);
        string runWorkspace = Path.Combine(options.WorkspaceRoot, CompositionTestFiles.RunId);
        CompositionWorkspace.CreatePrivateDirectory(runWorkspace);
        string hostile = Path.Combine(runWorkspace, "execution-timing.json");
        if (!OperatingSystem.IsWindows())
        {
            CompositionProcessResult fifo = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("mkfifo", [hostile], workspace.Root, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            fifo.ExitCode.ShouldBe(0);
        }
        else
        {
            await File.WriteAllTextAsync(hostile, "forged", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        ModuleTestExecutionTiming timing = ModuleTestExecutionTiming.Start(selections[0], DateTimeOffset.UtcNow);
        ModuleTestRunRecord recorded = (await engine.RecordTestOutcomeAsync(CompositionTestFiles.RunId, [], new("completed", ToolOutcome.Passed(), []), "unknown", new([], []), TestContext.Current.CancellationToken, executionTiming: timing).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        recorded.ExecutionTiming.ShouldBe(timing);
        CompositionRunState state = new(CompositionRunState.SupportedSchema, CompositionTestFiles.RunId, CompositionRunStatus.Ready, selections[0].ManifestHash, runWorkspace, null, null, null, DateTimeOffset.UtcNow) { SelectedTest = selections[0], TestRun = recorded };
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(hostile);
        CompositionEngine cleanup = new(Options(workspace.Root));
        _ = await cleanup.RecordTestOutcomeAsync(CompositionTestFiles.RunId, [], new("withdrawn", ToolOutcome.Passed(), []), "removed", new([], []), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord retained = (await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "selected-test.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        retained.Selections.ShouldBe(selections);
        retained.ExecutionTiming.ShouldBe(timing);
    }

    /// <summary>Child-created archive links cannot overwrite an outside sentinel.</summary>
    /// <param name="target">The archive root, directory or file replaced by the real child.</param>
    /// <returns>The owned-path refusal and outside-content assertions.</returns>
    [Theory]
    [InlineData("workspace-entry")]
    [InlineData("durable-root")]
    [InlineData("durable-entries")]
    [InlineData("durable-entry")]
    [InlineData("durable-file")]
    public async Task ArchiveRejectsChildCreatedParentAndFileLinksAsync(string target)
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The child creates POSIX symbolic links.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string outside = Path.Combine(workspace.Root, "outside");
        CompositionWorkspace.CreatePrivateDirectory(outside);
        string[] names = ["test-environment.json", "execution-timing.json", "native.trx", "functional-observations.json"];
        foreach (string name in names)
        {
            await File.WriteAllTextAsync(Path.Combine(outside, name), "outside-sentinel-" + name, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string link = target switch
        {
            "workspace-entry" => "path.parent / 'native-tests' / identity",
            "durable-root" => "root / 'durable'",
            "durable-entries" => "root / 'durable' / 'entries'",
            "durable-entry" => "root / 'durable' / 'entries' / identity",
            _ => "root / 'durable' / 'entries' / identity / 'test-environment.json'",
        };
        string script = "\nimport hashlib\nroot = pathlib.Path(__file__).parent\nidentity = hashlib.sha256(os.environ['HEXALITH_G4_TEST_NAME'].encode()).hexdigest()\nlink = " + link + "\nlink.parent.mkdir(parents=True, exist_ok=True)\nlink.symlink_to(root / 'outside'" + (target == "durable-file" ? " / 'test-environment.json'" : string.Empty) + ", target_is_directory=" + (target == "durable-file" ? "False" : "True") + ")\n";
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), script, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selection = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        using Process host = new();
        CompositionRunSession session = (await ReadyOptions(workspace.Root, manifest, host).SelectedPlatformStartOverride!(manifest, selection, TestContext.Current.CancellationToken).ConfigureAwait(true)).Session!;
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new(selection.Entry, selection.Executor), manifest, TestContext.Current.CancellationToken, selection: selection, archiveDirectory: Path.Combine(workspace.Root, "durable")).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.EvidenceSchemaOrPolicy);
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
        File.Exists(Path.Combine(workspace.Root, "children.jsonl")).ShouldBeTrue();
        foreach (string name in names)
        {
            (await File.ReadAllTextAsync(Path.Combine(outside, name), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("outside-sentinel-" + name);
        }
    }

    /// <summary>Each sequential entry retains its already-inspected functional observations with its report.</summary>
    /// <returns>The two real child and both archive-destination assertions.</returns>
    [Fact]
    public async Task BatchArchivesEachEntriesInspectedFunctionalObservationsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "\nobservation = {'runId': os.environ['HEXALITH_G4_RUN_ID'], 'status': 'functional-observations-passed', 'qualificationCredit': False, 'entry': os.environ['HEXALITH_G4_TEST_NAME']}\n(pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).parent / 'functional-observations.json').write_text(json.dumps(observation))\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using Process host = new();
        CompositionEngineOptions options = ReadyOptions(workspace.Root, manifest, host);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", manifest, "--profile", "first", "--profile", "second", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        exit.ShouldBe(0, output.ToString());
        (await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "children.jsonl"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Length.ShouldBe(2);
        foreach (string name in new[] { "first", "second" })
        {
            string identity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)));
            byte[]? snapshot = null;
            foreach (string archive in new[]
            {
                Path.Combine(options.WorkspaceRoot, CompositionTestFiles.RunId, "native-tests", identity),
                Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId, "entries", identity),
            })
            {
                byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(archive, "functional-observations.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
                using JsonDocument observation = JsonDocument.Parse(bytes);
                observation.RootElement.GetProperty("entry").GetString().ShouldBe(name);
                observation.RootElement.GetProperty("runId").GetString().ShouldBe(CompositionTestFiles.RunId);
                if (snapshot is not null)
                {
                    bytes.ShouldBe(snapshot);
                }

                snapshot = bytes;
                File.Exists(Path.Combine(archive, "native.trx")).ShouldBeTrue();
            }
        }
    }

    /// <summary>A child-created startup-task FIFO cannot block terminal metadata recording or renew its stopping bound.</summary>
    /// <returns>The real FIFO, regular metadata and bounded terminal-record assertions.</returns>
    [Fact]
    public async Task TerminalMetadataRejectsStartupTaskFifoWithinOriginalBoundAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("mkfifo is POSIX-only.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selection = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionEngineOptions options = Options(workspace.Root) with { SelectedTest = selection };
        CompositionEngine engine = new(options);
        string runWorkspace = Path.Combine(options.WorkspaceRoot, CompositionTestFiles.RunId);
        CompositionWorkspace.CreatePrivateDirectory(runWorkspace);
        await File.WriteAllTextAsync(Path.Combine(runWorkspace, "plan.json"), "{\"observed\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string fifo = Path.Combine(runWorkspace, "startup-task-results.json");
        CompositionProcessResult created = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("python3", ["-c", "import os,sys; os.mkfifo(sys.argv[1])", fifo], workspace.Root, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        created.ExitCode.ShouldBe(0);
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        stopping.CancelAfter(TimeSpan.FromSeconds(1));
        Stopwatch elapsed = Stopwatch.StartNew();
        Task<ModuleTestRunRecord?> recording = Task.Run(() => engine.RecordTestOutcomeAsync(CompositionTestFiles.RunId, [], new("completed", ToolOutcome.Passed(), []), "unknown", new([], []), stopping.Token), TestContext.Current.CancellationToken);
        try
        {
            ModuleTestRunRecord record = (await recording.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            record.TerminalStatus.ShouldBe("completed");
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
            string diagnostics = Path.Combine(options.StateDirectory, "diagnostics", CompositionTestFiles.RunId);
            File.Exists(Path.Combine(diagnostics, "startup-task-results.json")).ShouldBeFalse();
            (await File.ReadAllTextAsync(Path.Combine(diagnostics, "selected-plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("{\"observed\":true}");
        }
        finally
        {
            // Release a regressed blocking open so a failing test cannot leave a worker behind.
            _ = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("python3", ["-c", "import errno,os,sys\ntry:\n fd=os.open(sys.argv[1],os.O_WRONLY|os.O_NONBLOCK); os.write(fd,b'{}'); os.close(fd)\nexcept OSError as e:\n if e.errno != errno.ENXIO: raise", fifo], workspace.Root, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    private static CompositionEngineOptions ReadyOptions(string root, string manifest, Process appHost) => Options(root) with
    {
        SelectedPlatformStartOverride = async (_, selected, _) =>
        {
            CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(root) with { SelectedTest = selected }, manifest, root).ConfigureAwait(false);
            CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("eventstore", "project", "Healthy")]) { SelectedTest = selected }, "eventstore");
            return new(new("ready", ToolOutcome.Passed(), []), plan.RunId, new(plan, readiness, "private-fixture-key", appHost));
        },
        PlatformDownOverride = (_, _) => Task.FromResult(new CompositionDownResult(new("completed", ToolOutcome.Passed(), []), new([], []))),
    };
}
