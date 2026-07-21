// <copyright file="ModuleInvocationStateStoreTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies runner-owned invocation state creation and idempotent teardown.
/// </summary>
public sealed class ModuleInvocationStateStoreTests
{
    /// <summary>
    /// Verifies a created invocation state is discoverable and idempotently removed by <c>down</c>,
    /// while a state file belonging to a different manifest is left untouched.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownAsyncRemovesOnlyMatchingManifestStateAndIsIdempotentAsync()
    {
        string manifestPath = CreateManifestFile("state-store-a");
        string otherManifestPath = CreateManifestFile("state-store-b");
        try
        {
            ModuleManifest manifest = CreateManifest();
            ModuleRuntimePlan plan = ModuleRuntimePlan.Create(manifest, "full");
            ModuleInvocationState created = await ModuleInvocationStateStore.CreateAsync(
                ModuleInvocationCommand.Run,
                manifestPath,
                "full",
                null,
                plan,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            ModuleRuntimePlan otherPlan = ModuleRuntimePlan.Create(manifest, "full");
            ModuleInvocationState otherState = await ModuleInvocationStateStore.CreateAsync(
                ModuleInvocationCommand.Run,
                otherManifestPath,
                "full",
                null,
                otherPlan,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            string stateDirectory = Path.Combine(Path.GetTempPath(), "hexalith-builds", "runs");
            string createdStatePath = Path.Combine(stateDirectory, $"{created.RunId}.json");
            string otherStatePath = Path.Combine(stateDirectory, $"{otherState.RunId}.json");
            File.Exists(createdStatePath).ShouldBeTrue();
            File.Exists(otherStatePath).ShouldBeTrue();

            await ModuleInvocationStateStore.DownAsync(manifestPath, TestContext.Current.CancellationToken).ConfigureAwait(true);

            File.Exists(createdStatePath).ShouldBeFalse();
            File.Exists(otherStatePath).ShouldBeTrue();

            // A second down for the same manifest is a true no-op: nothing left to remove, no exception.
            await ModuleInvocationStateStore.DownAsync(manifestPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            File.Exists(otherStatePath).ShouldBeTrue();

            await ModuleInvocationStateStore.DownAsync(otherManifestPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            File.Exists(otherStatePath).ShouldBeFalse();
        }
        finally
        {
            File.Delete(manifestPath);
            File.Delete(otherManifestPath);
        }
    }

    /// <summary>
    /// Verifies a foreign, non-JSON file in the shared state directory does not abort cleanup for
    /// the caller's own state.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownAsyncToleratesUnrelatedFileInStateDirectoryAsync()
    {
        string manifestPath = CreateManifestFile("state-store-c");
        try
        {
            ModuleManifest manifest = CreateManifest();
            ModuleRuntimePlan plan = ModuleRuntimePlan.Create(manifest, "full");
            ModuleInvocationState created = await ModuleInvocationStateStore.CreateAsync(
                ModuleInvocationCommand.Run,
                manifestPath,
                "full",
                null,
                plan,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            string stateDirectory = Path.Combine(Path.GetTempPath(), "hexalith-builds", "runs");
            string foreignPath = Path.Combine(stateDirectory, $"{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(
                foreignPath,
                "not valid json",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            try
            {
                await ModuleInvocationStateStore.DownAsync(manifestPath, TestContext.Current.CancellationToken).ConfigureAwait(true);

                string createdStatePath = Path.Combine(stateDirectory, $"{created.RunId}.json");
                File.Exists(createdStatePath).ShouldBeFalse();
                File.Exists(foreignPath).ShouldBeTrue();
            }
            finally
            {
                File.Delete(foreignPath);
            }
        }
        finally
        {
            File.Delete(manifestPath);
        }
    }

    /// <summary>
    /// Verifies the manifest re-read <c>down</c> performs to compute its lookup hash is unguarded:
    /// if the manifest disappears before that read (the documented TOCTOU gap), <c>down</c> throws
    /// rather than completing idempotently. This is the exact failure class the CLI's
    /// runner-owned-lifecycle exit code maps to a stable diagnostic.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownAsyncThrowsWhenManifestIsUnreadableAndStateDirectoryExistsAsync()
    {
        string manifestPath = CreateManifestFile("state-store-d");
        ModuleManifest manifest = CreateManifest();
        ModuleRuntimePlan plan = ModuleRuntimePlan.Create(manifest, "full");

        // Ensure the shared state directory exists so DownAsync does not take its early-return path.
        _ = await ModuleInvocationStateStore.CreateAsync(
            ModuleInvocationCommand.Run,
            manifestPath,
            "full",
            null,
            plan,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(manifestPath);

        _ = await Should.ThrowAsync<IOException>(() =>
            ModuleInvocationStateStore.DownAsync(manifestPath, TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private static string CreateManifestFile(string content)
    {
        string path = Path.Combine(Path.GetTempPath(), $"hexalith-builds-state-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        return path;
    }

    private static ModuleManifest CreateManifest() => new(
        "hexalith.module-manifest.v1",
        "state-store-fixture",
        [new ModuleDescriptor("module-a", "assemblies/module-a.dll", [], "module-a", "module-a", "module-a")],
        new PlatformPins("3.70.0", "1.18.0", "1.18.4", "4.0.1"),
        null,
        new Dictionary<string, ModuleProfile>(StringComparer.Ordinal));
}
