// <copyright file="LiveLifecycleTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Net;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Proves lifecycle (a) and cleanup (h) of runner-owned runs.
/// </summary>
[Collection(LiveLane.Name)]
public sealed class LiveLifecycleTests
{
    private static readonly string[] _expectedResources =
    [
        "eventstore",
        "eventstore-dapr-cli",
        "p0-inventory",
        "p0-inventory-dapr-cli",
        "p0-orders",
        "p0-orders-dapr-cli",
        "placement",
        "redis",
        "scheduler",
        "ui",
    ];

    /// <summary>
    /// (a) A run starts, every resource reports healthy and carries the run identity, and the run stops.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RunStartsHealthyCarriesRunIdentityAndStopsAsync()
    {
        LiveGate.SkipUnlessEnabled();
        using CancellationTokenSource bound = Bound();
        LiveEngineScope scope = new("lifecycle");
        await using (scope.ConfigureAwait(true))
        {
            CompositionRunSession session = await scope.StartReadyAsync(bound.Token).ConfigureAwait(true);

            session.Readiness.RunId.ShouldBe(session.RunId);
            session.Readiness.Resources.Select(resource => resource.Name).ShouldBe(_expectedResources);
            session.Readiness.Resources.ShouldAllBe(resource => resource.State == "Healthy");
            using (LiveEventStore eventStore = new(session))
            {
                (await eventStore.GetStatusAsync("/health", bound.Token).ConfigureAwait(true)).ShouldBe(HttpStatusCode.OK);
            }

            CompositionRunResources tagged = await scope.Engine.Scanner.FindAsync(session.RunId, bound.Token).ConfigureAwait(true);
            tagged.ContainerIds.Count.ShouldBe(1);
            tagged.ProcessIds.Count.ShouldBeGreaterThanOrEqualTo(8);
            CompositionRunState? state = await scope.Engine.StateStore.TryReadAsync(session.RunId, bound.Token).ConfigureAwait(true);
            _ = state.ShouldNotBeNull();
            state.Status.ShouldBe(CompositionRunStatus.Ready);
            string stateText = await File.ReadAllTextAsync(scope.Engine.StateStore.PathFor(session.RunId), bound.Token).ConfigureAwait(true);
            string planText = await File.ReadAllTextAsync(Path.Combine(session.Plan.Workspace, "plan.json"), bound.Token).ConfigureAwait(true);
            string readinessText = await File.ReadAllTextAsync(session.Plan.ReadinessPath, bound.Token).ConfigureAwait(true);
            foreach (string retained in new[] { stateText, planText, readinessText })
            {
                retained.ShouldNotContain(session.SigningKey);
            }

            AssertOnlyTokenValidatorsHoldTheKey(session, tagged, state.AppHostProcessId);

            CompositionDownResult down = await scope.Engine.DownAsync(session, bound.Token).ConfigureAwait(true);

            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            down.Remaining.IsEmpty.ShouldBeTrue();
        }
    }

    /// <summary>
    /// (h) After <c>down</c> nothing tagged with the run remains, and a repeated <c>down</c> succeeds.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownLeavesNothingTaggedAndIsIdempotentAsync()
    {
        LiveGate.SkipUnlessEnabled();
        using CancellationTokenSource bound = Bound();
        LiveEngineScope scope = new("cleanup");
        await using (scope.ConfigureAwait(true))
        {
            CompositionRunSession session = await scope.StartReadyAsync(bound.Token).ConfigureAwait(true);
            string runId = session.RunId;

            CompositionDownResult first = await scope.Engine.DownAsync(session, bound.Token).ConfigureAwait(true);
            CompositionRunResources afterFirst = await scope.Engine.Scanner.FindAsync(runId, bound.Token).ConfigureAwait(true);
            CompositionDownResult second = await scope.Engine.DownAsync(runId, bound.Token).ConfigureAwait(true);

            first.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            afterFirst.IsEmpty.ShouldBeTrue();
            Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
            File.Exists(scope.Engine.StateStore.PathFor(runId)).ShouldBeFalse();
            second.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            second.Result.Diagnostics.Single().RuleId.ShouldBe("HXI001");
            second.Remaining.IsEmpty.ShouldBeTrue();
        }
    }

    /// <summary>
    /// Verifies no tagged run process other than the AppHost (which receives and discards the key) and the
    /// EventStore host (which validates tokens) carries the signing key in its environment.
    /// </summary>
    /// <param name="session">The ready session.</param>
    /// <param name="tagged">The tagged run resources.</param>
    /// <param name="appHostProcessId">The recorded AppHost process identity.</param>
    private static void AssertOnlyTokenValidatorsHoldTheKey(CompositionRunSession session, CompositionRunResources tagged, int? appHostProcessId)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int inspected = 0;
        foreach (int processId in tagged.ProcessIds.Where(processId => processId != appHostProcessId))
        {
            string environment;
            try
            {
                if (File.ReadAllText($"/proc/{processId}/cmdline").Contains("Hexalith.Builds.Module.EventStoreHost", StringComparison.Ordinal))
                {
                    continue;
                }

                environment = File.ReadAllText($"/proc/{processId}/environ");
            }
            catch (IOException)
            {
                // The process exited between the scan and this read.
                continue;
            }

            environment.ShouldNotContain(CompositionEnvironment.SigningKey + "=", customMessage: $"process {processId}");
            environment.ShouldNotContain(session.SigningKey, customMessage: $"process {processId}");
            inspected++;
        }

        inspected.ShouldBeGreaterThan(0);
    }

    private static CancellationTokenSource Bound()
    {
        CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromMinutes(10));
        return bound;
    }
}