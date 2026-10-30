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