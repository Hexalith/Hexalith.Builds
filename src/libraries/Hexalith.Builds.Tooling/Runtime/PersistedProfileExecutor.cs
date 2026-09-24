// <copyright file="PersistedProfileExecutor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

using StackExchange.Redis;

/// <summary>
/// Qualifies one two-module profile through the real gateway and Redis actor state.
/// </summary>
internal static class PersistedProfileExecutor
{
    /// <summary>Runs the complete persisted profile.</summary>
    /// <param name="session">The ready run session.</param>
    /// <param name="profile">The validated profile.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A result that passes only after every assertion succeeds.</returns>
    public static async Task<ToolCommandResult> ExecuteAsync(
        CompositionRunSession session,
        PersistedProfileDefinition profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(profile);
        string step = "profile.start";
        try
        {
            using PersistedProfileClient client = new(session);
            if (!client.HasSecondInstance || profile.Modules.Count != 2)
            {
                return Fail("HXP001", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, ToolExitCode.TopologyOrLifecycle);
            }

            PersistedProfileStore store = await PersistedProfileStore.ConnectAsync(session).ConfigureAwait(false);
            await using ConfiguredAsyncDisposable storeDisposal = store.ConfigureAwait(false);
            string tenant = session.Plan.TenantNamespace;
            string foreign = session.Plan.ForeignTenantNamespace;
            string aggregateId = session.Plan.ResourceNamespace;
            string token = client.Token([tenant]);
            foreach (PersistedProfileModule module in profile.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                step = module.ModuleId + ".preflight";
                if (await store.ReadSequenceAsync(tenant, module.ModuleId, aggregateId).ConfigureAwait(false) is not null
                    || await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, 1).ConfigureAwait(false) is not null
                    || await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, 2).ConfigureAwait(false) is not null
                    || await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, 3).ConfigureAwait(false) is not null
                    || await store.ReadProjectionAsync(module.ProjectionType, tenant, aggregateId).ConfigureAwait(false) is not null
                    || await store.ReadSequenceAsync(foreign, module.ModuleId, aggregateId).ConfigureAwait(false) is not null
                    || await store.ReadEventAsync(foreign, module.ModuleId, aggregateId, 1).ConfigureAwait(false) is not null
                    || await store.ReadProjectionAsync(module.ProjectionType, foreign, aggregateId).ConfigureAwait(false) is not null)
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".authentication";
                string firstMessageId = Guid.NewGuid().ToString("N");
                if (await client.SubmitAsync(null, tenant, module.ModuleId, aggregateId, module.CommandType, 1, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false) != HttpStatusCode.Unauthorized
                    || await client.SubmitAsync(client.Token([tenant], ["query:read"]), tenant, module.ModuleId, aggregateId, module.CommandType, 1, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false) != HttpStatusCode.Forbidden)
                {
                    return Fail("HXP005", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, step);
                }

