// <copyright file="CompositionReadiness.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The metadata-only readiness document written by the AppHost once every resource is healthy.
/// </summary>
/// <param name="Schema">The readiness schema identity.</param>
/// <param name="RunId">The run identity the AppHost composed.</param>
/// <param name="EventStoreEndpoint">The EventStore HTTP endpoint.</param>
/// <param name="UiEndpoint">The FrontComposer UI host HTTP endpoint, when the run declares UI markers.</param>
/// <param name="Resources">The deterministically ordered resource readiness records.</param>
public sealed record CompositionReadiness(
    string Schema,
    string RunId,
    Uri EventStoreEndpoint,
    Uri? UiEndpoint,
    IReadOnlyList<CompositionResourceReadiness> Resources)
{
    /// <summary>
    /// The supported readiness schema identity.
    /// </summary>
    public const string SupportedSchema = "hexalith.g4-run-readiness.v1";
}