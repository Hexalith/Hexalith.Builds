// <copyright file="LiveRunIsolationTests.cs" company="ITANEO">
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
/// Proves run isolation (f): two concurrent runs use distinct ports and resources, and <c>down</c> of one leaves the other healthy.
/// </summary>
[Collection(LiveLane.Name)]
public sealed class LiveRunIsolationTests
{
    /// <summary>
    /// (f) Two concurrent runs are isolated.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task ConcurrentRunsAreIsolatedAndDownOfOneLeavesTheOtherHealthyAsync()
    {
        LiveGate.SkipUnlessEnabled();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromMinutes(12));
        LiveEngineScope scope = new("isolation");
        await using (scope.ConfigureAwait(true))
        {
            CompositionRunSession[] sessions = await Task.WhenAll(
                scope.StartReadyAsync(bound.Token),
                scope.StartReadyAsync(bound.Token)).ConfigureAwait(true);
            CompositionRunSession first = sessions[0];
            CompositionRunSession second = sessions[1];

            first.RunId.ShouldNotBe(second.RunId);
            first.Plan.TenantNamespace.ShouldNotBe(second.Plan.TenantNamespace);
            first.Plan.Ports.ToList().Intersect(second.Plan.Ports.ToList()).ShouldBeEmpty();
            first.Readiness.EventStoreEndpoint.ShouldNotBe(second.Readiness.EventStoreEndpoint);
            first.Readiness.UiEndpoint.ShouldNotBe(second.Readiness.UiEndpoint);
            first.Plan.RedisContainerName.ShouldNotBe(second.Plan.RedisContainerName);
            CompositionRunResources firstResources = await scope.Engine.Scanner.FindAsync(first.RunId, bound.Token).ConfigureAwait(true);
            CompositionRunResources secondResources = await scope.Engine.Scanner.FindAsync(second.RunId, bound.Token).ConfigureAwait(true);
            firstResources.ContainerIds.Intersect(secondResources.ContainerIds).ShouldBeEmpty();
            firstResources.ProcessIds.Intersect(secondResources.ProcessIds).ShouldBeEmpty();

            const string aggregateId = "g4-shared-aggregate";
            await SubmitAsync(first, aggregateId, bound.Token).ConfigureAwait(true);
            await SubmitAsync(second, aggregateId, bound.Token).ConfigureAwait(true);
            LiveStateStore firstStore = await LiveStateStore.ConnectAsync(first).ConfigureAwait(true);
            await using (firstStore.ConfigureAwait(true))
            {
                (await firstStore.ReadCurrentSequenceAsync(first.Plan.TenantNamespace, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBe(1);
                (await firstStore.ReadCurrentSequenceAsync(second.Plan.TenantNamespace, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBeNull();
            }

            CompositionDownResult down = await scope.Engine.DownAsync(first, bound.Token).ConfigureAwait(true);

            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            (await scope.Engine.Scanner.FindAsync(first.RunId, bound.Token).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
            (await scope.Engine.Scanner.FindAsync(second.RunId, bound.Token).ConfigureAwait(true)).ContainerIds.Count.ShouldBe(1);
            using (LiveEventStore survivor = new(second))
            {
                (await survivor.GetStatusAsync("/health", bound.Token).ConfigureAwait(true)).ShouldBe(HttpStatusCode.OK);
            }

            await SubmitAsync(second, aggregateId, bound.Token).ConfigureAwait(true);
            LiveStateStore secondStore = await LiveStateStore.ConnectAsync(second).ConfigureAwait(true);
            await using (secondStore.ConfigureAwait(true))
            {
                (await secondStore.ReadCurrentSequenceAsync(second.Plan.TenantNamespace, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBe(2);
                (await secondStore.ReadCurrentSequenceAsync(first.Plan.TenantNamespace, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBeNull();
            }

            (await scope.Engine.DownAsync(second, bound.Token).ConfigureAwait(true)).Remaining.IsEmpty.ShouldBeTrue();
        }
    }

    private static async Task SubmitAsync(CompositionRunSession session, string aggregateId, CancellationToken cancellationToken)
    {
        using LiveEventStore eventStore = new(session);
        string tenant = session.Plan.TenantNamespace;
        (await eventStore.SubmitAndWaitAsync(eventStore.Token([tenant]), tenant, "p0-orders", aggregateId, "PlaceOrder", 1, cancellationToken).ConfigureAwait(false))
            .ShouldBe("Completed");
    }
}