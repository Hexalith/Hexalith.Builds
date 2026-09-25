// <copyright file="PersistedProfileEvidence.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Names the persisted checks a completed two-module profile has passed; the runner emits and the acceptance validator requires the same set.
/// </summary>
internal static class PersistedProfileEvidence
{
    private static readonly string[] _checks =
    [
        "authenticated-write",
        "persisted-event-and-projection",
        "restart-and-rehydrated-read",
        "retry-idempotency",
        "tenant-isolation",
        "two-instance-read",
    ];

    /// <summary>
    /// Gets the ordinal-sorted persisted assertions of a completed profile.
    /// </summary>
    /// <param name="moduleIds">The profile module identifiers.</param>
    /// <returns>Every check for every module.</returns>
    public static IReadOnlyList<string> Assertions(IEnumerable<string> moduleIds) =>
        [.. moduleIds.SelectMany(module => _checks.Select(check => module + ":" + check)).Order(StringComparer.Ordinal)];

    /// <summary>
    /// Gets the ordinal-sorted expected event sequences of a completed profile.
    /// </summary>
    /// <param name="moduleIds">The profile module identifiers.</param>
    /// <returns>The first write and the retry write for every module.</returns>
    public static IReadOnlyList<string> Sequences(IEnumerable<string> moduleIds) =>
        [.. moduleIds.Select(module => module + ":1,2").Order(StringComparer.Ordinal)];
}
