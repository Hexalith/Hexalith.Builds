// <copyright file="CompositionTestObservation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Optional observed test access in the existing readiness contract; it never grants qualification credit.</summary>
public sealed record CompositionTestObservation
{
    /// <summary>Gets the run that owns these observations.</summary>
    public required string RunId { get; init; }

    /// <summary>Gets the selected module.</summary>
    public required string ModuleId { get; init; }

    /// <summary>Gets the exact local canonical profile digest.</summary>
    public required string ProfileHash { get; init; }

    /// <summary>Gets the canonical profile bytes.</summary>
    public required string ProfileJson { get; init; }

    /// <summary>Gets the actually loaded candidate assembly hashes, or an empty map when unavailable.</summary>
    public IReadOnlyDictionary<string, string> LoadedArtifactHashes { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets each service's observed one-hour runtime configuration.</summary>
    public IReadOnlyDictionary<string, int> ServiceRetentionSeconds { get; init; } = new Dictionary<string, int>();

    /// <summary>Gets the combined actually loaded candidate identity, absent when unavailable.</summary>
    public string? ObservedCandidateHash { get; init; }

    /// <summary>Gets the actual backend image/version/TLS identity, absent when unavailable.</summary>
    public string? ObservedBackendIdentity { get; init; }

    /// <summary>Gets the executed runtime binary digest, absent when unavailable.</summary>
    public string? ObservedRuntimeHash { get; init; }

    /// <summary>Gets or initializes a value indicating whether the runtime digest matches the approved immutable image executable.</summary>
    public bool RuntimeArtifactVerified { get; init; }

    /// <summary>Gets actually allocated app and sidecar endpoints, addressed only by declared resource identity.</summary>
    public IReadOnlyDictionary<string, Uri> Endpoints { get; init; } = new Dictionary<string, Uri>();

    /// <summary>Gets the observed run-labelled PostgreSQL container ID.</summary>
    public string? PostgresqlContainerId { get; init; }

    /// <summary>Gets the private run-owned test access file; credentials are never serialized here.</summary>
    public string? AccessPath { get; init; }

    /// <summary>Gets the private short-gate file controlled by the runner.</summary>
    public string? GatePath { get; init; }

    /// <summary>Gets the actual observation time.</summary>
    public DateTimeOffset ObservedAtUtc { get; init; }

    /// <summary>Gets a value indicating whether all mandatory measurements, rather than declarations, are available.</summary>
    public bool IsComplete => !string.IsNullOrEmpty(ObservedCandidateHash) && !string.IsNullOrEmpty(ObservedBackendIdentity)
        && !string.IsNullOrEmpty(ObservedRuntimeHash) && RuntimeArtifactVerified && LoadedArtifactHashes.Count > 0 && ServiceRetentionSeconds.Count == 3 && ServiceRetentionSeconds.Values.All(seconds => seconds == 3600) && Endpoints.Count >= 6 && PostgresqlContainerId?.Length == 64;
}
