// <copyright file="CompositionRunStateStoreTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies metadata-only run state keyed by run identity in a per-user directory.
/// </summary>
public sealed class CompositionRunStateStoreTests
{
    /// <summary>
    /// Verifies state round-trips by run identity and is removed idempotently.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task StateRoundTripsByRunIdAndDeletesIdempotentlyAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunStateStore store = new(Path.Combine(root, "state"));
            CompositionRunState state = new(
                CompositionRunState.SupportedSchema,
                CompositionTestFiles.RunId,
                CompositionRunStatus.Cancelled,
                "ABCDEF",
                Path.Combine(root, "workspace"),
                null,
                null,
                "HXC130",
                DateTimeOffset.UnixEpoch);

            await store.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);

            store.PathFor(CompositionTestFiles.RunId).ShouldBe(Path.Combine(root, "state", CompositionTestFiles.RunId + ".json"));
            (await store.TryReadAsync(CompositionTestFiles.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(state);
            (await store.TryReadAsync("fedcba9876543210fedcba9876543210", TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
            store.Delete(CompositionTestFiles.RunId).ShouldBeTrue();
            store.Delete(CompositionTestFiles.RunId).ShouldBeTrue();
            File.Exists(store.PathFor(CompositionTestFiles.RunId)).ShouldBeFalse();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(store.Directory).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies malformed run identities cannot address files outside the state directory.
    /// </summary>
    [Fact]
    public void MalformedRunIdsAreRejected()
    {
        CompositionRunStateStore store = new(Path.Combine(Path.GetTempPath(), "hexalith-g4-unit-state"));

        _ = Should.Throw<ArgumentException>(() => store.PathFor("../escape"));
        _ = Should.Throw<ArgumentException>(() => store.PathFor(string.Empty));
        _ = Should.Throw<ArgumentException>(() => new CompositionRunStateStore("relative/state"));
    }

    /// <summary>
    /// Verifies the default state and workspace roots are per-user.
    /// </summary>
    [Fact]
    public void DefaultDirectoriesArePerUser()
    {
        string userRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);

        CompositionRunStateStore.DefaultDirectory().ShouldStartWith(userRoot);
        CompositionWorkspace.DefaultRoot().ShouldStartWith(userRoot);
        CompositionRunStateStore.DefaultDirectory().ShouldNotStartWith(Path.GetTempPath());
    }
}