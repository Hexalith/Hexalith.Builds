// <copyright file="PlatformEventStoreExtensionAuthority.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Collections.Frozen;

/// <summary>
/// Bounds the AD-13 authority of EventStore extension packages: configuration inputs only, no Dapr client and no
/// provider SDK, except a spine-admitted AD-9 exception's own SDK and own secret.
/// </summary>
internal static class PlatformEventStoreExtensionAuthority
{
    /// <summary>The spine admits each exception for one fixed owner module; a module's own declaration grants nothing.</summary>
    private static readonly FrozenDictionary<string, (string Owner, string[] Sdks)> _exceptions = new Dictionary<string, (string Owner, string[] Sdks)>(StringComparer.Ordinal)
    {
        ["memories-falkordb-graph"] = ("memories", ["NFalkorDB"]),
        ["memories-redis-search-vector"] = ("memories", ["NRedisStack", "StackExchange.Redis"]),
        ["memories-set-nx-preflight-dedup"] = ("memories", ["StackExchange.Redis"]),
        ["memories-redis-coordination"] = ("memories", ["StackExchange.Redis"]),
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly string[] _providerSdks =
    [
        "StackExchange.Redis", "NRedisStack", "NFalkorDB", "Npgsql", "MongoDB.Driver", "MySqlConnector", "Microsoft.Data.SqlClient",
        "Microsoft.Azure.Cosmos", "Confluent.Kafka", "RabbitMQ.Client", "Qdrant.Client", "Elastic.Clients.Elasticsearch", "NEST", "CassandraCSharpDriver",
    ];

    private static readonly string[] _providerSdkFamilies =
    [
        "StackExchange.Redis.", "NRedisStack.", "Npgsql.", "MongoDB.", "Azure.", "Microsoft.Azure.", "AWSSDK.", "Google.Cloud.",
        "Microsoft.EntityFrameworkCore.", "Microsoft.Extensions.Caching.StackExchangeRedis", "Confluent.", "RabbitMQ.",
    ];

    /// <summary>Gets the only module project that may publish the <c>eventstore</c> image: the EventStore server owner's own server.</summary>
    internal static string EventStoreServerProject => "src/Hexalith.EventStore/Hexalith.EventStore.csproj";

    /// <summary>Gets the EventStore server owner's module identity.</summary>
    internal static string EventStoreModule => "eventstore";

    /// <summary>Classifies a direct dependency name as a Dapr client or a known provider SDK.</summary>
    /// <param name="dependency">The package identity or referenced project name.</param>
    /// <returns><c>Dapr client</c>, <c>provider SDK</c>, or null for any other dependency.</returns>
    internal static string? Classify(string dependency)
    {
        ArgumentNullException.ThrowIfNull(dependency);
        bool dapr = dependency.Equals("Dapr", StringComparison.OrdinalIgnoreCase) || dependency.StartsWith("Dapr.", StringComparison.OrdinalIgnoreCase);
        bool provider = _providerSdks.Contains(dependency, StringComparer.OrdinalIgnoreCase)
            || _providerSdkFamilies.Any(family => dependency.StartsWith(family, StringComparison.OrdinalIgnoreCase));
        return (dapr, provider) switch
        {
            (true, _) => "Dapr client",
            (false, true) => "provider SDK",
            _ => null,
        };
    }

    /// <summary>Returns the spine-admitted owner module of a named AD-9 exception.</summary>
    /// <param name="exception">The named exception, or null.</param>
    /// <returns>The fixed owner module, or null for an unadmitted name.</returns>
    internal static string? Owner(string? exception) =>
        exception is not null && _exceptions.TryGetValue(exception, out (string Owner, string[] Sdks) admitted) ? admitted.Owner : null;

    /// <summary>Returns whether a named AD-9 exception admits a provider SDK for its fixed owner module.</summary>
    /// <param name="exception">The named exception, or null.</param>
    /// <param name="module">The declaring module.</param>
    /// <param name="dependency">The provider SDK identity.</param>
    /// <returns>Whether the module owns the exception and the dependency is the exception's own SDK.</returns>
    internal static bool AdmitsSdk(string? exception, string module, string dependency) =>
        Owner(exception) == module && _exceptions[exception!].Sdks.Contains(dependency, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns whether a secret reference is the named AD-9 exception's own least-privilege secret for its fixed owner module.</summary>
    /// <param name="exception">The named exception, or null.</param>
    /// <param name="module">The declaring module.</param>
    /// <param name="reference">The secret reference identity.</param>
    /// <returns>Whether the module owns the exception and the reference is the exception name or starts with it and a hyphen.</returns>
    internal static bool AdmitsSecret(string? exception, string module, string? reference) =>
        Owner(exception) == module && reference is not null
        && (reference == exception || reference.StartsWith(exception + "-", StringComparison.Ordinal));

    /// <summary>Lists a named exception's own SDKs for diagnostics.</summary>
    /// <param name="exception">The named exception.</param>
    /// <returns>The SDK identities, or an empty list.</returns>
    internal static IReadOnlyList<string> Sdks(string? exception) =>
        exception is not null && _exceptions.TryGetValue(exception, out (string Owner, string[] Sdks) admitted) ? admitted.Sdks : [];
}
