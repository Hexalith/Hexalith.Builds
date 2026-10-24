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
            output.ToString().ShouldContain("unavailable");
            if (format == "json")
            {
                using JsonDocument json = JsonDocument.Parse(output.ToString());
                json.RootElement.GetProperty("environments").GetArrayLength().ShouldBe(2);
            }
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
        result.Environments.ShouldBeNull();
        result.Diagnostics.Single().Message.ShouldNotContain("do-not-echo");
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
        result.Environments.ShouldBeNull();
        result.Diagnostics.Single().Message.ShouldContain("listing is incomplete");
    }
}
