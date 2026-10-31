// <copyright file="Program.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using Dapr;

using Hexalith.EventStore.Extensions;
using Hexalith.EventStore.HealthChecks;
using Hexalith.EventStore.Middleware;
using Hexalith.EventStore.OpenApi;
using Hexalith.EventStore.Server.Configuration;
using Hexalith.EventStore.ServiceDefaults;
using Hexalith.EventStore.ServiceDefaults.Authentication;

using Microsoft.Extensions.Options;

// Builds-owned EventStore composition root. It mirrors the EventStore server host over the published
// Hexalith.EventStore.Gateway package; every endpoint, credential, and Dapr binding is supplied by the
// runner-owned AppHost through environment configuration. Modules never supply this host.
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddDaprClient();
builder.Services.AddHealthChecks()
    .AddEventStoreDaprHealthChecks();
builder.Services.AddEventStore();
builder.Services.AddEventStoreServer(builder.Configuration);
builder.Services.AddEventStoreDomainQueryRouting();
if (!string.IsNullOrEmpty(builder.Configuration["EventStore:RouteCatalog:DevelopmentBootstrap:CatalogPath"]))
{
#if HEXALITH_EVENTSTORE_MAPPED_SOURCE
    _ = Hexalith.EventStore.Server.Routing.RunScopedRouteCatalogGateway.AddEventStoreRunScopedCatalogGateway(builder.Services, builder.Configuration);
#else
    throw new NotSupportedException("Run-scoped route catalogs require the mapped EventStore source build.");
#endif
}

WebApplication app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.MapDefaultEndpoints();
app.UseRouting();
app.UseAuthentication();
app.UseRateLimiter();
app.UseEventStoreHttpHostSecurity();
app.UseAuthorization();
#if HEXALITH_EVENTSTORE_MAPPED_SOURCE
if (!string.IsNullOrEmpty(app.Configuration["EventStore:RouteCatalog:DevelopmentBootstrap:CatalogPath"]))
{
    _ = Hexalith.EventStore.ServiceDefaults.Routing.FiniteRouteCatalogReceiver.UseFiniteDevelopmentRouteCatalogReceiver(
        app,
        Hexalith.EventStore.Server.Routing.RunScopedRouteCatalogGateway.Operation);
}
#endif

app.UseCloudEvents();

bool declarativeSubscriptions = app.Configuration.GetValue<bool>("Dapr:DeclarativeSubscriptions");
app.MapControllers().Add(
    endpoint =>
    {
        for (int index = endpoint.Metadata.Count - 1; index >= 0; index--)
        {
            if (endpoint.Metadata[index] is not ITopicMetadata { Name: "*.*.projection-changed" } topic)
            {
                continue;
            }

            if (declarativeSubscriptions)
            {
                endpoint.Metadata.RemoveAt(index);
            }
            else
            {
                endpoint.Metadata[index] = new TopicAttribute(app.Services.GetRequiredService<IOptions<ProjectionChangeNotifierOptions>>().Value.PubSubName, topic.Name);
            }
        }
    });
app.MapErrorReferences();
app.MapApiVersionFallback();
if (!declarativeSubscriptions)
{
    _ = app.MapSubscribeHandler().RequireEventStoreChannel();
}

app.MapActorsHandlers().WithMetadata(EventStoreActorCallbackEndpointMetadata.Instance).RequireEventStoreChannel();

await app.RunAsync().ConfigureAwait(false);
