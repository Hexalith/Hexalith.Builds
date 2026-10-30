// <copyright file="CandidateRunnerTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.CandidatePolicy;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Verifies durable runner/writer and public command candidate bindings.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CandidateRunnerTests
{
    private static readonly string[] _consumers = ["composed-host", "mcpcli", "tests"];
    private static readonly string[] _required = ["test-1"];

    /// <summary>Declared/loaded identities and suites must agree even when a result says passed.</summary>
    /// <param name="scenario">The inconsistent or missing evidence.</param>
    /// <param name="condition">The named refusal.</param>
    /// <returns>The policy matrix assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture scenario is unsupported.</exception>
    [Theory]
    [InlineData("baseline", "candidate-required-diagnostics-only")]
    [InlineData("policy", "exact-policy-required")]
    [InlineData("descriptor", "descriptor-result-disagreement")]
    [InlineData("producer", "decision-producer-mismatch")]
    [InlineData("loaded-missing", "artifact-identities-required")]
    [InlineData("loaded-mismatch", "artifact-identity-mismatch")]
    [InlineData("wrong-suite", "complete-required-suite-proof-required")]
    [InlineData("skipped", "complete-required-suite-proof-required")]
    [InlineData("empty", "complete-required-suite-proof-required")]
    [InlineData("counts", "complete-required-suite-proof-required")]
    [InlineData("tool", "tool-qualification-required")]
    [InlineData("cancelled", "passed-outcome-required")]
    public async Task PassingLabelCannotOverrideMissingOrMismatchedProofAsync(string scenario, string condition)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        JsonNode after = result["bindings"]!;
        switch (scenario)
        {
            case "baseline":
                after["candidate"] = JsonSerializer.SerializeToNode(new { state = "populated", classification = "baseline-only", reason = "diagnostic" });
                break;
            case "policy":
                result["policyId"] = "another-policy";
                break;
            case "descriptor":
                descriptor["bindings"]!["run"]!["environmentId"] = "another-environment";
                break;
            case "producer":
                after["decision"]!["producingRunId"] = "another-run";
                break;
            case "loaded-missing":
                after["artifacts"]!["loaded"] = JsonSerializer.SerializeToNode(new { state = "not-yet-populated" });
                break;
            case "loaded-mismatch":
                after["artifacts"]!["loaded"]!["items"]![0]!["contentIdentity"] = "sha256:" + new string('8', 64);
                break;
            case "wrong-suite":
                after["suite"]!["completion"]!["executedSuiteContentIdentity"] = "sha256:" + new string('8', 64);
                break;
            case "skipped":
                after["suite"]!["completion"]!["tests"]![0]!["outcome"] = "skipped";
                break;
            case "empty":
                after["suite"]!["declared"]!["requiredTestIds"] = new JsonArray();
                descriptor["bindings"]!["suite"]!["declared"] = after["suite"]!["declared"]!.DeepClone();
                break;
            case "counts":
                after["suite"]!["completion"]!["counts"]!["completed"] = 0;
                break;
            case "tool":
                after["tool"] = JsonSerializer.SerializeToNode(new { state = "not-yet-populated" });
                descriptor["bindings"]!["tool"] = after["tool"]!.DeepClone();
                break;
            case "cancelled":
                result["terminal"]!["outcome"] = "cancelled";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result))).Condition.ShouldBe(condition);
    }

    /// <summary>Even a complete, reader-valid fixture cannot authenticate its own tool qualification references.</summary>
    /// <returns>The explicit acceptance refusal without claiming live qualification.</returns>
    [Fact]
    public async Task CompleteFixtureStillRequiresAuthenticToolQualificationAsync()
    {
        using CandidateGateFixture fixture = new();
        string request = fixture.Sign();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result));
        using PlatformManifestTestWorkspace workspace = new();
        string environmentPath = Path.Combine(workspace.Root, "descriptor.json");
        string resultPath = Path.Combine(workspace.Root, "result.json");
        await File.WriteAllTextAsync(environmentPath, descriptor.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(resultPath, result.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReader.ReadDescriptor(environmentPath, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateAcceptanceAsync(fixture.Gate, request, environmentPath, resultPath, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("tool-qualification-authentication-unavailable");
    }

    /// <summary>The actual descriptor writer and terminal writer preserve one issued attempt. Fixture execution grants no qualification.</summary>
    /// <returns>The validated shared binding snapshots.</returns>
    [Fact]
    public async Task DescriptorAndTerminalWritersPreserveSameIssuedCandidateAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState original) = LocalRunLifecycleFixture.Create(workspace);
        using CandidateGateFixture fixture = new();
        fixture.Payload["moduleId"] = selected.ModuleId;
        fixture.Payload["attemptSequence"] = 2;
        fixture.CurrentAttempt = 2;
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = binding.RunId, AttemptId = binding.AttemptId, CandidatePolicy = binding, SelectedTest = selected };
        plan = await TestEnvironmentProvisioningFixture.PrepareAsync(plan, workspace.Save(), workspace.Root).ConfigureAwait(true);
        plan = plan with { ExecutionTiming = ModuleTestExecutionTiming.Start(selected, DateTimeOffset.UtcNow) };
        CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("eventstore", "project", "Healthy")]), selected.ModuleId);
        using System.Diagnostics.Process host = new();
        CompositionRunSession session = new(plan, ready, "private-offline-fixture-key", host);
        string descriptorPath = await TestEnvironmentDescriptorWriter.WriteAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonElement descriptor = TestEnvironmentContractReader.ReadDescriptor(descriptorPath, TestContext.Current.CancellationToken).Document!.Value;
        CompositionRunState state = original with { RunId = plan.RunId, AttemptId = plan.AttemptId, EnvironmentId = plan.EnvironmentId, CandidatePolicy = binding };
        ModuleTestRunRecord record = new(plan.RunId, plan.RunId, selected, null, null, null, null, null, false, ready.Resources, "passed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new([], [])) { EnvironmentBindings = descriptor.GetProperty("bindings") };
        string resultPath = await TestResultWriter.WriteAsync(Path.Combine(workspace.Root, "diagnostics"), state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonElement result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value;
        foreach (string field in new[] { "candidate", "decision", "reuseLineage", "run" })
        {
            JsonNode.DeepEquals(JsonNode.Parse(descriptor.GetProperty("bindings").GetProperty(field).GetRawText()), JsonNode.Parse(result.GetProperty("bindings").GetProperty(field).GetRawText())).ShouldBeTrue();
        }

        result.GetProperty("bindings").GetProperty("run").GetProperty("attemptSequence").GetInt32().ShouldBe(2);
        record.QualificationCredit.ShouldBeFalse();
    }

    /// <summary>Partial startup persists issued candidate/attempts without inventing artifact or completion proof.</summary>
    /// <returns>The reader-valid partial snapshot and immutable binding assertions.</returns>
    [Fact]
    public async Task PartialStartupPreservesIssuedBindingsAndSequenceAsync()
    {
        using CandidateGateFixture fixture = new();
        fixture.CurrentAttempt = 2;
        fixture.Payload["attemptSequence"] = 2;
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState original) = LocalRunLifecycleFixture.Create(workspace, "package");
        CompositionRunState state = original with { RunId = binding.RunId, AttemptId = binding.AttemptId, CandidatePolicy = binding, ExecutionContext = fixture.Context };
        ModuleTestRunRecord record = new(binding.RunId, binding.RunId, selected with { ModuleId = "orders" }, null, null, null, null, null, false, [], "failed", ToolOutcome.Passed().Fail(ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "HXR014", ToolExitCode.PrerequisiteUnavailable), DateTimeOffset.UtcNow, "unknown", new([], [], true));
        string path = await TestResultWriter.WriteAsync(Path.Combine(workspace.Root, "diagnostics"), state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(string.Join(';', result.Diagnostics.Select(item => item.Message)));
        JsonElement bindings = result.Document!.Value.GetProperty("bindings");
        bindings.GetProperty("run").GetProperty("attemptSequence").GetInt32().ShouldBe(2);
        bindings.GetProperty("candidate").GetProperty("committedRevision").GetString().ShouldBe(CandidateGateFixture.Merge);
        bindings.GetProperty("decision").GetProperty("producingAttemptId").GetString().ShouldBe(binding.AttemptId);
        bindings.GetProperty("artifacts").GetProperty("loaded").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        bindings.GetProperty("suite").GetProperty("completion").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        JsonNode changed = JsonNode.Parse(bindings.GetRawText())!;
        changed["decision"]!["targetCommit"] = new string('d', 40);
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(Path.Combine(workspace.Root, "diagnostics"), state, record with { EnvironmentBindings = JsonSerializer.SerializeToElement(changed) }, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document!.Value.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("failed");
    }

    /// <summary>Public validation refetches current authority before considering saved descriptor/result JSON.</summary>
    /// <returns>The public named refusal and fresh API observation.</returns>
    [Fact]
    public async Task ValidateResultCommandRefetchesSupersedingAttemptAsync()
    {
        using CandidateGateFixture fixture = new();
        string request = fixture.Sign();
        _ = await fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.CurrentAttempt = 2;
        CompositionEngineOptions options = new("unused", "unused") { CandidateGateClientOverride = fixture.Gate };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["validate-result", "--candidate-request", request, "--environment", "unused.json", "--result", "unused.json", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        output.ToString().ShouldContain("latest-issued-attempt-required");
        output.ToString().ShouldContain("HXP301");
    }

    /// <summary>The public test command refuses an authenticated but stale candidate before invoking its service-start boundary.</summary>
    /// <returns>The named pre-provisioning refusal and untouched startup hooks.</returns>
    [Fact]
    public async Task PublicCandidateCommandChecksCurrentStateBeforeStartupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        using CandidateGateFixture fixture = new();
        fixture.Payload["moduleId"] = selected.ModuleId;
        string request = fixture.Sign();
        fixture.ChangedHead = true;
        int starts = 0;
        CompositionEngineOptions options = new("unused-apphost", "unused-child")
        {
            CandidateGateClientOverride = fixture.Gate,
            SourceMapping = new Hexalith.Builds.Tooling.Workspace.SourceMapping(Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            RootManifestCheckOverride = (_, _) => Task.FromResult(true),
            StateDirectory = Path.Combine(workspace.Root, "state"),
            WorkspaceRoot = Path.Combine(workspace.Root, "runs"),
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                throw new InvalidOperationException("A stale candidate started services.");
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "verify", "--candidate-request", request, "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest, output.ToString());
        output.ToString().ShouldContain("pr-binding-mismatch");
        starts.ShouldBe(0);
        Directory.Exists(options.StateDirectory).ShouldBeFalse();
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Legacy candidates refuse before a service startup hook can run.</summary>
    /// <returns>The public pre-provisioning refusal.</returns>
    [Fact]
    public async Task CandidateRequestCannotUseLegacyRunnerContractAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string manifest = Path.Combine(workspace.Root, "legacy.json");
        await File.WriteAllTextAsync(manifest, "{\"schema\":\"hexalith.module-manifest.v1\"}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", manifest, "--profile", "full", "--candidate-request", "unsigned.json", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        output.ToString().ShouldContain("declared-candidate-suite-required");
    }

    /// <summary>Module aliases and ambiguous expected declarations cannot cover an absent candidate artifact.</summary>
    /// <param name="duplicate">Whether to create an ambiguous expected duplicate instead of a foreign alias.</param>
    /// <returns>The artifact identity refusal.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpectedArtifactCoverageUsesModuleAndContentAsync(bool duplicate)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        JsonArray expected = result["bindings"]!["artifacts"]!["expected"]!["items"]!.AsArray();
        JsonNode dependency = expected[0]!.DeepClone();
        if (duplicate)
        {
            dependency["contentIdentity"] = "sha256:" + new string('9', 64);
        }
        else
        {
            dependency["moduleId"] = "dependency";
            foreach (JsonNode? loaded in result["bindings"]!["artifacts"]!["loaded"]!["items"]!.AsArray())
            {
                loaded!["moduleId"] = "dependency";
            }
        }

        expected.Add(dependency);
        descriptor["bindings"]!["artifacts"]!["expected"] = result["bindings"]!["artifacts"]!["expected"]!.DeepClone();
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result))).Condition.ShouldBe("artifact-identity-mismatch");
    }

    /// <summary>Startup declaration identity and requirement flags survive advancing observations.</summary>
    /// <param name="mutation">The altered declaration field, or observations only.</param>
    /// <returns>The immutable declaration assertions.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("id")]
    [InlineData("required")]
    [InlineData("observations")]
    public async Task StartupDeclarationFieldsAreImmutableAsync(string mutation)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        JsonNode task = JsonSerializer.SerializeToNode(new { taskId = "required-startup", required = true, status = "pending", diagnosticIds = Array.Empty<string>() })!;
        descriptor["bindings"]!["environment"]!["startupTasks"]!["tasks"] = new JsonArray(task.DeepClone());
        task["status"] = "passed";
        result["bindings"]!["environment"]!["startupTasks"]!["tasks"] = new JsonArray(task);
        if (mutation == "missing")
        {
            result["bindings"]!["environment"]!["startupTasks"]!["tasks"] = new JsonArray();
        }
        else if (mutation == "id")
        {
            task["taskId"] = "replacement";
        }
        else if (mutation == "required")
        {
            task["required"] = false;
        }

        if (mutation == "observations")
        {
            CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result));
        }
        else
        {
            Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result))).Condition.ShouldBe("descriptor-result-disagreement");
        }
    }

    /// <summary>Existing attachment acceptance and expiry are immutable while ends and later holds may advance.</summary>
    /// <param name="mutation">The hold mutation or valid progression.</param>
    /// <returns>The hold binding assertions.</returns>
    [Theory]
    [InlineData("acceptedAt")]
    [InlineData("expiresAt")]
    [InlineData("requestedHoldSeconds")]
    [InlineData("progress")]
    public async Task AttachmentHoldOriginsAreImmutableAsync(string mutation)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        descriptor["bindings"]!["run"]!["role"] = "attached";
        descriptor["bindings"]!["run"]!["ownerRunId"] = "owner-run";
        descriptor["bindings"]!["run"]!["attachedRunId"] = binding.RunId;
        result["bindings"]!["run"] = descriptor["bindings"]!["run"]!.DeepClone();
        JsonNode hold = JsonSerializer.SerializeToNode(new { originalAttachmentRequestId = "hold-1", environmentId = "sample-environment", ownerRunId = "owner-run", attachedRunId = binding.RunId, requestedHoldSeconds = 60, acceptedAt = "2026-10-22T12:00:00Z", effectiveHoldSeconds = 60, expiresAt = "2026-10-22T12:01:00Z", end = new { state = "active" } })!;
        descriptor["bindings"]!["attachments"]!["holds"] = new JsonArray(hold.DeepClone());
        result["bindings"]!["attachments"]!["holds"] = new JsonArray(hold);
        if (mutation == "progress")
        {
            hold["end"] = JsonSerializer.SerializeToNode(new { state = "ended", endedAt = "2026-10-22T12:00:30Z", reason = "terminal-outcome" });
            JsonNode later = hold.DeepClone();
            later["originalAttachmentRequestId"] = "hold-2";
            later["attachedRunId"] = "attached-2";
            result["bindings"]!["attachments"]!["holds"]!.AsArray().Add(later);
            CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result));
        }
        else
        {
            hold[mutation] = mutation == "requestedHoldSeconds" ? JsonValue.Create(120) : JsonValue.Create("2026-10-22T12:02:00Z");
            Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result))).Condition.ShouldBe("descriptor-result-disagreement");
        }
    }

    /// <summary>Contradictory passing durations cannot exceed either independent budget.</summary>
    /// <param name="deadline">The deadline to overrun.</param>
    /// <returns>The named policy refusal and continued validity of a failed timeout representation.</returns>
    [Theory]
    [InlineData("startup")]
    [InlineData("execution")]
    public async Task PassingActualDurationsCannotExceedBudgetsAsync(string deadline)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (JsonObject descriptor, JsonObject result) = Evidence(binding, fixture.Context);
        if (deadline == "execution")
        {
            JsonNode timing = JsonSerializer.SerializeToNode(new { state = "populated", origin = "test-execution-started", originAt = "2026-10-22T12:00:10Z", effectiveSeconds = 10, expiresAt = "2026-10-22T12:00:20Z", configurationContentIdentity = "sha256:" + new string('1', 64) })!;
            descriptor["bindings"]!["deadlines"]!["execution"] = timing.DeepClone();
            result["bindings"]!["deadlines"]!["execution"] = timing;
        }

        result["bindings"]!["deadlines"]![deadline]!["actualDurationSeconds"] = deadline == "startup" ? 601 : 11;
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateEvidence(binding, JsonSerializer.SerializeToElement(descriptor), JsonSerializer.SerializeToElement(result))).Condition.ShouldBe("passing-deadline-exceeded");
        result["terminal"]!["outcome"] = "failed";
        result["terminal"]!["cause"] = deadline == "startup" ? "startup-timeout" : "execution-timeout";
        using PlatformManifestTestWorkspace workspace = new();
        string path = Path.Combine(workspace.Root, "timeout.json");
        await File.WriteAllTextAsync(path, result.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Malformed independently loaded enrollment always produces metadata-only HXP301 JSON.</summary>
    /// <param name="field">The missing/null enrollment field.</param>
    /// <returns>The public stable enrollment refusal.</returns>
    [Theory]
    [InlineData("all")]
    [InlineData("WorkflowRef")]
    [InlineData("WorkflowSha")]
    [InlineData("Environment")]
    [InlineData("CallerWorkflowPath")]
    [InlineData("CallerContentIdentity")]
    [InlineData("ConsumerJobId")]
    [InlineData("ConsumerJobName")]
    public async Task MalformedEnrollmentHasStablePublicJsonAsync(string field)
    {
        using CandidateGateFixture fixture = new();
        JsonObject config = field == "all" ? [] : JsonSerializer.SerializeToNode(fixture.Configuration)!.AsObject();
        if (field != "all")
        {
            config[field] = null;
        }

        CandidateGateClient gate = new(fixture.Client, JsonSerializer.Deserialize<CandidateGateConfiguration>(config.ToJsonString())!, "fixture-read-token");
        CompositionEngineOptions options = new("unused", "unused") { CandidateGateClientOverride = gate };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["validate-result", "--candidate-request", "/private/unreadable-request", "--environment", "/private/environment", "--result", "/private/result", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        output.ToString().ShouldContain("HXP301");
        output.ToString().ShouldContain("issuer-not-enrolled");
        output.ToString().ShouldNotContain("/private/");
    }

    /// <summary>Eligible authenticated candidates reach the actual Git source check and refuse wrong or dirty inputs.</summary>
    /// <param name="mutation">The checkout violation.</param>
    /// <returns>The named source refusal with zero service starts.</returns>
    [Theory]
    [InlineData("head")]
    [InlineData("tracked")]
    [InlineData("untracked")]
    [InlineData("replay")]
    public async Task PublicCandidateCommandRefusesReplayAndInvalidGitInputsAsync(string mutation)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace, "package");
        string manifest = workspace.Save();
        _ = await GitAsync(workspace.Root, "init", "--quiet").ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "config", "user.name", "Fixture").ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "config", "user.email", "fixture@example.invalid").ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "add", ".").ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "commit", "--quiet", "-m", "test: create base").ConfigureAwait(true);
        string baseCommit = (await GitAsync(workspace.Root, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await GitAsync(workspace.Root, "checkout", "--quiet", "-b", "feature").ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "commit", "--quiet", "--allow-empty", "-m", "test: create head").ConfigureAwait(true);
        string head = (await GitAsync(workspace.Root, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await GitAsync(workspace.Root, "checkout", "--quiet", "-b", "candidate", baseCommit).ConfigureAwait(true);
        _ = await GitAsync(workspace.Root, "merge", "--quiet", "--no-ff", "feature", "-m", "test: merge candidate").ConfigureAwait(true);
        string merge = (await GitAsync(workspace.Root, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        using CandidateGateFixture fixture = new();
        fixture.Payload["moduleId"] = selected.ModuleId;
        fixture.BindCandidate(merge, head, baseCommit);
        string request = fixture.Sign();
        if (mutation == "head")
        {
            _ = await GitAsync(workspace.Root, "checkout", "--quiet", "feature").ConfigureAwait(true);
        }
        else if (mutation == "replay")
        {
            fixture.ConsumerProofMode = "issuance";
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, mutation == "tracked" ? "README.md" : "uncommitted.cs"), "uncommitted candidate input", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        int starts = 0;
        CompositionEngineOptions options = new("unused-apphost", "unused-child")
        {
            CandidateGateClientOverride = fixture.Gate,
            SourceMapping = new Hexalith.Builds.Tooling.Workspace.SourceMapping(Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Package, workspace.Root, "sample", [new("sample", "source", workspace.Root)], true),
            RootManifestCheckOverride = (_, _) => Task.FromResult(true),
            StateDirectory = Path.Combine(workspace.Root, "state"),
            WorkspaceRoot = Path.Combine(workspace.Root, "runs"),
            PlatformStartOverride = (_, _) =>
            {
                starts++;
                throw new InvalidOperationException("An invalid checkout started services.");
            },
        };
        string[] variables = ["CI", "GITHUB_ACTIONS", "GITHUB_REPOSITORY", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT", "GITHUB_JOB", "RUNNER_OS", "RUNNER_ENVIRONMENT"];
        string?[] previous = [.. variables.Select(Environment.GetEnvironmentVariable)];
        string[] values = ["true", "true", "Hexalith/Orders", "101", "1", "integration", "Linux", "github-hosted"];
        try
        {
            for (int index = 0; index < variables.Length; index++)
            {
                Environment.SetEnvironmentVariable(variables[index], values[index]);
            }

            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
            int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", manifest, "--profile", "verify", "--mode", "package", "--candidate-request", request, "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.UsageOrManifest, output.ToString());
            output.ToString().ShouldContain("HXP301");
            output.ToString().ShouldContain(mutation == "replay" ? "issuer-authentication-required" : "clean-exact-candidate-checkout-required");
            starts.ShouldBe(0);
            Directory.Exists(options.StateDirectory).ShouldBeFalse();
            Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
        }
        finally
        {
            for (int index = 0; index < variables.Length; index++)
            {
                Environment.SetEnvironmentVariable(variables[index], previous[index]);
            }
        }
    }

    /// <summary>An actual missing Aspire executable records the issued candidate's first startup outcome and preserves peers.</summary>
    /// <returns>The durable partial-startup result and peer preservation assertions.</returns>
    [Fact]
    public async Task IssuedCandidatePrerequisiteFailurePublishesTerminalResultAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        using CandidateGateFixture fixture = new();
        fixture.Payload["moduleId"] = selected.ModuleId;
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(true);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            CandidatePolicy = binding,
            SourceMapping = plan.SourceMapping,
            AspireCommand = Path.Combine(workspace.Root, "missing-aspire"),
            RetainOnFailure = false,
        };
        CompositionEngine engine = new(options);
        string peer = Guid.NewGuid().ToString("N");
        string peerWorkspace = Path.Combine(options.WorkspaceRoot, peer);
        _ = Directory.CreateDirectory(peerWorkspace);
        string sentinel = Path.Combine(peerWorkspace, "peer.txt");
        await File.WriteAllTextAsync(sentinel, "peer survives", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await engine.StateStore.WriteAsync(CompositionRunPlanFactory.CreateInitialState(peer, peerWorkspace, selected.ManifestHash, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionStartResult started = await engine.StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        started.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        string resultPath = Path.Combine(options.StateDirectory, "diagnostics", binding.RunId, "test-result.json");
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(string.Join(';', result.Diagnostics.Select(item => item.Message)));
        result.Document!.Value.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("failed");
        result.Document.Value.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("startup-failure");
        result.Document.Value.GetProperty("bindings").GetProperty("run").GetProperty("attemptId").GetString().ShouldBe(binding.AttemptId);
        result.Document.Value.GetProperty("bindings").GetProperty("candidate").GetProperty("committedRevision").GetString().ShouldBe(CandidateGateFixture.Merge);
        (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("peer survives");
        _ = (await engine.StateStore.TryReadAsync(peer, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
    }

    private static async Task<CompositionProcessResult> GitAsync(string root, params string[] arguments)
    {
        CompositionProcessResult result = await Hexalith.Builds.ModuleTool.Tests.Workspace.WorkspaceGitFixture.RunGitAsync(root, arguments).ConfigureAwait(true);
        result.ExitCode.ShouldBe(0, result.ErrorOutput);
        return result;
    }

    private static (JsonObject Descriptor, JsonObject Result) Evidence(CandidatePolicyBinding binding, JsonElement context)
    {
        JsonObject descriptor = JsonNode.Parse(File.ReadAllText(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test/fixtures/module/test-environment-v1/descriptor.json")))!.AsObject();
        JsonObject result = JsonNode.Parse(File.ReadAllText(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test/fixtures/module/test-environment-v1/result.json")))!.AsObject();
        string content = "sha256:" + new string('1', 64);
        JsonObject bindings = descriptor["bindings"]!.AsObject();
        bindings["run"] = JsonSerializer.SerializeToNode(new { runId = binding.RunId, attemptId = binding.AttemptId, attemptSequence = binding.AttemptSequence, ownerRunId = binding.RunId, environmentId = "sample-environment", role = "owner" });
        bindings["candidate"] = JsonNode.Parse(binding.Candidate.GetRawText());
        bindings["decision"] = JsonNode.Parse(binding.Decision.GetRawText());
        bindings["reuseLineage"] = JsonNode.Parse(binding.ReuseLineage.GetRawText());
        bindings["environment"]!["mode"] = "package";
        bindings["environment"]!["executionContext"] = JsonNode.Parse(context.GetRawText());
        object qualification = new { state = "populated", referenceId = "offline-fixture-not-authentic", toolVersion = "1.0.0", toolContentIdentity = content, policyId = TestEnvironmentContractReader.PolicyId };
        bindings["tool"] = JsonSerializer.SerializeToNode(new { state = "populated", name = "hexalith-module", version = "1.0.0", contentIdentity = content, buildsAdoption = qualification, platformAcceptance = qualification, integrationQualification = qualification });
        bindings["artifacts"]!["expected"] = JsonSerializer.SerializeToNode(new { state = "populated", items = new[] { new { artifactId = "Orders", moduleId = "orders", kind = "contracts", version = "1.0.0", repository = "Hexalith/Orders", committedRevision = CandidateGateFixture.Merge, configuration = "Release", contentIdentity = content } } });
        bindings["artifacts"]!["loaded"] = JsonSerializer.SerializeToNode(new { state = "populated", items = _consumers.Select(consumer => new { artifactId = "Orders", moduleId = "orders", consumerKind = consumer, consumerId = consumer, contentIdentity = content }) });
        bindings["suite"]!["declared"] = JsonSerializer.SerializeToNode(new { state = "populated", suiteId = "required", suiteContentIdentity = content, configurationContentIdentity = content, requiredTestSetContentIdentity = content, requiredTestIds = _required });
        bindings["suite"]!["completion"] = JsonSerializer.SerializeToNode(new { state = "populated", executedSuiteContentIdentity = content, executedConfigurationContentIdentity = content, requiredTestSetContentIdentity = content, counts = new { required = 1, executed = 1, completed = 1, passed = 1, failed = 0, skipped = 0 }, tests = new[] { new { testId = "test-1", outcome = "passed" } } });
        result["bindings"] = bindings.DeepClone();
        return (descriptor, result);
    }
}