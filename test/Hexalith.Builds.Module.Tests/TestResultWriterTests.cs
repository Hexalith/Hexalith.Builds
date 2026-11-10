// <copyright file="TestResultWriterTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Verifies durable validated result snapshots with truthful partial-startup observations.</summary>
public sealed class TestResultWriterTests
{
    /// <summary>Reader-valid snapshots cannot relabel known original startup observations.</summary>
    /// <param name="conflict">The independent known startup binding to contradict.</param>
    /// <returns>The valid-input refusal and unchanged publication assertions.</returns>
    [Theory]
    [InlineData("origin")]
    [InlineData("override")]
    [InlineData("budget")]
    [InlineData("tool")]
    [InlineData("duration")]
    public async Task ReviewConflictingKnownStartupBindingIsRejectedAsync(string conflict)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        state = state with { StartupDurationSeconds = 0.25, ExecutingTool = ExecutingToolIdentity.Capture() };
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), DateTimeOffset.UtcNow, "unknown", new([], [], true));
        string path = await TestResultWriter.WriteAsync(workspace.Root, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonNode document = JsonNode.Parse(original).ShouldNotBeNull();
        JsonObject startup = document["bindings"]!["deadlines"]!["startup"]!.AsObject();
        if (conflict == "origin")
        {
            startup["originAt"] = state.CreatedAt!.Value.AddSeconds(1).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            startup["expiresAt"] = state.StartupDeadlineUtc!.Value.AddSeconds(1).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (conflict == "tool")
        {
            document["bindings"]!["tool"]!["contentIdentity"] = "sha256:" + new string('0', 64);
        }
        else if (conflict == "duration")
        {
            startup["actualDurationSeconds"] = 0.5;
        }
        else
        {
            int seconds = conflict == "budget" ? 601 : 600;
            startup["effectiveSeconds"] = seconds;
            startup["expiresAt"] = state.CreatedAt!.Value.AddSeconds(seconds).UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            startup["override"] = JsonSerializer.SerializeToNode(new { state = "declared", requestedSeconds = seconds, justification = "conflicting declaration" });
        }

        string candidate = Path.Combine(workspace.Root, "valid-conflict.json");
        await File.WriteAllTextAsync(candidate, document.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReader.ReadResult(candidate, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        record = record with { EnvironmentBindings = JsonSerializer.SerializeToElement(document["bindings"]) };
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(workspace.Root, state, record, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(original);
    }

    /// <summary>First and refreshed partial results preserve measured startup and the original binary manifest after workspace deletion.</summary>
    /// <returns>The reader, original identity and diagnostic-byte assertions.</returns>
    [Fact]
    public async Task PartialResultsPreserveFrozenStartupAndExecutorAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        state = state with { ExecutingTool = ExecutingToolIdentity.Capture(), StartupDurationSeconds = 1.25, StartupObservedAtUtc = selected.RequestedAtUtc.AddSeconds(1.25), EffectiveDeadlineSeconds = 12, StartupDeadlineUtc = selected.RequestedAtUtc.AddSeconds(12), EffectiveDeadlineIsOverride = true, EffectiveDeadlineJustification = "controlled startup measurement" };
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", ToolOutcome.Passed().Fail(ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "HXR021", ToolExitCode.TopologyOrLifecycle), DateTimeOffset.UtcNow, "retained", new([], [], true)) { TerminalCause = "startup-timeout" };
        string directory = Path.Combine(workspace.Root, "diagnostics");
        string path = await TestResultWriter.WriteAsync(directory, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonElement first = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        JsonElement startup = first.GetProperty("bindings").GetProperty("deadlines").GetProperty("startup");
        startup.GetProperty("actualDurationSeconds").GetDouble().ShouldBe(1.25);
        startup.GetProperty("originAt").GetDateTimeOffset().ShouldBe(selected.RequestedAtUtc);
        startup.GetProperty("override").GetProperty("justification").GetString().ShouldBe("controlled startup measurement");
        ModuleTestRunRecord removed = record with { CleanupDisposition = "complete", CleanupAttemptId = CompositionRunPlanFactory.NewRunId(), RemainingResources = new([], []) };
        _ = await TestResultWriter.WriteAsync(directory, state, removed, TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReadResult refreshed = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        refreshed.IsValid.ShouldBeTrue();
        JsonElement last = refreshed.Document!.Value;
        JsonElement.DeepEquals(first.GetProperty("terminal"), last.GetProperty("terminal")).ShouldBeTrue();
        JsonElement.DeepEquals(startup, last.GetProperty("bindings").GetProperty("deadlines").GetProperty("startup")).ShouldBeTrue();
        last.GetProperty("bindings").GetProperty("tool").GetProperty("contentIdentity").GetString().ShouldBe(state.ExecutingTool.ContentIdentity);
        JsonElement manifest = last.GetProperty("diagnostics").EnumerateArray().Single(item => item.GetProperty("id").GetString() == "executing-tool");
        (await File.ReadAllBytesAsync(Path.Combine(directory, manifest.GetProperty("reference").GetString()!), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(System.Text.Encoding.UTF8.GetBytes(state.ExecutingTool.Manifest));
    }

    /// <summary>Legacy unknown executor facts stay unavailable with explicit classified diagnostics.</summary>
    /// <returns>The unavailable binding and safe explanation assertions.</returns>
    [Fact]
    public async Task LegacyExecutorRemainsUnavailableWithDiagnosticsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "cancelled", ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled), DateTimeOffset.UtcNow, "unknown", new([], [], true));
        string path = await TestResultWriter.WriteAsync(workspace.Root, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonElement document = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        document.GetProperty("bindings").GetProperty("tool").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        string reference = document.GetProperty("diagnostics")[0].GetProperty("reference").GetString()!;
        using JsonDocument detail = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(workspace.Root, reference), TestContext.Current.CancellationToken).ConfigureAwait(true));
        detail.RootElement.GetProperty("toolIdentityUnavailable").GetBoolean().ShouldBeTrue();
        detail.RootElement.GetProperty("startupDurationUnavailable").GetBoolean().ShouldBeTrue();
    }

    /// <summary>A passed owner's existing result records attached failure retention and explicit release without changing the first outcome.</summary>
    /// <param name="local">Whether the attachment can establish local retention.</param>
    /// <returns>The reader-valid retained/released snapshots, failed ids and immutable owner outcome.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AttachedFailuresPopulateExistingRetentionAndReleaseAsync(bool local)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        if (!local)
        {
            state = state with { ExecutionContext = JsonSerializer.SerializeToElement(new { kind = "ci", repository = "o/r", workflowRunId = "1", workflowAttempt = 1, jobId = "j", runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true }) };
        }

        string directory = Path.Combine(workspace.Root, "state", "diagnostics", state.RunId);
        DateTimeOffset accepted = DateTimeOffset.UtcNow;
        CompositionAttachment failed = new(CompositionRunPlanFactory.NewRunId(), state.EnvironmentId!, state.RunId, CompositionRunPlanFactory.NewRunId(), 60, accepted, 60, accepted.AddSeconds(60)) { Outcome = "failed", EndReason = "terminal-outcome", EndedAtUtc = accepted.AddSeconds(1) };
        _ = await CompositionAttachmentStore.UpdateAsync<int>(directory, state.RunId, _ => (0, new[] { failed }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord passed = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), accepted, "retained", new CompositionRunResources([], []) { WorkspaceIds = ["workspace:" + state.RunId] });
        string path = await TestResultWriter.WriteAsync(directory, state, passed, TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReadResult retained = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        retained.IsValid.ShouldBeTrue(string.Join(';', retained.Diagnostics.Select(item => item.Message)));
        retained.Document!.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(local ? "failure-retained" : "not-retained");
        retained.Document.Value.GetProperty("retention").GetProperty("failureRunIds").EnumerateArray().Select(item => item.GetString()).ShouldBe(local ? [failed.AttachedRunId] : []);
        ModuleTestRunRecord removed = passed with { ResourceDisposition = "removed", CleanupDisposition = "complete", CleanupAttemptId = CompositionRunPlanFactory.NewRunId(), RemainingResources = new CompositionRunResources([], []) };
        _ = await TestResultWriter.WriteAsync(directory, state, removed, TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReadResult released = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        released.IsValid.ShouldBeTrue(string.Join(';', released.Diagnostics.Select(item => item.Message)));
        released.Document!.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(local ? "released" : "not-retained");
        released.Document.Value.GetProperty("retention").GetProperty("failureRunIds").EnumerateArray().Select(item => item.GetString()).ShouldBe(local ? [failed.AttachedRunId] : []);
        released.Document.Value.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("passed");
        released.Document.Value.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBe(passed.TerminalAtUtc);
        passed.FailureRetained.ShouldBeFalse();
    }

    /// <summary>A non-string recorded execution kind is not local and fails only with the writer's documented contract refusal.</summary>
    /// <returns>The structured refusal and unpublished result assertions.</returns>
    [Fact]
    public async Task NonStringExecutionKindRefusesWithoutEscapingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        state = state with { ExecutionContext = JsonSerializer.SerializeToElement(new { kind = 1 }) };
        string directory = Path.Combine(workspace.Root, "state", "diagnostics", state.RunId);
        DateTimeOffset accepted = DateTimeOffset.UtcNow;
        CompositionAttachment failed = new(CompositionRunPlanFactory.NewRunId(), state.EnvironmentId!, state.RunId, CompositionRunPlanFactory.NewRunId(), 60, accepted, 60, accepted.AddSeconds(60)) { Outcome = "failed", EndReason = "terminal-outcome", EndedAtUtc = accepted.AddSeconds(1) };
        _ = await CompositionAttachmentStore.UpdateAsync<int>(directory, state.RunId, _ => (0, new[] { failed }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord passed = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), accepted, "retained", new CompositionRunResources([], []) { WorkspaceIds = ["workspace:" + state.RunId] });
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(directory, state, passed, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        File.Exists(Path.Combine(directory, "test-result.json")).ShouldBeFalse();
    }

    /// <summary>Malformed deadlines refuse durably without escaping JSON exceptions or replacing a prior result.</summary>
    /// <param name="field">The deadline object or population field to corrupt.</param>
    /// <param name="replacement">The replacement JSON, or null to omit the field.</param>
    /// <returns>The structured refusal and unchanged prior publication assertions.</returns>
    [Theory]
    [InlineData("deadlines", null)]
    [InlineData("deadlines", "null")]
    [InlineData("deadlines", "[]")]
    [InlineData("deadlines", "1")]
    [InlineData("execution", null)]
    [InlineData("execution", "null")]
    [InlineData("execution", "[]")]
    [InlineData("state", null)]
    [InlineData("state", "null")]
    [InlineData("state", "1")]
    public async Task MalformedDeadlineBindingsRefuseBeforeUpdatingDurationAsync(string field, string? replacement)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        ModuleTestRunRecord first = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new([], [], true));
        string directory = Path.Combine(workspace.Root, "diagnostics");
        string path = await TestResultWriter.WriteAsync(directory, state, first, TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] published = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonNode document = JsonNode.Parse(published).ShouldNotBeNull();
        JsonNode bindings = document["bindings"].ShouldNotBeNull();
        JsonObject parent = field switch
        {
            "deadlines" => bindings.AsObject(),
            "execution" => bindings["deadlines"]!.AsObject(),
            _ => bindings["deadlines"]!["execution"]!.AsObject(),
        };
        if (replacement is null)
        {
            parent.Remove(field).ShouldBeTrue();
        }
        else
        {
            parent[field] = JsonNode.Parse(replacement);
        }

        ModuleTestRunRecord malformed = first with
        {
            EnvironmentBindings = JsonSerializer.SerializeToElement(bindings),
            ExecutionTiming = ModuleTestExecutionTiming.Start(selected, selected.RequestedAtUtc) with { ActualDurationSeconds = 0.5 },
        };
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(directory, state, malformed, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(published);
        TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Every first lifecycle cause produces a reader-valid result and immutable classified diagnostics.</summary>
    /// <param name="outcome">The observed outcome.</param>
    /// <param name="cause">The observed cause.</param>
    /// <returns>The contract and retained-byte checks.</returns>
    [Theory]
    [InlineData("passed", "tests-completed")]
    [InlineData("failed", "test-failure")]
    [InlineData("failed", "startup-failure")]
    [InlineData("failed", "startup-timeout")]
    [InlineData("failed", "execution-timeout")]
    [InlineData("cancelled", "run-cancelled")]
    public async Task PartialStartupResultsRemainReadableAfterWorkspaceDeletionAsync(string outcome, string cause)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        ToolExitCode failure = outcome == "cancelled" ? ToolExitCode.Cancelled : ToolExitCode.ProductOrTest;
        ToolOutcome observed = outcome == "passed" ? ToolOutcome.Passed() : ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", failure);
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], outcome, observed, DateTimeOffset.UtcNow, "retained", new CompositionRunResources([], [], true)) { TerminalCause = cause, FailureRetained = outcome == "failed" };
        string directory = Path.Combine(workspace.Root, "state", "diagnostics", state.RunId);
        string path = await TestResultWriter.WriteAsync(directory, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Directory.Delete(state.Workspace, true);
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(string.Join(",", result.Diagnostics.Select(diagnostic => diagnostic.RuleId + diagnostic.Field)));
        JsonElement document = result.Document!.Value;
        document.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe(outcome);
        document.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe(cause);
        document.GetProperty("bindings").GetProperty("candidate").GetProperty("classification").GetString().ShouldBe("baseline-only");
        document.GetProperty("bindings").GetProperty("environment").GetProperty("resources").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        document.GetProperty("bindings").GetProperty("environment").GetProperty("catalog").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        document.GetProperty("bindings").GetProperty("deadlines").GetProperty("execution").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        foreach (JsonElement reference in document.GetProperty("diagnostics").EnumerateArray())
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(directory, reference.GetProperty("reference").GetString()!), TestContext.Current.CancellationToken).ConfigureAwait(true);
            reference.GetProperty("contentIdentity").GetString().ShouldBe("sha256:" + Convert.ToHexStringLower(SHA256.HashData(bytes)));
            reference.GetProperty("redacted").GetBoolean().ShouldBeTrue();
        }
    }

    /// <summary>A rejected later update cannot change the bytes authenticated by the previously published result.</summary>
    /// <returns>The prior-result and immutable-reference assertions.</returns>
    [Fact]
    public async Task RejectedUpdatePreservesPublishedDiagnosticBytesAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        ModuleTestRunRecord first = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new CompositionRunResources([], [], true));
        string directory = Path.Combine(workspace.Root, "diagnostics");
        string path = await TestResultWriter.WriteAsync(directory, state, first, TestContext.Current.CancellationToken).ConfigureAwait(true);
        byte[] published = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(published);
        JsonElement reference = document.RootElement.GetProperty("diagnostics")[0];
        string diagnosticPath = Path.Combine(directory, reference.GetProperty("reference").GetString()!);
        byte[] diagnostic = await File.ReadAllBytesAsync(diagnosticPath, TestContext.Current.CancellationToken).ConfigureAwait(true);

        // The changed diagnostic is written, then final validation refuses its inconsistent cause.
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(directory, state, first with { TerminalCause = "run-cancelled" }, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(published);
        (await File.ReadAllBytesAsync(diagnosticPath, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(diagnostic);
        reference.GetProperty("contentIdentity").GetString().ShouldBe("sha256:" + Convert.ToHexStringLower(SHA256.HashData(diagnostic)));
        TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Classified diagnostics preserve bounded discovery categories while refusing supplied secret text.</summary>
    /// <returns>The safe diagnostic category assertions.</returns>
    [Fact]
    public async Task DiscoveryCategoriesAreRetainedWithoutSecretTextAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), DateTimeOffset.UtcNow, "retained", new CompositionRunResources([], [], true) { DiscoveryFailures = ["containers", "claims", "credential=fixture-secret", new string('a', 10000)] });
        string directory = Path.Combine(workspace.Root, "diagnostics");
        string path = await TestResultWriter.WriteAsync(directory, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument result = JsonDocument.Parse(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
        string diagnostic = await File.ReadAllTextAsync(Path.Combine(directory, result.RootElement.GetProperty("diagnostics")[0].GetProperty("reference").GetString()!), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(diagnostic);
        document.RootElement.GetProperty("discoveryFailures").EnumerateArray().Select(value => value.GetString()).ShouldBe(["claims", "containers"]);
        diagnostic.ShouldNotContain("fixture-secret");
        diagnostic.Length.ShouldBeLessThan(1024);
    }

    /// <summary>Atomic report snapshots survive interrupted publication and a later rejected result update.</summary>
    /// <returns>The old references and legacy path assertions.</returns>
    [Fact]
    public async Task ImmutableReportsSurviveInterruptedAndRejectedUpdatesAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        byte[] originalReport = "original-report"u8.ToArray();
        byte[] originalObservations = "{}"u8.ToArray();
        NativeTestExecutionResult original = new(new ToolCommandResult("completed", ToolOutcome.Passed(), []), null, originalReport) { ObservationBytes = originalObservations };
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(state.RunId, [], original.Result, "unknown", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken, original).ConfigureAwait(true)).ShouldNotBeNull();
        string path = Path.Combine(directory, "test-result.json");
        byte[] published = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument document = JsonDocument.Parse(published);
        Dictionary<string, byte[]> referenced = [];
        foreach (JsonElement reference in document.RootElement.GetProperty("diagnostics").EnumerateArray())
        {
            string artifact = Path.Combine(directory, reference.GetProperty("reference").GetString()!);
            referenced.Add(artifact, await File.ReadAllBytesAsync(artifact, TestContext.Current.CancellationToken).ConfigureAwait(true));
        }

        using CancellationTokenSource interrupted = new();
        await interrupted.CancelAsync().ConfigureAwait(true);
        _ = await Should.ThrowAsync<OperationCanceledException>(() => CompositionDocumentStore.WriteBytesAsync(Path.Combine(directory, "native.trx"), "interrupted"u8.ToArray(), interrupted.Token)).ConfigureAwait(true);
        (await File.ReadAllBytesAsync(Path.Combine(directory, "native.trx"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(originalReport);
        NativeTestExecutionResult next = original with { ReportBytes = "new-report"u8.ToArray(), ObservationBytes = "{\"new\":true}"u8.ToArray() };
        ModuleTestRunRecord later = (await engine.RecordTestOutcomeAsync(state.RunId, [], next.Result, "unknown", new CompositionRunResources([], [], true), TestContext.Current.CancellationToken, next).ConfigureAwait(true)).ShouldNotBeNull();
        later.ReportHash.ShouldNotBe(first.ReportHash);
        byte[] accepted = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(directory, state, later with { TerminalCause = "run-cancelled" }, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(accepted);
        foreach ((string artifact, byte[] bytes) in referenced)
        {
            (await File.ReadAllBytesAsync(artifact, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(bytes);
        }

        (await File.ReadAllBytesAsync(Path.Combine(directory, "native.trx"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(next.ReportBytes);
        (await File.ReadAllBytesAsync(Path.Combine(directory, "functional-observations.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(next.ObservationBytes);
    }

    /// <summary>Results refuse identity changes and preserve the previously published valid result.</summary>
    /// <returns>The publication refusal check.</returns>
    [Fact]
    public async Task MismatchedOriginalIdentityDoesNotPublishAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "passed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new CompositionRunResources([], [], true));
        _ = await Should.ThrowAsync<InvalidDataException>(() => TestResultWriter.WriteAsync(workspace.Root, state with { AttemptId = null }, record, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        File.Exists(Path.Combine(workspace.Root, "test-result.json")).ShouldBeFalse();
    }

    /// <summary>Accepted holds, an ended hold and a passively expired hold are published in the valid owner result.</summary>
    /// <returns>The populated holds assertions.</returns>
    [Fact]
    public async Task ResultPublishesAcceptedHoldsWithActualEndsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        string directory = Path.Combine(workspace.Root, "diagnostics");
        DateTimeOffset accepted = DateTimeOffset.UtcNow.AddHours(-1);
        CompositionAttachment Hold(int? requested, int effective, DateTimeOffset? ended = null) => new(CompositionRunPlanFactory.NewRunId(), state.EnvironmentId!, state.RunId, CompositionRunPlanFactory.NewRunId(), requested, accepted, effective, accepted.AddSeconds(effective))
        {
            EndedAtUtc = ended,
            EndReason = ended is null ? null : "terminal-outcome",
        };

        CompositionAttachment[] holds = [Hold(null, 1800, accepted.AddMinutes(2)), Hold(60, 60), Hold(14400, 14400)];
        _ = await CompositionAttachmentStore.UpdateAsync<int>(directory, state.RunId, current => (0, [.. current, .. holds]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new([], [], true));
        string path = await TestResultWriter.WriteAsync(directory, state, record, TestContext.Current.CancellationToken).ConfigureAwait(true);
        TestEnvironmentContractReadResult read = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        read.IsValid.ShouldBeTrue(string.Join(';', read.Diagnostics.Select(item => item.Message)));
        JsonElement[] published = [.. read.Document!.Value.GetProperty("bindings").GetProperty("attachments").GetProperty("holds").EnumerateArray()];
        published.Length.ShouldBe(3);
        published[0].TryGetProperty("requestedHoldSeconds", out _).ShouldBeFalse();
        published[0].GetProperty("end").GetProperty("reason").GetString().ShouldBe("terminal-outcome");
        published[1].GetProperty("end").GetProperty("reason").GetString().ShouldBe("expired");
        published[2].GetProperty("end").GetProperty("state").GetString().ShouldBe("active");
        published[2].GetProperty("effectiveHoldSeconds").GetInt32().ShouldBe(14400);
    }
}
