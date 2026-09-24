// <copyright file="LiveStateStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using StackExchange.Redis;

/// <summary>
/// Reads the persisted end-state of one run directly from its run-scoped Redis store.
/// </summary>
internal sealed class LiveStateStore : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _connection;

    private LiveStateStore(ConnectionMultiplexer connection) => _connection = connection;

    /// <summary>
    /// Connects to a run's Redis store.
    /// </summary>
    /// <param name="session">The ready session.</param>
    /// <returns>The reader.</returns>
    public static async Task<LiveStateStore> ConnectAsync(CompositionRunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ConnectionMultiplexer connection = await ConnectionMultiplexer
            .ConnectAsync("127.0.0.1:" + session.Plan.Ports.Redis.ToString(CultureInfo.InvariantCulture))
            .ConfigureAwait(false);
        return new LiveStateStore(connection);
    }

    /// <summary>
    /// Reads one persisted event.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="aggregateId">The aggregate identity.</param>
    /// <param name="sequence">The sequence number.</param>
    /// <returns>The event, or null when absent.</returns>
    public async Task<LivePersistedEvent?> ReadEventAsync(string tenant, string domain, string aggregateId, int sequence)
    {
        string actor = $"{tenant}:{domain}:{aggregateId}";
        RedisValue data = await _connection.GetDatabase()
            .HashGetAsync($"eventstore||AggregateActor||{actor}||{actor}:events:{sequence.ToString(CultureInfo.InvariantCulture)}", "data")
            .ConfigureAwait(false);
        if (data.IsNullOrEmpty)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(data.ToString());
        JsonElement root = document.RootElement;
        using JsonDocument payload = JsonDocument.Parse(Convert.FromBase64String(root.GetProperty("payload").GetString()!));
        return new LivePersistedEvent(
            root.GetProperty("eventTypeName").GetString()!,
            root.GetProperty("sequenceNumber").GetInt64(),
            root.GetProperty("tenantId").GetString()!,
            root.GetProperty("domain").GetString()!,
            root.GetProperty("aggregateId").GetString()!,
            payload.RootElement.GetProperty("Quantity").GetInt32(),
            payload.RootElement.GetProperty("Sequence").GetInt32());
    }

    /// <summary>
    /// Reads the persisted current sequence of an aggregate.
    /// </summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The domain.</param>
    /// <param name="aggregateId">The aggregate identity.</param>
    /// <returns>The current sequence, or null when the aggregate has no metadata.</returns>
    public async Task<long?> ReadCurrentSequenceAsync(string tenant, string domain, string aggregateId)
    {
        string actor = $"{tenant}:{domain}:{aggregateId}";
        RedisValue data = await _connection.GetDatabase()
            .HashGetAsync($"eventstore||AggregateActor||{actor}||{actor}:metadata", "data")
            .ConfigureAwait(false);
        if (data.IsNullOrEmpty)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(data.ToString());
        return document.RootElement.GetProperty("currentSequence").GetInt64();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);
}