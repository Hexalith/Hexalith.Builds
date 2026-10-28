// <copyright file="LocalRunSourceReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Exercises accepted source-review defects through durable observations and actual ownership boundaries.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class LocalRunSourceReviewTests
{
    /// <summary>A workspace is still reported when claim discovery fails or the workspace is a file.</summary>
    /// <param name="file">Whether the owned workspace path is a file rather than a directory.</param>
    /// <returns>The known leftover and independent claim uncertainty assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkspaceObservationSurvivesClaimFailureAsync(bool file)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(state.Workspace)!);
        if (file)
        {
            await File.WriteAllTextAsync(state.Workspace, "owned workspace obstruction", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        }

        engine.StateStore.Claims.Acquire(state.RunId, ["owned"]);
        string receipt = Path.Combine(engine.StateStore.Directory, "claims", "owners", state.RunId + ".json");
        await File.WriteAllTextAsync(receipt, "not-json", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunResources observed = await engine.ObserveOwnedResourcesAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        observed.WorkspaceIds.ShouldContain("workspace:" + state.RunId);
        observed.Unverified.ShouldBeTrue();
        observed.DiscoveryFailures.ShouldContain("workspaces-or-claims");
    }

    /// <summary>Unbound torn claims remain uncertain until their ownership becomes readable.</summary>
    /// <param name="stalePeerReceipt">Whether an unrelated receipt still names the now-unreadable claim.</param>
    /// <returns>The incomplete discovery, stable bytes and later verified ownership assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TornClaimWithoutReceiptNeverEstablishesEmptyCleanupAsync(bool stalePeerReceipt)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionResourceClaimStore claims = new(workspace.Root);
        claims.Acquire(CompositionTestFiles.RunId, ["owned"]);
        string claim = Directory.GetFiles(Path.Combine(workspace.Root, "claims"), "*.json").Single();
        byte[] original = await File.ReadAllBytesAsync(claim, TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(Path.Combine(workspace.Root, "claims", "owners", CompositionTestFiles.RunId + ".json"));
        if (stalePeerReceipt)
        {
            string peerReceipt = Path.Combine(workspace.Root, "claims", "owners", CompositionRunPlanFactory.NewRunId() + ".json");
            await File.WriteAllTextAsync(peerReceipt, JsonSerializer.Serialize(new[] { Path.GetFileNameWithoutExtension(claim) }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(claim, "{\"OwnerRunId\":", TestContext.Current.CancellationToken).ConfigureAwait(true);
        bool unverified = false;
        claims.FindOwned(CompositionTestFiles.RunId, () => unverified = true).ShouldBeEmpty();
        unverified.ShouldBeTrue();
        _ = Should.Throw<IOException>(() => claims.ReleaseOwned(CompositionTestFiles.RunId));
        File.Exists(claim).ShouldBeTrue();
        await File.WriteAllBytesAsync(claim, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        claims.FindOwned(CompositionTestFiles.RunId).ShouldNotBeEmpty();
        claims.ReleaseOwned(CompositionTestFiles.RunId);
        File.Exists(claim).ShouldBeFalse();
    }

    /// <summary>Malformed immutable bindings cannot escape recording as an unchecked JSON exception.</summary>
    /// <param name="bindings">The incomplete held snapshot.</param>
    /// <param name="tracked">Whether the active owner was already persisted.</param>
    /// <returns>The structured recording failure with a preserved first observation.</returns>
    [Theory]
    [InlineData("{}", false)]
    [InlineData("{}", true)]
    [InlineData("{\"run\":null}", false)]
    [InlineData("{\"run\":null}", true)]
    [InlineData("{\"run\":[]}", false)]
    [InlineData("{\"run\":[]}", true)]
    public async Task MissingRunBindingPreservesFirstOutcomeAsync(string bindings, bool tracked)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        if (tracked)
        {
            await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        JsonElement snapshot = JsonSerializer.Deserialize<JsonElement>(bindings);
        _ = await Should.ThrowAsync<InvalidDataException>(() => engine.RecordTestOutcomeAsync(state.RunId, [], Failed(), "retained", new([], [], true), TestContext.Current.CancellationToken, environmentBindings: snapshot)).ConfigureAwait(true);
        ModuleTestRunRecord committed = (await engine.ReadCommittedOutcomeAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.TerminalStatus.ShouldBe("failed");
        committed.FailureRetained.ShouldBeTrue();
    }

    /// <summary>Metadata copies preserve outside sentinels and create private replacement files.</summary>
    /// <param name="linked">Whether the destination is a symlink to an outside file.</param>
    /// <returns>The sentinel or private-byte publication assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataCopiesRespectPrivateOwnedDestinationsAsync(bool linked)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        string source = Path.Combine(workspace.Root, "run");
        string destination = Path.Combine(workspace.Root, "archive");
        CompositionWorkspace.CreatePrivateDirectory(source);
        CompositionWorkspace.CreatePrivateDirectory(destination);
        const string bytes = "{\"schema\":\"fixture\"}";
        await File.WriteAllTextAsync(Path.Combine(source, "plan.json"), bytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string target = Path.Combine(destination, "selected-plan.json");
        string sentinel = Path.Combine(workspace.Root, "outside.json");
        await File.WriteAllTextAsync(sentinel, "outside-sentinel", TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (linked)
        {
            _ = File.CreateSymbolicLink(target, sentinel);
        }
        else
        {
            await File.WriteAllTextAsync(target, "old", TestContext.Current.CancellationToken).ConfigureAwait(true);
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        MethodInfo copy = typeof(CompositionEngine).GetMethod("CopySelectedMetadataAsync", BindingFlags.Static | BindingFlags.NonPublic).ShouldNotBeNull();
        await ((Task)copy.Invoke(null, [source, destination, TestContext.Current.CancellationToken])!).ConfigureAwait(true);
        (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("outside-sentinel");
        if (linked)
        {
            new FileInfo(target).LinkTarget.ShouldBe(sentinel);
        }
        else
        {
            (await File.ReadAllTextAsync(target, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(bytes);
            File.GetUnixFileMode(target).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    /// <summary>A forged link cannot authorize release of local failure retention.</summary>
    /// <param name="exclusive">Whether a valid marker is exclusively held instead of linked.</param>
    /// <returns>The retained owned workspace and untouched outside request assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LinkedCleanupRequestCannotSuppressFailureRetentionAsync(bool exclusive)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        string outside = Path.Combine(workspace.Root, "outside-request.json");
        string request = JsonSerializer.Serialize(new { runId = state.RunId, attemptId = CompositionRunPlanFactory.NewRunId() });
        await File.WriteAllTextAsync(outside, request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string marker = Path.Combine(directory, "cleanup-request.json");
        if (exclusive)
        {
            await File.WriteAllTextAsync(marker, request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            _ = File.CreateSymbolicLink(marker, outside);
        }

        using FileStream? held = exclusive ? new(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        _ = await FailStartupAsync(engine, state, false).ConfigureAwait(true);
        ModuleTestRunRecord committed = (await engine.ReadCommittedOutcomeAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.FailureRetained.ShouldBeTrue();
        committed.ResourceDisposition.ShouldBe("retained");
        committed.CleanupDisposition.ShouldBe("not-requested");
        Directory.Exists(state.Workspace).ShouldBeTrue();
        (await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(request);
    }

    /// <summary>A startup timeout whose first recording contends preserves its cause on the final retry.</summary>
    /// <returns>The retry diagnostic, published timeout and retained workspace assertions.</returns>
    [Fact]
    public async Task StartupTimeoutRecordingRetryPreservesCauseAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(state.Workspace);
        await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream held = new(Path.Combine(directory, "outcome.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable heldLifetime = held.ConfigureAwait(true);
        async Task ReleaseAsync()
        {
            await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await held.DisposeAsync().ConfigureAwait(true);
        }

        Task release = ReleaseAsync();
        CompositionStartResult failed;
        try
        {
            failed = await FailStartupAsync(engine, state, true).ConfigureAwait(true);
        }
        finally
        {
            await release.ConfigureAwait(true);
        }

        failed.Result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXT008");
        JsonElement result = TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).Document.ShouldNotBeNull();
        result.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe("startup-timeout");
        Directory.Exists(state.Workspace).ShouldBeTrue();
    }

    /// <summary>Command-level recovery waits for the committed pass after a non-waiting metadata retry fails.</summary>
    /// <returns>The authoritative pass, automatic cleanup and absence of failure retention assertions.</returns>
    [Fact]
    public async Task SessionRecoveryWaitsForCommittedPassAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        using Process host = new();
        Task release = Task.CompletedTask;
        CompositionRunSession? session = null;
        int cleanups = 0;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
        };
        options = options with
        {
            SelectedPlatformStartOverride = async (observedManifest, entry, token) =>
            {
                observedManifest.ShouldBe(manifest);
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = entry }, manifest, workspace.Root).ConfigureAwait(false);
                plan = plan with { ExecutionTiming = ModuleTestExecutionTiming.Start(entry, DateTimeOffset.UtcNow.AddMinutes(-1)) };
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                session = new(plan, ready, "private-key", host);
                CompositionEngine engine = new(options);
                CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, entry.ManifestHash, entry.RequestedAtUtc) with
                {
                    SelectedTest = entry,
                    AttemptId = plan.AttemptId,
                    EnvironmentId = plan.EnvironmentId,
                    ExecutionContext = JsonSerializer.SerializeToElement(new { kind = "local" }),
                };
                await engine.StateStore.WriteAsync(issued, token).ConfigureAwait(false);
                _ = await engine.RecordTestOutcomeAsync(plan.RunId, ready.Resources, new("completed", ToolOutcome.Passed(), []), "unknown", new([], []), token).ConfigureAwait(false);
                FileStream held = new(Path.Combine(options.StateDirectory, "diagnostics", plan.RunId, "outcome.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                async Task ReleaseAsync()
                {
                    await Task.Delay(500, TestContext.Current.CancellationToken).ConfigureAwait(false);
                    await held.DisposeAsync().ConfigureAwait(false);
                }

                release = ReleaseAsync();
                return new CompositionStartResult(new("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = (owned, _) =>
            {
                cleanups++;
                Directory.Delete(owned.Plan.Workspace, true);
                return Task.FromResult(new CompositionDownResult(new("completed", ToolOutcome.Passed(), []), new([], [])));
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        int exit;
        try
        {
            exit = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        }
        finally
        {
            await release.ConfigureAwait(true);
        }

        exit.ShouldBe(0, output.ToString());
        cleanups.ShouldBe(1);
        Directory.Exists(session.ShouldNotBeNull().Plan.Workspace).ShouldBeFalse();
        ModuleTestRunRecord record = (await new CompositionEngine(options).ReadCommittedOutcomeAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        record.TerminalStatus.ShouldBe("completed");
        record.FailureRetained.ShouldBeFalse();
        using JsonDocument rendered = JsonDocument.Parse(output.ToString());
        rendered.RootElement.GetProperty("status").GetString().ShouldBe("completed");
    }

    /// <summary>Published metadata remains readable under the shared-read protocol.</summary>
    /// <returns>The durable byte round-trip used by the native interrupted-lock probe.</returns>
    [Fact]
    public async Task SharedMetadataReadPreservesPublishedBytesAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = Path.Combine(workspace.Root, "metadata-read.json");
        await CompositionDocumentStore.WriteAsync(path, new { status = "committed" }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument? document = await CompositionDocumentStore.ReadIfExistsAsync<JsonDocument>(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        document.ShouldNotBeNull().RootElement.GetProperty("status").GetString().ShouldBe("committed");
    }

    /// <summary>Unverifiable cleanup markers refuse both ordinary issuance and restoration of missing tracking.</summary>
    /// <param name="obstruction">The parent link, exclusive holder or cancelled read.</param>
    /// <param name="tracked">Whether active tracking already exists.</param>
    /// <returns>The fail-closed provisioning and unchanged tracking assertions.</returns>
    [Theory]
    [InlineData("parent-link", false)]
    [InlineData("parent-link", true)]
    [InlineData("exclusive", false)]
    [InlineData("exclusive", true)]
    [InlineData("cancelled", false)]
    [InlineData("cancelled", true)]
    public async Task UnverifiableCleanupMarkerCannotPermitIssuanceAsync(string obstruction, bool tracked)
    {
        if (obstruction == "parent-link" && OperatingSystem.IsWindows())
        {
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, selected));
        CompositionWorkspace.CreatePrivateDirectory(engine.StateStore.Directory);
        if (tracked)
        {
            await engine.StateStore.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string directory = Path.Combine(engine.StateStore.Directory, "diagnostics", state.RunId);
        CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(directory)!);
        if (obstruction == "parent-link")
        {
            string outside = Path.Combine(workspace.Root, "outside-marker");
            CompositionWorkspace.CreatePrivateDirectory(outside);
            _ = Directory.CreateSymbolicLink(directory, outside);
        }
        else
        {
            CompositionWorkspace.CreatePrivateDirectory(directory);
        }

        string marker = Path.Combine(directory, "cleanup-request.json");
        if (obstruction == "exclusive")
        {
            await File.WriteAllTextAsync(marker, JsonSerializer.Serialize(new { runId = state.RunId, attemptId = CompositionRunPlanFactory.NewRunId() }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using FileStream? held = obstruction == "exclusive" ? new(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using CancellationTokenSource read = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (obstruction == "cancelled")
        {
            await read.CancelAsync().ConfigureAwait(true);
            _ = await Should.ThrowAsync<OperationCanceledException>(() => engine.EnsureIssuedStateAsync(state, read.Token)).ConfigureAwait(true);
        }
        else
        {
            _ = await Should.ThrowAsync<IOException>(() => engine.EnsureIssuedStateAsync(state, read.Token)).ConfigureAwait(true);
        }

        File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBe(tracked);
        if (obstruction != "cancelled")
        {
            MethodInfo initialize = typeof(CompositionEngine).GetMethod("InitializeStateAsync", BindingFlags.Instance | BindingFlags.NonPublic).ShouldNotBeNull();
            _ = await Should.ThrowAsync<IOException>(() => (Task<CompositionRunState>)initialize.Invoke(engine, [state.RunId, state.ManifestHash, selected.RequestedAtUtc, new CompositionEffectiveDeadline(600, false, null, null), read.Token])!).ConfigureAwait(true);
            File.Exists(engine.StateStore.PathFor(state.RunId)).ShouldBe(tracked);
        }
    }

    /// <summary>Delayed process shutdown cannot consume metadata recovery's separate finite allowance.</summary>
    /// <returns>The committed pass and pending-publication assertions without renewed cleanup time.</returns>
    [Fact]
    public async Task DelayedStoppingOriginPreservesMetadataRecoveryAllowanceAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        string manifest = workspace.Save();
        using Process host = new();
        CompositionRunSession? session = null;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
        {
            ContinuousIntegrationOverride = false,
            WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
            SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
        };
        options = options with
        {
            SelectedPlatformStartOverride = async (observedManifest, entry, token) =>
            {
                observedManifest.ShouldBe(manifest);
                CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = entry }, manifest, workspace.Root).ConfigureAwait(false);
                plan = plan with { ExecutionTiming = ModuleTestExecutionTiming.Start(entry, DateTimeOffset.UtcNow.AddMinutes(-1)) };
                CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                session = new(plan, ready, "private-key", host)
                {
                    StoppingTimestamp = Stopwatch.GetTimestamp() - (10 * Stopwatch.Frequency),
                };
                session.FirstObservedOutcome = new(new("completed", ToolOutcome.Passed(), []), null, null)
                {
                    StoppingTimestamp = session.StoppingTimestamp,
                    MetadataRecordingTimestamp = Stopwatch.GetTimestamp(),
                };
                CompositionEngine engine = new(options);
                CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, entry.ManifestHash, entry.RequestedAtUtc) with
                {
                    SelectedTest = entry,
                    AttemptId = plan.AttemptId,
                    EnvironmentId = plan.EnvironmentId,
                    ExecutionContext = JsonSerializer.SerializeToElement(new { kind = "local" }),
                };
                await engine.StateStore.WriteAsync(issued, token).ConfigureAwait(false);
                CompositionWorkspace.CreatePrivateDirectory(Path.Combine(options.StateDirectory, "diagnostics", plan.RunId, "test-result.json"));
                _ = await Should.ThrowAsync<IOException>(() => engine.RecordTestOutcomeAsync(plan.RunId, ready.Resources, session.FirstObservedOutcome.Result, "unknown", new([], [], true), token, executionTiming: plan.ExecutionTiming, archiveMetadata: false)).ConfigureAwait(false);
                return new CompositionStartResult(Failed(), plan.RunId, session) { StoppingTimestamp = session.StoppingTimestamp };
            },
            PlatformDownOverride = (_, _) => throw new InvalidOperationException("Invalid publication must leave cleanup pending."),
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
        (await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true)).ShouldBe(0, output.ToString());
        ModuleTestRunRecord committed = (await new CompositionEngine(options).ReadCommittedOutcomeAsync(session.ShouldNotBeNull().RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.TerminalStatus.ShouldBe("completed");
        committed.FailureRetained.ShouldBeFalse();
        committed.CleanupDisposition.ShouldBe("pending");
        Stopwatch.GetElapsedTime(session.StoppingTimestamp!.Value).ShouldBeGreaterThan(TimeSpan.FromSeconds(10));
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
    }

    private static ToolCommandResult Failed() => new("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), []);

    private static Task<CompositionStartResult> FailStartupAsync(CompositionEngine engine, CompositionRunState state, bool timedOut)
    {
        MethodInfo failure = typeof(CompositionEngine).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic).Single(method => method.Name == "FailAndTearDownAsync" && method.GetParameters().Length == 13);
        ToolDiagnostic diagnostic = new("HXR021", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "Startup failed.", "readiness");
        return (Task<CompositionStartResult>)failure.Invoke(engine, [state.RunId, null, state.ManifestHash, CompositionRunStatus.Failed, new[] { diagnostic }, ToolExitCode.TopologyOrLifecycle, state.CreatedAt!.Value, new CompositionEffectiveDeadline(600, false, null, null), Array.Empty<CompositionResourceReadiness>(), null, null, null, timedOut])!;
    }
}