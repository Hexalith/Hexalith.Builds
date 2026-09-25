// <copyright file="RunHandoff.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.NativeTests;

/// <summary>
/// The runner-owned endpoint and identity handoff for one native test invocation.
/// </summary>
/// <param name="RunId">The runner invocation identity.</param>
/// <param name="EventStore">The primary EventStore endpoint.</param>
/// <param name="Tenant">The run-unique tenant.</param>
/// <param name="ResourceNamespace">The run-unique resource namespace.</param>
/// <param name="AccessToken">The run-scoped development bearer token; never written to output.</param>
internal sealed record RunHandoff(string RunId, Uri EventStore, string Tenant, string ResourceNamespace, string AccessToken)
{
    /// <summary>
    /// Reads the handoff, failing instead of skipping when the runner did not supply it.
    /// </summary>
    /// <returns>The handoff.</returns>
    public static RunHandoff Read() => new(
        Required("HEXALITH_G4_RUN_ID"),
        new Uri(Required("HEXALITH_G4_EVENTSTORE_URL"), UriKind.Absolute),
        Required("HEXALITH_G4_TENANT"),
        Required("HEXALITH_G4_RESOURCE_NAMESPACE"),
        Required("HEXALITH_G4_ACCESS_TOKEN"));

    /// <inheritdoc />
    public override string ToString() => $"RunHandoff {{ RunId = {RunId}, AccessToken = [redacted] }}";

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"The runner handoff variable {name} is missing.");
}