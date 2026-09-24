// <copyright file="LiveCancellationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Proves cancellation (g): cancelling mid-startup leaves no run process or container and retains metadata-only state.
/// </summary>
[Collection(LiveLane.Name)]
public sealed class LiveCancellationTests
{
    /// <summary>
    /// (g) Cancelling while the run is starting runs bounded teardown.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task CancellingMidStartupLeavesNoProcessOrContainerAsync()
    {
        LiveGate.SkipUnlessEnabled();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromMinutes(10));
        LiveEngineScope scope = new("cancellation");
        await using (scope.ConfigureAwait(true))
        {
            using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(bound.Token);
            Task<CompositionStartResult> start = scope.StartAsync(startup.Token);
            string runId = await WaitForStartingRunAsync(scope, start, bound.Token).ConfigureAwait(true);
            scope.Track(runId);

            await startup.CancelAsync().ConfigureAwait(true);
            CompositionStartResult result = await start.ConfigureAwait(true);

            result.Session.ShouldBeNull();
            result.RunId.ShouldBe(runId);
            result.Result.Status.ShouldBe("cancelled");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Cancelled);
            result.Result.Outcome.RuleId.ShouldBe("HXC130");
            (await scope.Engine.Scanner.FindAsync(runId, bound.Token).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
            Directory.Exists(Path.Combine(scope.Directory, "w", runId)).ShouldBeFalse();
            CompositionRunState? state = await scope.Engine.StateStore.TryReadAsync(runId, bound.Token).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            state.Status.ShouldBe(CompositionRunStatus.Cancelled);
            state.RuleId.ShouldBe("HXC130");

            CompositionDownResult down = await scope.Engine.DownAsync(runId, bound.Token).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            File.Exists(scope.Engine.StateStore.PathFor(runId)).ShouldBeFalse();
        }
    }

    /// <summary>
    /// Waits until the run has a state record and its first run-scoped container, i.e. it is mid-startup.
    /// </summary>
    /// <param name="scope">The engine scope.</param>
    /// <param name="start">The pending start.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The starting run identity.</returns>
    private static async Task<string> WaitForStartingRunAsync(LiveEngineScope scope, Task<CompositionStartResult> start, CancellationToken cancellationToken)
    {
        string stateDirectory = scope.Engine.StateStore.Directory;
        while (true)
        {
            start.IsCompleted.ShouldBeFalse("The run finished before it could be cancelled mid-startup.");
            string? statePath = Directory.Exists(stateDirectory)
                ? Directory.EnumerateFiles(stateDirectory, "*.json").SingleOrDefault()
                : null;
            if (statePath is not null)
            {
                string runId = Path.GetFileNameWithoutExtension(statePath);
                CompositionRunResources resources = await scope.Engine.Scanner.FindAsync(runId, cancellationToken).ConfigureAwait(false);
                if (resources.ContainerIds.Count > 0)
                {
                    return runId;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}