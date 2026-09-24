// <copyright file="LivePersistedRunTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Globalization;
using System.Net;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Proves persistence (b), Dapr invocation (c), identity (d), and FrontComposer registration (e) on one live run.
/// </summary>
/// <param name="fixture">The shared live run.</param>
[Collection(LiveLane.Name)]
public sealed class LivePersistedRunTests(LiveRunFixture fixture) : IClassFixture<LiveRunFixture>
{
    /// <summary>
    /// (b) An authenticated command for each module completes, and the run-scoped Redis end-state holds the
    /// run-unique aggregate events at the expected sequence, folded from rehydrated module state.
    /// </summary>
    /// <param name="domain">The fixture domain.</param>
    /// <param name="commandType">The command type.</param>
    /// <param name="eventType">The expected persisted event type.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("p0-orders", "PlaceOrder", "Hexalith.Builds.P0Fixture.Orders.OrderPlaced")]
    [InlineData("p0-inventory", "ReserveStock", "Hexalith.Builds.P0Fixture.Inventory.StockReserved")]
    public async Task AuthenticatedCommandsPersistEventsAtExpectedSequenceAsync(string domain, string commandType, string eventType)
    {
        CompositionRunSession session = fixture.RequireSession();
        using CancellationTokenSource bound = Bound();
        string tenant = session.Plan.TenantNamespace;
        string aggregateId = $"{session.Plan.ResourceNamespace}-{domain}-1";
        using LiveEventStore eventStore = new(session);
        string token = eventStore.Token([tenant]);

        (await eventStore.SubmitAndWaitAsync(token, tenant, domain, aggregateId, commandType, 3, bound.Token).ConfigureAwait(true)).ShouldBe("Completed");
        (await eventStore.SubmitAndWaitAsync(token, tenant, domain, aggregateId, commandType, 5, bound.Token).ConfigureAwait(true)).ShouldBe("Completed");

        LiveStateStore store = await LiveStateStore.ConnectAsync(session).ConfigureAwait(true);
        await using (store.ConfigureAwait(true))
        {
            LivePersistedEvent? first = await store.ReadEventAsync(tenant, domain, aggregateId, 1).ConfigureAwait(true);
            LivePersistedEvent? second = await store.ReadEventAsync(tenant, domain, aggregateId, 2).ConfigureAwait(true);

            first.ShouldBe(new LivePersistedEvent(eventType, 1, tenant, domain, aggregateId, 3, 1));
            second.ShouldBe(new LivePersistedEvent(eventType, 2, tenant, domain, aggregateId, 5, 2));
            (await store.ReadEventAsync(tenant, domain, aggregateId, 3).ConfigureAwait(true)).ShouldBeNull();
            (await store.ReadCurrentSequenceAsync(tenant, domain, aggregateId).ConfigureAwait(true)).ShouldBe(2);
            (await store.ReadCurrentSequenceAsync(session.Plan.ForeignTenantNamespace, domain, aggregateId).ConfigureAwait(true)).ShouldBeNull();
        }
    }

    /// <summary>
    /// (c) EventStore reaches each module through the Dapr sidecars and the run's own name resolution.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task EventStoreInvokesModulesThroughDaprSidecarsAsync()
    {
        CompositionRunSession session = fixture.RequireSession();
        using CancellationTokenSource bound = Bound();
        using HttpClient sidecar = new()
        {
            BaseAddress = new Uri("http://127.0.0.1:" + session.Plan.Ports.EventStoreDaprHttp.ToString(CultureInfo.InvariantCulture)),
        };

        using (HttpResponseMessage metadata = await sidecar.GetAsync(new Uri("/v1.0/metadata", UriKind.Relative), bound.Token).ConfigureAwait(true))
        {
            metadata.StatusCode.ShouldBe(HttpStatusCode.OK);
            string body = await metadata.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(true);
            body.ShouldContain("\"id\":\"eventstore\"");
            body.ShouldContain("AggregateActor");
        }

        foreach (CompositionRunModule module in session.Plan.Modules)
        {
            using HttpResponseMessage invoked = await sidecar
                .GetAsync(new Uri($"/v1.0/invoke/{module.AppId}/method/alive", UriKind.Relative), bound.Token)
                .ConfigureAwait(true);
            invoked.StatusCode.ShouldBe(HttpStatusCode.OK, module.AppId);
        }

        using HttpResponseMessage unknown = await sidecar
            .GetAsync(new Uri("/v1.0/invoke/g4-unregistered-app/method/alive", UriKind.Relative), bound.Token)
            .ConfigureAwait(true);
        unknown.IsSuccessStatusCode.ShouldBeFalse();

        using LiveEventStore eventStore = new(session);
        string tenant = session.Plan.TenantNamespace;
        (await eventStore.SubmitAndWaitAsync(
            eventStore.Token([tenant]),
            tenant,
            "p0-orders",
            session.Plan.ResourceNamespace + "-dapr-1",
            "PlaceOrder",
            1,
            bound.Token).ConfigureAwait(true)).ShouldBe("Completed");
    }

