// <copyright file="CompositionControlResponse.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The metadata-only result of a run-scoped AppHost resource command.
/// </summary>
/// <param name="Schema">The control protocol schema.</param>
/// <param name="RunId">The owning run.</param>
/// <param name="Nonce">The request identity.</param>
/// <param name="Command">The executed command.</param>
/// <param name="Success">Whether the requested resource reached its required state.</param>
/// <param name="FailureStep">The metadata-only failing step, if any.</param>
public sealed record CompositionControlResponse(string Schema, string RunId, string Nonce, string Command, bool Success, string? FailureStep = null)
{
    /// <summary>Gets the response path in the private run workspace.</summary>
    /// <param name="workspace">The private run workspace.</param>
    /// <returns>The response path.</returns>
    public static string PathFor(string workspace) => Path.Combine(workspace, "resource-control-response.json");
}
