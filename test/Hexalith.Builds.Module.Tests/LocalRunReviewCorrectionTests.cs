// <copyright file="LocalRunReviewCorrectionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Guards the concrete persistence, bounded-input and callback regressions from implementation review.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class LocalRunReviewCorrectionTests
{
    /// <summary>Late unknown snapshots cannot erase known identities, while verified absence and completed cleanup remain monotonic.</summary>
    /// <returns>The known, verified-empty and late-completion assertions.</returns>
    [Fact]
    public async Task UnverifiedLateObservationPreservesKnownLeftoversAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunResources known = new([new string('c', 64)], [12345]) { ClaimIds = ["claim:known"], WorkspaceIds = ["workspace:" + state.RunId] };
        _ = await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", known, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestRunRecord late = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "unknown", new([], [], true), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        late.RemainingResources.ContainerIds.ShouldBe(known.ContainerIds);
        late.RemainingResources.ProcessIds.ShouldBe(known.ProcessIds);
        late.RemainingResources.ClaimIds.ShouldBe(known.ClaimIds);
        late.RemainingResources.WorkspaceIds.ShouldBe(known.WorkspaceIds);
        ModuleTestRunRecord verified = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "removed", new([], []), TestContext.Current.CancellationToken, cleanupDisposition: "complete", cleanupAttemptId: CompositionRunPlanFactory.NewRunId()).ConfigureAwait(true)).ShouldNotBeNull();
        verified.RemainingResources.IsEmpty.ShouldBeTrue();
        ModuleTestRunRecord after = (await engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", known with { Unverified = true }, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        after.CleanupDisposition.ShouldBe("complete");
        after.RemainingResources.IsEmpty.ShouldBeTrue();
        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBeFalse();
    }

    /// <summary>A FIFO without any writer is rejected before a synchronous open can outlive the metadata allowance.</summary>
    /// <returns>The bounded contract-reader refusal.</returns>
    [Fact]
    public async Task ResultReaderRejectsWriterlessFifoWithoutBlockingAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The regression uses a POSIX FIFO.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        string fifo = Path.Combine(workspace.Root, "test-result.json");
        (await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("mkfifo", [fifo], workspace.Root, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true)).ExitCode.ShouldBe(0);
        using CancellationTokenSource metadata = new(TimeSpan.FromMilliseconds(200));
        Task<TestEnvironmentContractReadResult> reading = Task.Run(() => TestEnvironmentContractReader.ReadResult(fifo, metadata.Token), TestContext.Current.CancellationToken);
        try
        {
            TestEnvironmentContractReadResult result = await reading.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Single().RuleId.ShouldBe("HXT105");
        }
        finally
        {
            _ = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("python3", ["-c", "import errno,os,sys\ntry:\n fd=os.open(sys.argv[1],os.O_WRONLY|os.O_NONBLOCK); os.write(fd,b'{}'); os.close(fd)\nexcept OSError as e:\n if e.errno != errno.ENXIO: raise", fifo], workspace.Root, null), TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Oversized receipts cannot replace a readable prior receipt or publish any requested claims.</summary>
    /// <param name="existing">Whether this owner already holds a valid claim.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedAcquisitionPreservesReadableReceipt(bool existing)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string owner = CompositionRunPlanFactory.NewRunId();
        CompositionResourceClaimStore store = new(workspace.Root);
        string receipt = Path.Combine(workspace.Root, "claims", "owners", owner + ".json");
        if (existing)
        {
            store.Acquire(owner, ["prior-exclusive"]);
        }

        byte[]? original = existing ? File.ReadAllBytes(receipt) : null;
        IReadOnlyList<string> previous = store.FindOwned(owner);
        string[] requested = [.. Enumerable.Range(0, 1000).Select(index => "exclusive-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture))];
        _ = Should.Throw<ArgumentException>(() => store.Acquire(owner, requested));
        store.FindOwned(owner).ShouldBe(previous);
        if (existing)
        {
            File.ReadAllBytes(receipt).ShouldBe(original);
        }
        else
        {
            File.Exists(receipt).ShouldBeFalse();
        }

        store.ReleaseOwned(owner);
        store.FindOwned(owner).ShouldBeEmpty();
    }

    /// <summary>Claim payloads obey the same readable bound before any claim or receipt publication.</summary>
    [Fact]
    public void OversizedClaimPayloadDoesNotPublishReceipt()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string owner = CompositionRunPlanFactory.NewRunId();
        CompositionResourceClaimStore store = new(workspace.Root);
        _ = Should.Throw<ArgumentException>(() => store.Acquire(owner, [new string('x', 64 * 1024)]));
        Directory.Exists(Path.Combine(workspace.Root, "claims")).ShouldBeFalse();
    }

    /// <summary>A claim path replaced by a directory remains an exact known blocker until absence is verified.</summary>
    /// <returns>The owned claim identity, peer preservation and successful retry assertions.</returns>
    [Fact]
    public async Task DirectoryClaimRemainsVisibleUntilVerifiedDeletionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string peer = CompositionRunPlanFactory.NewRunId();
        engine.StateStore.Claims.Acquire(state.RunId, ["owned-claim"]);
        engine.StateStore.Claims.Acquire(peer, ["peer-claim"]);
        string identity = engine.StateStore.Claims.FindOwned(state.RunId).Single();
        string path = Path.Combine(engine.StateStore.Directory, "claims", identity["claim:".Length..] + ".json");
        File.Delete(path);
        _ = Directory.CreateDirectory(path);
        CompositionDownResult incomplete = await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        incomplete.Result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        incomplete.Remaining.Unverified.ShouldBeTrue();
        incomplete.Remaining.ClaimIds.ShouldContain(identity);
        engine.StateStore.Claims.FindOwned(peer).ShouldNotBeEmpty();
        Directory.Delete(path);
        (await engine.DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        engine.StateStore.Claims.FindOwned(peer).ShouldNotBeEmpty();
    }

    /// <summary>Invalid cleanup metadata refuses only its owner while healthy listing entries remain visible.</summary>
    /// <param name="invalid">The demonstrated cleanup invariant to violate.</param>
    /// <returns>The incomplete listing and preserved healthy peer.</returns>
    [Theory]
    [InlineData("state-disposition")]
    [InlineData("record-disposition")]
    [InlineData("state-leftovers")]
    [InlineData("state-unverified")]
    [InlineData("record-leftovers")]
    [InlineData("record-unverified")]
    public async Task InvalidCleanupMetadataPreservesHealthyListingAsync(string invalid)
    {
        ArgumentNullException.ThrowIfNull(invalid);
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState healthy) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        await store.WriteAsync(healthy, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string runId = CompositionRunPlanFactory.NewRunId();
        CompositionRunResources remaining = new([], []);
        if (invalid.EndsWith("leftovers", StringComparison.Ordinal))
        {
            remaining = remaining with { ContainerIds = [new string('c', 64)] };
        }
        else if (invalid.EndsWith("unverified", StringComparison.Ordinal))
        {
            remaining = remaining with { Unverified = true };
        }

        CompositionRunState broken = CompositionRunPlanFactory.CreateInitialState(runId, Path.Combine(workspace.Root, runId), selected.ManifestHash, selected.RequestedAtUtc);
        if (invalid.StartsWith("state", StringComparison.Ordinal))
        {
            broken = broken with { CleanupDisposition = invalid == "state-disposition" ? "invented" : "complete", RemainingResources = remaining };
        }
        else
        {
            ModuleTestRunRecord terminal = new(runId, runId, selected, null, null, null, null, null, false, [], "failed", Failed().Outcome, DateTimeOffset.UtcNow, "retained", remaining) { CleanupDisposition = invalid == "record-disposition" ? "invented" : "complete" };
            broken = broken with { TestRun = terminal };
        }

        await store.WriteAsync(broken, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        listed.Environments.ShouldNotBeNull().Single().OwnerRunId.ShouldBe(healthy.RunId);
    }

    /// <summary>A linked owner directory cannot redirect diagnostics, temporary files or result replacement outside ownership.</summary>
    /// <returns>The unchanged outside sentinel and directory inventory.</returns>
    [Fact]
    public async Task LinkedResultOwnerCannotOverwriteOutsideSentinelAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The regression creates a POSIX directory link.");
        }

        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        string directory = Path.Combine(workspace.Root, "diagnostics", state.RunId);
        string outside = Path.Combine(workspace.Root, "outside");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(directory)!);
        _ = Directory.CreateDirectory(outside);
        string sentinel = Path.Combine(outside, "test-result.json");
        await File.WriteAllTextAsync(sentinel, "outside-sentinel", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Directory.CreateSymbolicLink(directory, outside);
        ModuleTestRunRecord terminal = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", Failed().Outcome, DateTimeOffset.UtcNow, "retained", new([], [], true));
        _ = await Should.ThrowAsync<IOException>(() => TestResultWriter.WriteAsync(directory, state, terminal, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("outside-sentinel");
        Directory.GetFiles(outside).ShouldBe([sentinel]);
    }

    /// <summary>The immediate callback preserves a process-start prerequisite failure before report inspection.</summary>
    /// <returns>The first and returned prerequisite classification.</returns>
    [Fact]
    public async Task MissingNativeProcessCommitsPrerequisiteClassificationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        _ = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executor", "\"vstest\"");
        workspace.Change("modules.0.tests.0.entry", "\"runner.csproj\"");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        using Process host = new();
        CompositionRunSession session = await ReadyAsync(workspace, selected, manifest, host).ConfigureAwait(true);
        NativeTestExecutionResult? first = null;
        NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(session, new(selected.Entry, selected.Executor), manifest, TestContext.Current.CancellationToken, Path.Combine(workspace.Root, "missing-dotnet"), selected, onOutcome: (observation, _) =>
        {
            first ??= observation;
            return Task.CompletedTask;
        }).ConfigureAwait(true);
        first.ShouldNotBeNull().Result.Outcome.RuleId.ShouldBe("HXR030");
        first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        result.Result.Outcome.RuleId.ShouldBe("HXR030");
    }

    /// <summary>A thrown first timeout callback retains its causal observation for the command's metadata retry.</summary>
    /// <returns>The durable timeout cause and original stopping timestamp without resource cleanup.</returns>
    [Fact]
    public async Task TimeoutCallbackFailureRetriesOriginalCauseWithoutRenewingStoppingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        _ = LocalRunLifecycleFixture.Create(workspace);
        workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "1");
        workspace.Change("modules.0.tests.0.stopTimeoutSeconds", "1");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "import time; time.sleep(60)", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        ModuleTestSelection selected = Select(manifest);
        using Process host = new();
        CompositionRunSession? session = null;
        long? stopping = null;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            ContinuousIntegrationOverride = false,
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = async (declarationPath, entry, token) =>
            {
                session = await ReadyAsync(workspace, entry, declarationPath, host).ConfigureAwait(false);
                _ = await Should.ThrowAsync<IOException>(() => NativeTestExecutor.ExecuteAsync(session, new(entry.Entry, entry.Executor), declarationPath, token, selection: entry, onOutcome: async (observation, metadata) =>
                {
                    stopping = observation.StoppingTimestamp;
                    await Task.Delay(TimeSpan.FromMilliseconds(1200), metadata).ConfigureAwait(false);
                    throw new IOException("First metadata callback failed before committing.");
                })).ConfigureAwait(false);
                return new CompositionStartResult(session.FirstObservedOutcome.ShouldNotBeNull().Result, session.RunId, session) { StoppingTimestamp = stopping };
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.ProductOrTest, output.ToString());
        session.ShouldNotBeNull().StoppingTimestamp.ShouldBe(stopping);
        Stopwatch.GetElapsedTime(stopping.ShouldNotBeNull()).ShouldBeGreaterThan(TimeSpan.FromSeconds(1));
        string resultPath = Path.Combine(options.StateDirectory, "diagnostics", session.RunId, "test-result.json");
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeTrue(output.ToString());
        result.Document!.Value.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("execution-timeout");
        result.Document.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("failure-retained");
    }

    private static ModuleTestSelection Select(string manifest) => ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();

    private static async Task<CompositionRunSession> ReadyAsync(PlatformManifestTestWorkspace workspace, ModuleTestSelection selected, string manifest, Process host)
    {
        CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected }, manifest, workspace.Root).ConfigureAwait(false);
        CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
        return new(plan, ready, "private-round-two-signing-key-value", host);
    }

    private static ToolCommandResult Failed() => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);
}
