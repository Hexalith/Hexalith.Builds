// <copyright file="TestEnvironmentProvisioningTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies credential-free descriptor handoff and independent startup/execution clocks.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed partial class TestEnvironmentProvisioningTests
{
    /// <summary>Delayed readiness preserves each sequential entry's full execution budget and one environment identity.</summary>
    /// <returns>The command, descriptor, activation and batch assertions.</returns>
    [Fact]
    public async Task ReadyBatchHandsValidatedDescriptorsToSequentialChildrenAfterStartupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        using Process appHost = new();
        int starts = 0;
        int cleanups = 0;
        CompositionRunSession? session = null;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            SelectedPlatformStartOverride = async (_, selected, token) =>
            {
                starts++;
                await Task.Delay(TimeSpan.FromMilliseconds(1200), token).ConfigureAwait(false);
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(false);
                CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("eventstore", "project", "Healthy")]), "eventstore");
                session = new CompositionRunSession(plan, readiness, "private-fixture-signing-key", appHost);
                await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, readiness, token).ConfigureAwait(false);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = async (_, token) =>
            {
                cleanups++;

                // Longer than the first entry's stop limit, shorter than the active last entry's limit.
                await Task.Delay(TimeSpan.FromMilliseconds(1500), token).ConfigureAwait(false);
                CompositionTestFiles.Delete(session!.Plan.Workspace);
                return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], []));
            },
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", manifest, "--profile", "first", "--profile", "second", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe(0, output.ToString());
            starts.ShouldBe(1);
            cleanups.ShouldBe(1);
        }

        string[] children = await File.ReadAllLinesAsync(Path.Combine(workspace.Root, "children.jsonl"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        children.Length.ShouldBe(2);
        using JsonDocument first = JsonDocument.Parse(children[0]);
        using JsonDocument second = JsonDocument.Parse(children[1]);
        first.RootElement.GetProperty("environment").GetString().ShouldBe(second.RootElement.GetProperty("environment").GetString());
        first.RootElement.GetProperty("name").GetString().ShouldBe("first");
        second.RootElement.GetProperty("name").GetString().ShouldBe("second");
        DateTimeOffset firstStart = first.RootElement.GetProperty("started").GetDateTimeOffset();
        DateTimeOffset secondStart = second.RootElement.GetProperty("started").GetDateTimeOffset();
        secondStart.ShouldBeGreaterThan(firstStart);
        (firstStart - session!.Plan.SelectedTest!.RequestedAtUtc).TotalSeconds.ShouldBeGreaterThan(1);
        (session.Plan.ExecutionTiming!.DeadlineUtc - session.Plan.ExecutionTiming.StartedAtUtc).TotalSeconds.ShouldBe(1);
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
        string diagnostics = Path.Combine(options.StateDirectory, "diagnostics", session.RunId);
        TestEnvironmentContractReader.ReadDescriptor(Path.Combine(diagnostics, "test-environment.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        string[] entries = Directory.GetDirectories(Path.Combine(diagnostics, "entries"));
        entries.Length.ShouldBe(2);
        foreach (string name in new[] { "first", "second" })
        {
            string entry = Path.Combine(diagnostics, "entries", Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))));
            TestEnvironmentContractReader.ReadDescriptor(Path.Combine(entry, "test-environment.json"), TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(entry, "native.trx"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            System.Text.Encoding.UTF8.GetString(bytes).ShouldContain("total=\"" + (name == "first" ? "1" : "2") + "\"");
        }
    }

    /// <summary>Incompatible runtime requirements and invalid names refuse before environment startup.</summary>
    /// <param name="scenario">The incompatible selection.</param>
    /// <returns>The refusal assertions.</returns>
    [Theory]
    [InlineData("mode")]
    [InlineData("runtime")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    public async Task IncompatibleBatchNeverStartsAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        if (scenario == "mode")
        {
            workspace.Change("modules.0.tests.1.mode", "\"package\"");
            workspace.Change("modules.0.tests.1.executor", "\"vstest\"");
            workspace.Change("modules.0.tests.1.entry", "\"suite.csproj\"");
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "suite.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario == "runtime")
        {
            workspace.Change("modules.0.tests.1.daprRuntimeVersion", "\"1.18.1\"");
        }

        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                throw new InvalidOperationException("Invalid batch started.");
            },
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            string second = scenario switch { "duplicate" => "first", "missing" => "absent", _ => "second" };
            int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "first", "--profile", second, "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.UsageOrManifest, output.ToString());
            starts.ShouldBe(0);
            File.Exists(Path.Combine(workspace.Root, "children.jsonl")).ShouldBeFalse();
        }
    }

    /// <summary>Missing catalog identity and invalid real observations cannot hand a descriptor to a child.</summary>
    /// <param name="scenario">The missing or invalid producer observation.</param>
    /// <returns>The fail-closed handoff assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The scenario is unknown.</exception>
    [Theory]
    [InlineData("catalog")]
    [InlineData("unhealthy")]
    [InlineData("owner")]
    [InlineData("module-owner")]
    [InlineData("endpoint")]
    [InlineData("endpoint-userinfo")]
    [InlineData("endpoint-query")]
    [InlineData("endpoint-fragment")]
    [InlineData("task")]
    [InlineData("source-drift")]
    [InlineData("dependency-drift")]
    [InlineData("catalog-drift")]
    [InlineData("descriptor")]
    [InlineData("missing-endpoints")]
    public async Task MissingProofNeverLaunchesAChildAsync(string scenario)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selection = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(true);
        CompositionReadiness readiness = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("eventstore", "project", "Healthy")]), "eventstore");
        switch (scenario)
        {
            case "catalog": File.Delete(Path.Combine(plan.Workspace, "source-mapping", "catalog-versions.json")); break;
            case "unhealthy": readiness = readiness with { Resources = [readiness.Resources[0] with { State = "Unhealthy" }] }; break;
            case "owner": readiness = readiness with { RunId = CompositionRunPlanFactory.NewRunId() }; break;
            case "module-owner": readiness = readiness with { Resources = [readiness.Resources[0] with { ModuleId = null }] }; break;
            case "endpoint":
            case "endpoint-userinfo":
            case "endpoint-query":
            case "endpoint-fragment":
                string unsafeUri = scenario switch
                {
                    "endpoint-userinfo" => "http://user:secret@127.0.0.1:20013/",
                    "endpoint-query" => "http://127.0.0.1:20013/?value=secret",
                    "endpoint-fragment" => "http://127.0.0.1:20013/#secret",
                    _ => "http://hosted.example.com/",
                };
                readiness = readiness with { Resources = [readiness.Resources[0] with { Endpoints = [.. readiness.Resources[0].Endpoints, new("unsafe", "http", new Uri(unsafeUri))] }] };
                break;
            case "task": plan = plan with { StartupTasks = [new("sample", "required-verify", "missing", [])] }; break;
            case "source-drift": await File.AppendAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "\n# drift", TestContext.Current.CancellationToken).ConfigureAwait(true); break;
            case "dependency-drift": await File.AppendAllTextAsync(Path.Combine(workspace.Root, "references", "Hexalith.EventStore", "module", "hexalith.module-manifest.v2.json"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true); break;
            case "catalog-drift": await File.AppendAllTextAsync(Path.Combine(plan.Workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true); break;
            case "descriptor": readiness = readiness with { Resources = [readiness.Resources[0] with { ModuleId = "invalid\nowner" }] }; break;
            case "missing-endpoints": readiness = readiness with { Resources = [readiness.Resources[0] with { Endpoints = [] }] }; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        using Process appHost = new();
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, readiness, "private-fixture-signing-key", appHost), new PersistedProfileNativeTests(selection.Entry, selection.Executor), manifest, TestContext.Current.CancellationToken, selection: selection).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        if (scenario.StartsWith("endpoint", StringComparison.Ordinal))
        {
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message == "An observed endpoint is invalid or unsafe.");
        }

        if (scenario == "module-owner")
        {
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message == "Every composed resource requires explicit declaration ownership. Shared runner infrastructure requires an actual selected Platform declaration.");
        }

        if (scenario == "catalog")
        {
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message == "Verified catalog identity is unavailable or inconsistent.");
        }

        if (scenario == "task")
        {
            result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message == "Required startup tasks have not all passed.");
        }

        File.Exists(Path.Combine(workspace.Root, "children.jsonl")).ShouldBeFalse();
    }

    /// <summary>Legacy evidence cannot represent a batch, so export refuses before startup.</summary>
    /// <returns>The no-allocation assertions.</returns>
    [Fact]
    public async Task BatchLegacyEvidenceRefusesBeforeProvisioningAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                throw new InvalidOperationException();
            },
        };
        StringWriter output = new();
        await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "first", "--profile", "second", "--evidence", "result.json", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest, output.ToString());
        output.ToString().ShouldContain("one declared suite");
        starts.ShouldBe(0);
        File.Exists(Path.Combine(workspace.Root, "result.json")).ShouldBeFalse();
    }

    /// <summary>A noncontract startup fallback requires the declaration's existing justified override before resources start.</summary>
    /// <param name="declared">Whether a real override exists.</param>
    /// <returns>The startup invocation assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFallbackIsCheckedBeforeProvisioningAsync(bool declared)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        workspace.Change("modules.0.lifecycle.startup", declared ? "{\"override\":{\"timeoutSeconds\":30,\"justification\":\"Explicit finite fixture budget\"}}" : "{}");
        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                return Task.FromResult(new CompositionStartResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []), null, null));
            },
        };
        StringWriter output = new();
        await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "first", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        starts.ShouldBe(declared ? 1 : 0, output.ToString());
        exit.ShouldBe(declared ? (int)ToolExitCode.ProductOrTest : (int)ToolExitCode.UsageOrManifest);
        if (!declared)
        {
            output.ToString().ShouldContain("600-second startup default");
        }
    }

    /// <summary>Actual unsupported or incomplete CI metadata refuses before any service start.</summary>
    /// <param name="context">The actual ambient execution context.</param>
    /// <returns>The early refusal or supported-start assertions.</returns>
    [Theory]
    [InlineData("other-ci")]
    [InlineData("missing-job")]
    [InlineData("bad-attempt")]
    [InlineData("invalid-repository")]
    [InlineData("unsupported-runner")]
    [InlineData("github-hosted")]
    [InlineData("self-hosted")]
    public async Task ActualCiContextIsCheckedBeforeProvisioningAsync(string context)
    {
        string[] names = ["CI", "GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_JOB", "RUNNER_OS", "RUNNER_ENVIRONMENT", "GITHUB_RUN_ATTEMPT"];
        string?[] prior = [.. names.Select(Environment.GetEnvironmentVariable)];
        try
        {
            string runner = context switch { "self-hosted" => "self-hosted", "unsupported-runner" => "unknown", _ => "github-hosted" };
            string?[] values = ["true", context == "other-ci" ? null : "true", context == "invalid-repository" ? " owner/repository" : "owner/repository", "123", context == "missing-job" ? null : "tests", "Linux", runner, context == "bad-attempt" ? "0" : "1"];
            for (int i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], values[i]);
            }

            using PlatformManifestTestWorkspace workspace = new();
            Declare(workspace);
            int starts = 0;
            CompositionEngineOptions options = Options(workspace.Root) with
            {
                PlatformStartOverride = (_, _) =>
                {
                    starts++;
                    return Task.FromResult(new CompositionStartResult(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []), null, null));
                },
            };
            StringWriter output = new();
            await using ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "first", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            bool supported = context is "github-hosted" or "self-hosted";
            starts.ShouldBe(supported ? 1 : 0, output.ToString());
            exit.ShouldBe(supported ? (int)ToolExitCode.ProductOrTest : (int)ToolExitCode.UsageOrManifest);
            if (!supported)
            {
                output.ToString().ShouldContain("actual GitHub Actions metadata");
            }
        }
        finally
        {
            for (int i = 0; i < names.Length; i++)
            {
                Environment.SetEnvironmentVariable(names[i], prior[i]);
            }
        }
    }

    /// <summary>A child cannot make archival reread its replacement descriptor or block on a FIFO.</summary>
    /// <param name="replacement">The child replacement after consuming the handoff.</param>
    /// <returns>The bounded archive assertions.</returns>
    [Theory]
    [InlineData("changed")]
    [InlineData("fifo")]
    public async Task DescriptorArchiveUsesTheValidatedPreLaunchSnapshotAsync(string replacement)
    {
        if (replacement == "fifo" && OperatingSystem.IsWindows())
        {
            Assert.Skip("mkfifo is POSIX-only.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string script = Path.Combine(workspace.Root, "runner.py");
        await File.AppendAllTextAsync(script, "\npath.unlink()\n" + (replacement == "fifo" ? "os.mkfifo(path)\n" : "path.write_text('{\"changed\":true}')\n"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selection = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(true);
        using Process host = new();
        CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("eventstore", "project", "Healthy")]), "eventstore") with { SelectedTest = selection };
        string durable = Path.Combine(workspace.Root, "durable");
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(new CompositionRunSession(plan, ready, "private-fixture-key", host), new PersistedProfileNativeTests(selection.Entry, selection.Executor), manifest, TestContext.Current.CancellationToken, selection: selection, archiveDirectory: durable).WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(result.Result));
        string archived = Path.Combine(durable, "test-environment.json");
        TestEnvironmentContractReader.ReadDescriptor(archived, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        (await File.ReadAllTextAsync(archived, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotContain("changed");
    }

    /// <summary>Expired original processing bounds and I/O failures preserve the child's cause and first stopping timestamp.</summary>
    /// <param name="expired">Whether the original bound is already spent.</param>
    /// <returns>The evidence-failure and causal-order assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchivalFailurePreservesTheFirstOutcomeAndStoppingTimestampAsync(bool expired)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace);
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "first", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected, ExecutionTiming = ModuleTestExecutionTiming.Start(selected, DateTimeOffset.UtcNow) };
        using Process host = new();
        CompositionRunSession session = new(plan, new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://localhost:1"), null, []), "private-key", host);
        NativeTestExecutionResult child = new(new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []), null, "inspected"u8.ToArray()) { StoppingTimestamp = 12345 };
        string archive = Path.Combine(workspace.Root, "blocked");
        await File.WriteAllTextAsync(archive, "blocked", TestContext.Current.CancellationToken).ConfigureAwait(true);
        using CancellationTokenSource processing = new();
        if (expired)
        {
            await processing.CancelAsync().ConfigureAwait(true);
        }

        NativeTestExecutionResult result = await NativeTestExecutor.ArchiveSelectedAsync(child, session, "{}"u8.ToArray(), archive, processing.Token).ConfigureAwait(true);
        result.Result.Outcome.ShouldBe(child.Result.Outcome);
        result.StoppingTimestamp.ShouldBe(child.StoppingTimestamp);
        result.ReportBytes.ShouldBe(child.ReportBytes);
        result.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
        File.Exists(Path.Combine(plan.Workspace, "execution-timing.json")).ShouldBeFalse();
    }

    private static CompositionEngineOptions Options(string root) => new("unused-apphost", "unused-child")
    {
        SourceMapping = new SourceMapping(WorkspaceMode.Source, root, "sample", [new("sample", "source", root)], false),
        WorkspaceRoot = Path.Combine(root, "workspace"),
        StateDirectory = Path.Combine(root, "state"),
        ContinuousIntegrationOverride = false,
    };

    private static void Declare(PlatformManifestTestWorkspace workspace)
    {
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.tests", """
            [{"name":"first","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":1,"stopTimeoutSeconds":1,"mode":"source"},
             {"name":"second","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":1,"stopTimeoutSeconds":4,"mode":"source"}]
            """);
        File.WriteAllText(Path.Combine(workspace.Root, "runner.py"), """
            import os, json, pathlib, time, base64, datetime
            path = pathlib.Path(os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'])
            assert path.is_absolute()
            descriptor = json.loads(path.read_text())
            assert descriptor['schema'] == 'hexalith.test-environment.v1'
            readiness = json.loads(pathlib.Path(os.environ['HEXALITH_G4_READINESS_PATH']).read_text())
            assert readiness['selectedTest']['name'] == os.environ['HEXALITH_G4_TEST_NAME']
            bindings = descriptor['bindings']
            assert bindings['environment']['readiness']['status'] == 'ready'
            assert all(t['status'] == 'passed' for t in bindings['environment']['startupTasks']['tasks'])
            assert bindings['deadlines']['execution']['effectiveSeconds'] == 1
            assert datetime.datetime.fromisoformat(os.environ['HEXALITH_G4_EXECUTION_DEADLINE_UTC']) == datetime.datetime.fromisoformat(bindings['deadlines']['execution']['expiresAt'].replace('Z', '+00:00'))
            token_payload = os.environ['HEXALITH_G4_ACCESS_TOKEN'].split('.')[1]
            claims = json.loads(base64.urlsafe_b64decode(token_payload + '=' * (-len(token_payload) % 4)))
            expiry = datetime.datetime.fromisoformat(bindings['deadlines']['execution']['expiresAt'].replace('Z', '+00:00')).timestamp()
            assert claims['exp'] >= expiry and claims['exp'] - expiry < 1
            assert int(datetime.datetime.fromisoformat(os.environ['HEXALITH_G4_CREDENTIAL_EXPIRY_UTC']).timestamp()) == claims['exp']
            with (pathlib.Path(__file__).parent / 'children.jsonl').open('a') as output:
                output.write(json.dumps({'name':os.environ['HEXALITH_G4_TEST_NAME'],'environment':bindings['run']['environmentId'],'started':bindings['deadlines']['execution']['originAt']}) + '\n')
            time.sleep(0.15)
            count = '1' if os.environ['HEXALITH_G4_TEST_NAME'] == 'first' else '2'
            report = f'<TestRun><ResultSummary outcome="Completed"><Counters total="{count}" passed="{count}" failed="0" /></ResultSummary></TestRun>'
            pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text(report)
            """);
    }
}