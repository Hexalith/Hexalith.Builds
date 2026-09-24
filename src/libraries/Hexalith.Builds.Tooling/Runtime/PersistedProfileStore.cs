// <copyright file="PersistedProfileStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Text.Json;

using StackExchange.Redis;

/// <summary>
/// Reads actual Dapr actor state from the run-scoped Redis store.
/// </summary>
internal sealed class PersistedProfileStore : IAsyncDisposable
{
    private readonly ConnectionMultiplexer _connection;

    private PersistedProfileStore(ConnectionMultiplexer connection) => _connection = connection;

    /// <summary>Connects to the run-scoped state store.</summary>
    /// <param name="session">The owning run session.</param>
    /// <returns>The store reader.</returns>
    public static async Task<PersistedProfileStore> ConnectAsync(CompositionRunSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        ConnectionMultiplexer connection = await ConnectionMultiplexer.ConnectAsync(
            "127.0.0.1:" + session.Plan.Ports.Redis.ToString(CultureInfo.InvariantCulture)).ConfigureAwait(false);
        return new PersistedProfileStore(connection);
    }

    /// <summary>Reads one event by physical actor key.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The module domain.</param>
    /// <param name="aggregateId">The aggregate ID.</param>
    /// <param name="sequence">The event sequence.</param>
    /// <returns>The event JSON, or null if absent.</returns>
    public async Task<JsonElement?> ReadEventAsync(string tenant, string domain, string aggregateId, int sequence)
    {
        string actor = $"{tenant}:{domain}:{aggregateId}";
        return await ReadAsync($"eventstore||AggregateActor||{actor}||{actor}:events:{sequence.ToString(CultureInfo.InvariantCulture)}").ConfigureAwait(false);
    }

    /// <summary>Reads the persisted aggregate sequence, or null when absent.</summary>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The module domain.</param>
    /// <param name="aggregateId">The aggregate ID.</param>
    /// <returns>The current sequence.</returns>
    public async Task<long?> ReadSequenceAsync(string tenant, string domain, string aggregateId)
    {
        string actor = $"{tenant}:{domain}:{aggregateId}";
        JsonElement? state = await ReadAsync($"eventstore||AggregateActor||{actor}||{actor}:metadata").ConfigureAwait(false);
        return state?.GetProperty("currentSequence").GetInt64();
    }

    /// <summary>Reads the persisted projection and its opaque JSON state.</summary>
    /// <param name="projectionType">The projection type.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="aggregateId">The aggregate ID.</param>
    /// <returns>The projection state, or null when absent.</returns>
    public async Task<JsonElement?> ReadProjectionAsync(string projectionType, string tenant, string aggregateId)
    {
        string actor = $"{projectionType}:{tenant}:{aggregateId}";
        JsonElement? state = await ReadAsync($"eventstore||ProjectionActor||{actor}||projection-state").ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        JsonElement envelope = state.Value;
        if (!string.Equals(envelope.GetProperty("projectionType").GetString(), projectionType, StringComparison.Ordinal)
            || !string.Equals(envelope.GetProperty("tenantId").GetString(), tenant, StringComparison.Ordinal))
        {
            return null;
        }

        byte[] bytes = Convert.FromBase64String(envelope.GetProperty("stateBytes").GetString()!);
        using JsonDocument document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _connection.DisposeAsync().ConfigureAwait(false);

    private async Task<JsonElement?> ReadAsync(string key)
    {
        RedisValue data = await _connection.GetDatabase().HashGetAsync(key, "data").ConfigureAwait(false);
        if (data.IsNullOrEmpty)
        {
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(data.ToString());
        return document.RootElement.Clone();
    }
}