                step = module.ModuleId + ".first-write";
                if (await client.SubmitAsync(token, tenant, module.ModuleId, aggregateId, module.CommandType, module.InitialQuantity, firstMessageId, cancellationToken).ConfigureAwait(false) != HttpStatusCode.Accepted
                    || !await client.WaitForCompletedAsync(token, firstMessageId, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP002", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, step);
                }

                step = module.ModuleId + ".first-state";
                if (!await AwaitStateAsync(store, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".first-primary-read";
                if (!await QueryMatchesAsync(client, false, token, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".first-peer-read";
                if (!await QueryMatchesAsync(client, true, token, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".tenant-isolation";
                if (await client.SubmitAsync(token, foreign, module.ModuleId, aggregateId, module.CommandType, 1, Guid.NewGuid().ToString("N"), cancellationToken).ConfigureAwait(false) != HttpStatusCode.Forbidden
                    || (await client.QueryAsync(true, token, foreign, module.ModuleId, aggregateId, module.ProjectionType, cancellationToken).ConfigureAwait(false)).Status != HttpStatusCode.Forbidden
                    || await store.ReadSequenceAsync(foreign, module.ModuleId, aggregateId).ConfigureAwait(false) is not null)
                {
                    return Fail("HXP005", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, step);
                }

                step = module.ModuleId + ".stop";
                if (!await CompositionResourceController.ExecuteAsync(session, "stop-eventstore", cancellationToken).ConfigureAwait(false)
                    || !await client.PrimaryIsUnavailableAsync(cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP004", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, ToolExitCode.TopologyOrLifecycle, step);
                }

                step = module.ModuleId + ".stopped-state";
                if (!await AwaitStateAsync(store, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".stopped-peer-read";
                if (!await QueryMatchesAsync(client, true, token, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".restart";
                if (!await CompositionResourceController.ExecuteAsync(session, "start-eventstore", cancellationToken).ConfigureAwait(false)
                    || !await QueryMatchesAsync(client, false, token, module, tenant, aggregateId, 1, module.InitialQuantity, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP004", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, ToolExitCode.TopologyOrLifecycle, step);
                }

                step = module.ModuleId + ".retry";
                HttpStatusCode retry = await client.SubmitAsync(token, tenant, module.ModuleId, aggregateId, module.CommandType, module.InitialQuantity, firstMessageId, cancellationToken).ConfigureAwait(false);
                if (retry is not (HttpStatusCode.Accepted or HttpStatusCode.OK or HttpStatusCode.Conflict)
                    || !await client.WaitForCompletedAsync(token, firstMessageId, cancellationToken).ConfigureAwait(false)
                    || await store.ReadSequenceAsync(tenant, module.ModuleId, aggregateId).ConfigureAwait(false) != 1
                    || await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, 2).ConfigureAwait(false) is not null)
                {
                    return Fail("HXP006", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".second-write";
                string secondMessageId = Guid.NewGuid().ToString("N");
                if (await client.SubmitAsync(token, tenant, module.ModuleId, aggregateId, module.CommandType, module.RetryQuantity, secondMessageId, cancellationToken).ConfigureAwait(false) != HttpStatusCode.Accepted
                    || !await client.WaitForCompletedAsync(token, secondMessageId, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP002", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, step);
                }

                step = module.ModuleId + ".second-state";
                int expectedTotal = checked(module.InitialQuantity + module.RetryQuantity);
                if (!await AwaitStateAsync(store, module, tenant, aggregateId, 2, expectedTotal, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".second-primary-read";
                if (!await QueryMatchesAsync(client, false, token, module, tenant, aggregateId, 2, expectedTotal, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".second-peer-read";
                if (!await QueryMatchesAsync(client, true, token, module, tenant, aggregateId, 2, expectedTotal, cancellationToken).ConfigureAwait(false))
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }

                step = module.ModuleId + ".final-isolation";
                if (await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, 3).ConfigureAwait(false) is not null
                    || await store.ReadEventAsync(foreign, module.ModuleId, aggregateId, 1).ConfigureAwait(false) is not null
                    || await store.ReadProjectionAsync(module.ProjectionType, foreign, aggregateId).ConfigureAwait(false) is not null)
                {
                    return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
                }
            }

            return new ToolCommandResult(
                "passed",
                ToolOutcome.Passed(),
                [new ToolDiagnostic("HXI004", ToolPhase.Test, ToolFailureCategory.None, "The two-module persisted profile passed.", "profile")]);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or RedisException or IOException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return Fail("HXP003", ToolPhase.Test, ToolFailureCategory.PersistedState, ToolExitCode.PersistedState, step);
        }
    }

    /// <summary>Checks a physical event, aggregate sequence, and projection as one fail-closed snapshot.</summary>
    /// <param name="eventState">The persisted event, if present.</param>
    /// <param name="projection">The persisted projection, if present.</param>
    /// <param name="currentSequence">The aggregate sequence, if present.</param>
    /// <param name="module">The module expectations.</param>
    /// <param name="tenant">The expected tenant.</param>
    /// <param name="aggregateId">The expected aggregate ID.</param>
    /// <param name="sequence">The expected sequence.</param>
    /// <param name="total">The expected total quantity.</param>
    /// <returns>Whether all physical state agrees.</returns>
    internal static bool StateMatches(
        JsonElement? eventState,
        JsonElement? projection,
        long? currentSequence,
        PersistedProfileModule module,
        string tenant,
        string aggregateId,
        int sequence,
        int total)
    {
        if (eventState is null || projection is null || currentSequence != sequence)
        {
            return false;
        }

        JsonElement eventValue = eventState.Value;
        byte[] payloadBytes = Convert.FromBase64String(eventValue.GetProperty("payload").GetString()!);
        using JsonDocument payload = JsonDocument.Parse(payloadBytes);
        int expectedQuantity = sequence == 1 ? module.InitialQuantity : module.RetryQuantity;
        return string.Equals(eventValue.GetProperty("eventTypeName").GetString(), module.EventType, StringComparison.Ordinal)
            && eventValue.GetProperty("sequenceNumber").GetInt64() == sequence
            && string.Equals(eventValue.GetProperty("tenantId").GetString(), tenant, StringComparison.Ordinal)
            && string.Equals(eventValue.GetProperty("domain").GetString(), module.ModuleId, StringComparison.Ordinal)
            && string.Equals(eventValue.GetProperty("aggregateId").GetString(), aggregateId, StringComparison.Ordinal)
            && payload.RootElement.GetProperty("Quantity").GetInt32() == expectedQuantity
            && payload.RootElement.GetProperty("Sequence").GetInt32() == sequence
            && Matches(projection.Value, tenant, aggregateId, sequence, total);
    }

    private static async Task<bool> AwaitStateAsync(
        PersistedProfileStore store,
        PersistedProfileModule module,
        string tenant,
        string aggregateId,
        int sequence,
        int total,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement? eventState = await store.ReadEventAsync(tenant, module.ModuleId, aggregateId, sequence).ConfigureAwait(false);
            JsonElement? projection = await store.ReadProjectionAsync(module.ProjectionType, tenant, aggregateId).ConfigureAwait(false);
            long? currentSequence = await store.ReadSequenceAsync(tenant, module.ModuleId, aggregateId).ConfigureAwait(false);
            if (StateMatches(eventState, projection, currentSequence, module, tenant, aggregateId, sequence, total))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static async Task<bool> QueryMatchesAsync(
        PersistedProfileClient client,
        bool second,
        string token,
        PersistedProfileModule module,
        string tenant,
        string aggregateId,
        int sequence,
        int total,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (HttpStatusCode status, JsonElement payload) = await client.QueryAsync(
                second, token, tenant, module.ModuleId, aggregateId, module.ProjectionType, cancellationToken).ConfigureAwait(false);
            if (status == HttpStatusCode.OK && payload.ValueKind == JsonValueKind.Object
                && Matches(payload, tenant, aggregateId, sequence, total))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool Matches(JsonElement value, string tenant, string aggregateId, int sequence, int total) =>
        Property(value, "TenantId") is { } tenantProperty
        && Property(value, "AggregateId") is { } aggregateProperty
        && Property(value, "Sequence") is { } sequenceProperty
        && Property(value, "Count") is { } countProperty
        && Property(value, "TotalQuantity") is { } totalProperty
        && string.Equals(tenantProperty.GetString(), tenant, StringComparison.Ordinal)
        && string.Equals(aggregateProperty.GetString(), aggregateId, StringComparison.Ordinal)
        && sequenceProperty.GetInt64() == sequence
        && countProperty.GetInt32() == sequence
        && totalProperty.GetInt32() == total;

    private static JsonElement? Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (value.TryGetProperty(name, out JsonElement exact))
        {
            return exact;
        }

        string camel = char.ToLowerInvariant(name[0]) + name[1..];
        return value.TryGetProperty(camel, out JsonElement alternate) ? alternate : null;
    }

    private static ToolCommandResult Fail(string rule, ToolPhase phase, ToolFailureCategory category, ToolExitCode exit, string field = "profile") =>
        new(
            "failed",
            ToolOutcome.Passed().Fail(phase, category, rule, exit),
            [new ToolDiagnostic(rule, phase, category, "The persisted profile assertion did not pass.", field)]);
}
