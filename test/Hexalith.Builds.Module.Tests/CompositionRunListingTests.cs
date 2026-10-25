// <copyright file="CompositionRunListingTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Verifies read-only durable listings independent of manifest changes and last-update timestamps.</summary>
public sealed class CompositionRunListingTests
{
    /// <summary>Listing uses the original request time and exposes unavailable attachment and legacy metadata.</summary>
    /// <returns>The human and JSON listing assertions.</returns>
    [Fact]
    public async Task ListingPreservesStableAgeAfterManifestDeletionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionRunStateStore store = new(options.StateDirectory);
        DateTimeOffset original = DateTimeOffset.UtcNow.AddMinutes(-5);
        state = state with { CreatedAt = original, UpdatedAt = DateTimeOffset.UtcNow, CleanupDisposition = "incomplete", RemainingResources = new CompositionRunResources([], [], true) { WorkspaceIds = ["workspace:" + state.RunId] } };
        await store.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string legacyRun = CompositionRunPlanFactory.NewRunId();
        await store.WriteAsync(new CompositionRunState(CompositionRunState.SupportedSchema, legacyRun, CompositionRunStatus.Failed, "legacy-hash", Path.Combine(options.WorkspaceRoot, legacyRun), null, null, null, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(Path.Combine(workspace.Root, "manifest.json"));
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, original.AddSeconds(301), TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        CompositionRunListingEntry actual = listed.Environments!.Single(entry => entry.OwnerRunId == state.RunId);
        actual.AgeSeconds.ShouldBe(301);
        actual.EnvironmentId.ShouldBe(state.EnvironmentId);
        actual.AttemptId.ShouldBe(state.AttemptId);
        actual.Cleanup.ShouldBe("incomplete");
        actual.AttachedRunIds.ShouldBeEmpty();
        actual.AttachmentInformation.ShouldContain("unsupported");
        CompositionRunListingEntry legacy = listed.Environments!.Single(entry => entry.OwnerRunId == legacyRun);
        legacy.CreatedAt.ShouldBeNull();
        legacy.AgeSeconds.ShouldBeNull();
        legacy.EnvironmentId.ShouldBeNull();
        foreach (string format in new[] { "human", "json" })
        {
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            int exit = await ModuleCommandApplication.InvokeAsync(["list", "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
            exit.ShouldBe(0, output.ToString());
            output.ToString().ShouldContain(state.RunId);
            output.ToString().ShouldContain(state.EnvironmentId!);
            output.ToString().ShouldContain(state.AttemptId!);
            output.ToString().ShouldContain("incomplete");
            output.ToString().ShouldContain("workspace:" + state.RunId);
            output.ToString().ShouldContain("unverified");
            output.ToString().ShouldContain("unavailable");
            if (format == "json")
            {
                using JsonDocument json = JsonDocument.Parse(output.ToString());
                json.RootElement.GetProperty("environments").GetArrayLength().ShouldBe(2);
            }
        }
    }

    /// <summary>A missing state root is empty; an existing file at that root is an incomplete listing.</summary>
    /// <param name="existingFile">Whether the configured root is an existing file.</param>
    /// <returns>The confirmed-absence or structured storage-refusal assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateRootAbsenceAndNonDirectoryAreDistinguishedAsync(bool existingFile)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = Path.Combine(workspace.Root, "state");
        if (existingFile)
        {
            await File.WriteAllTextAsync(path, "state root is a file", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        ToolCommandResult listed = await CompositionRunListing.ReadAsync(new CompositionRunStateStore(path), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (existingFile)
        {
            listed.Status.ShouldBe("failed");
            listed.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            listed.Diagnostics.Single().RuleId.ShouldBe("HXR024");
            (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("state root is a file");
        }
        else
        {
            listed.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            listed.Environments.ShouldBeEmpty();
        }
    }

    /// <summary>Corrupt tracked metadata refuses instead of silently reporting an empty list.</summary>
    /// <returns>The incomplete discovery refusal.</returns>
    [Fact]
    public async Task UnreadableStateNeverClaimsEmptySuccessAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        CompositionWorkspace.CreatePrivateDirectory(store.Directory);
        await File.WriteAllTextAsync(store.PathFor(state.RunId), "{invalid credential=do-not-echo}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult result = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        result.Environments.ShouldBeEmpty();
        result.Diagnostics.Single().Message.ShouldNotContain("do-not-echo");
        result.Diagnostics.Single().Message.ShouldContain(state.RunId);
    }

    /// <summary>One invalid owner does not hide verified peers or permit duplicate issued identities.</summary>
    /// <param name="duplicate">Whether valid JSON reuses the environment identity for the attempt.</param>
    /// <returns>The incomplete listing and preserved valid peer assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidEntryDoesNotHideVerifiedPeerAsync(bool duplicate)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState valid) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        await store.WriteAsync(valid, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string corrupt = CompositionRunPlanFactory.NewRunId();
        if (duplicate)
        {
            await store.WriteAsync(valid with { RunId = corrupt, AttemptId = valid.EnvironmentId }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            await File.WriteAllTextAsync(store.PathFor(corrupt), "{invalid}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        ToolCommandResult result = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        result.Environments.ShouldNotBeNull().Single().OwnerRunId.ShouldBe(valid.RunId);
        result.Diagnostics.Single().Message.ShouldContain("listing is incomplete");
        result.Diagnostics.Single().Message.ShouldContain(corrupt);
    }

    /// <summary>Well-formed JSON with a null required nested lifecycle value returns the structured incomplete refusal.</summary>
    /// <returns>The invalid nested-state refusal assertions.</returns>
    [Fact]
    public async Task NullTerminalOutcomeReturnsIncompleteListingAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        ModuleTestRunRecord record = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], "failed", null!, DateTimeOffset.UtcNow, "retained", new CompositionRunResources([], []));
        await store.WriteAsync(state with { TestRun = record }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult result = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        result.Environments.ShouldBeEmpty();
        result.Diagnostics.Single().Message.ShouldContain("listing is incomplete");
    }

    /// <summary>Unverifiable committed outcomes and activation metadata identify the exact owner while preserving peers.</summary>
    /// <param name="observation">The invalid durable observation.</param>
    /// <returns>The actionable owner diagnostic and healthy peer assertions.</returns>
    [Theory]
    [InlineData("outcome")]
    [InlineData("activation")]
    public async Task UnverifiableObservationNamesOwnerAsync(string observation)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        await store.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string peer = CompositionRunPlanFactory.NewRunId();
        await store.WriteAsync(state with { RunId = peer }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", state.RunId);
        if (observation == "outcome")
        {
            ModuleTestRunRecord invalid = new(state.RunId, peer, selected, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), DateTimeOffset.UtcNow, "unknown", new CompositionRunResources([], []));
            await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "selected-test.json"), invalid, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            ModuleTestExecutionTiming invalid = ModuleTestExecutionTiming.Start(selected, DateTimeOffset.UtcNow) with { EffectiveSeconds = 0 };
            await CompositionDocumentStore.WriteAsync(Path.Combine(directory, "execution-activation.json"), invalid, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Environments.ShouldNotBeNull().Single().OwnerRunId.ShouldBe(peer);
        listed.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        listed.Diagnostics.Single().Message.ShouldContain(state.RunId);
    }

    /// <summary>Nested terminal identities cannot borrow another owner while healthy peers stay visible.</summary>
    /// <param name="field">The nested owner identity to corrupt.</param>
    /// <returns>The incomplete listing with the healthy peer.</returns>
    [Theory]
    [InlineData("run")]
    [InlineData("owner")]
    public async Task ForeignNestedTerminalIdentityPreservesHealthyPeerAsync(string field)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState healthy) = LocalRunLifecycleFixture.Create(workspace);
        CompositionRunStateStore store = new(Path.Combine(workspace.Root, "state"));
        await store.WriteAsync(healthy, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string invalidId = CompositionRunPlanFactory.NewRunId();
        CompositionRunState invalid = CompositionRunPlanFactory.CreateInitialState(invalidId, Path.Combine(workspace.Root, invalidId), selected.ManifestHash, selected.RequestedAtUtc);
        ModuleTestRunRecord terminal = new(field == "run" ? healthy.RunId : invalidId, field == "owner" ? healthy.RunId : invalidId, selected, null, null, null, null, null, false, [], "failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest), DateTimeOffset.UtcNow, "retained", new CompositionRunResources([], []));
        await store.WriteAsync(invalid with { TestRun = terminal }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        listed.Environments.ShouldNotBeNull().Single().OwnerRunId.ShouldBe(healthy.RunId);
    }

    /// <summary>Persisted terminal disposition is rendered consistently in both actual CLI formats.</summary>
    /// <param name="retention">The expected retention classification.</param>
    /// <returns>The human and parsed JSON retention assertions.</returns>
    [Theory]
    [InlineData("failure-retained")]
    [InlineData("released")]
    [InlineData("not-retained")]
    [InlineData("unavailable")]
    public async Task CliListingClassifiesPersistedRetentionAsync(string retention)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        bool failure = retention is "failure-retained" or "released";
        string disposition = retention == "failure-retained" ? "retained" : "removed";
        string cleanup = retention == "released" ? "complete" : "not-requested";
        ToolOutcome outcome = failure ? ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT007", ToolExitCode.ProductOrTest) : ToolOutcome.Passed();
        ModuleTestRunRecord terminal = new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], failure ? "failed" : "completed", outcome, DateTimeOffset.UtcNow, disposition, new CompositionRunResources([], [])) { FailureRetained = failure, CleanupDisposition = cleanup };
        state = state with { TestRun = terminal, CreatedAt = retention == "unavailable" ? null : state.CreatedAt, CleanupDisposition = cleanup };
        await new CompositionRunStateStore(options.StateDirectory).WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        foreach (string format in new[] { "human", "json" })
        {
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            int exit = await ModuleCommandApplication.InvokeAsync(["list", "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
            exit.ShouldBe(0, output.ToString());
            if (format == "json")
            {
                using JsonDocument document = JsonDocument.Parse(output.ToString());
                document.RootElement.GetProperty("environments")[0].GetProperty("retention").GetString().ShouldBe(retention);
            }
            else
            {
                output.ToString().ShouldContain("retention=" + retention);
            }
        }
    }

    /// <summary>Both CLI formats show activation's exact deadline, independent of selection and startup expiry.</summary>
    /// <param name="activated">Whether the runner has observed actual execution activation.</param>
    /// <returns>The exact deadline or explicit unavailable rendering assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CliListingShowsIndependentExecutionActivationAsync(bool activated)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionRunStateStore store = new(options.StateDirectory);
        await store.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ModuleTestExecutionTiming timing = ModuleTestExecutionTiming.Start(selected, selected.RequestedAtUtc.AddMinutes(2));
        timing.DeadlineUtc.ShouldNotBe(selected.ExecutionDeadlineUtc);
        timing.DeadlineUtc.ShouldNotBe(state.StartupDeadlineUtc.ShouldNotBeNull());
        if (activated)
        {
            await CompositionDocumentStore.WriteAsync(Path.Combine(store.Directory, "diagnostics", state.RunId, "execution-activation.json"), timing, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        foreach (string format in new[] { "human", "json" })
        {
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
            (await ModuleCommandApplication.InvokeAsync(["list", "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true)).ShouldBe(0);
            if (format == "json")
            {
                using JsonDocument document = JsonDocument.Parse(output.ToString());
                JsonElement deadline = document.RootElement.GetProperty("environments")[0].GetProperty("executionDeadlineUtc");
                if (activated)
                {
                    deadline.GetDateTimeOffset().ShouldBe(timing.DeadlineUtc);
                }
                else
                {
                    deadline.ValueKind.ShouldBe(JsonValueKind.Null);
                }
            }
            else
            {
                output.ToString().ShouldContain("execution-deadline=" + (activated ? timing.DeadlineUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) : "unavailable"));
                output.ToString().ShouldNotContain("execution-deadline=" + selected.ExecutionDeadlineUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
