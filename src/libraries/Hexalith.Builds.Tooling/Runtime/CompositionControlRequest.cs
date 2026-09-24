// <copyright file="CompositionControlRequest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// A metadata-only, run-scoped AppHost resource command.
/// </summary>
/// <param name="Schema">The control protocol schema.</param>
/// <param name="RunId">The owning run.</param>
/// <param name="Nonce">The request identity.</param>
/// <param name="Command">The allowed resource command.</param>
public sealed record CompositionControlRequest(string Schema, string RunId, string Nonce, string Command)
{
    /// <summary>The supported control protocol.</summary>
    public const string SupportedSchema = "hexalith.g4-resource-control.v1";

    /// <summary>Gets the request path in the private run workspace.</summary>
    /// <param name="workspace">The private run workspace.</param>
    /// <returns>The request path.</returns>
    public static string PathFor(string workspace) => Path.Combine(workspace, "resource-control-request.json");
}
