// <copyright file="TestResultWriterTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Verifies durable validated result snapshots with truthful partial-startup observations.</summary>
public sealed class TestResultWriterTests
{
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
}