    /// <summary>
    /// (d) The run identity rejects anonymous requests with 401, a token signed by another key with 401,
    /// a foreign tenant with 403, and a missing submit permission with 403.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task IdentityRejectsAnonymousForeignKeyAndForeignTenantAsync()
    {
        CompositionRunSession session = fixture.RequireSession();
        using CancellationTokenSource bound = Bound();
        using LiveEventStore eventStore = new(session);
        string tenant = session.Plan.TenantNamespace;
        string foreign = session.Plan.ForeignTenantNamespace;
        string aggregateId = session.Plan.ResourceNamespace + "-identity-1";

        (await eventStore.SubmitAsync(null, tenant, "p0-orders", aggregateId, "PlaceOrder", 1, bound.Token).ConfigureAwait(true)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await eventStore.SubmitAsync(eventStore.Token([tenant], signingKey: CompositionSigningKey.Create()), tenant, "p0-orders", aggregateId, "PlaceOrder", 1, bound.Token).ConfigureAwait(true)).Status
            .ShouldBe(HttpStatusCode.Unauthorized);
        (await eventStore.SubmitAsync(eventStore.Token([tenant]), foreign, "p0-orders", aggregateId, "PlaceOrder", 1, bound.Token).ConfigureAwait(true)).Status
            .ShouldBe(HttpStatusCode.Forbidden);
        (await eventStore.SubmitAsync(eventStore.Token([tenant], ["query:read"]), tenant, "p0-orders", aggregateId, "PlaceOrder", 1, bound.Token).ConfigureAwait(true)).Status
            .ShouldBe(HttpStatusCode.Forbidden);

        LiveStateStore store = await LiveStateStore.ConnectAsync(session).ConfigureAwait(true);
        await using (store.ConfigureAwait(true))
        {
            (await store.ReadCurrentSequenceAsync(foreign, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBeNull();
            (await store.ReadCurrentSequenceAsync(tenant, "p0-orders", aggregateId).ConfigureAwait(true)).ShouldBeNull();
        }
    }

    /// <summary>
    /// (e) The generic FrontComposer host serves 200 and shows both module bounded contexts.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task FrontComposerHostShowsBothBoundedContextsAsync()
    {
        CompositionRunSession session = fixture.RequireSession();
        using CancellationTokenSource bound = Bound();
        _ = session.Readiness.UiEndpoint.ShouldNotBeNull();
        using HttpClient client = new() { BaseAddress = session.Readiness.UiEndpoint };

        using HttpResponseMessage response = await client.GetAsync(new Uri("/", UriKind.Relative), bound.Token).ConfigureAwait(true);
        string html = await response.Content.ReadAsStringAsync(bound.Token).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        html.ShouldContain("data-bounded-context=\"p0-orders\"");
        html.ShouldContain("data-bounded-context=\"p0-inventory\"");
        html.ShouldContain("P0 Orders");
        html.ShouldContain("P0 Inventory");
    }

    private static CancellationTokenSource Bound()
    {
        CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        bound.CancelAfter(TimeSpan.FromMinutes(5));
        return bound;
    }
}