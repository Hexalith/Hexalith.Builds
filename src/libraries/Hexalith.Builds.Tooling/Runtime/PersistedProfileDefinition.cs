// <copyright file="PersistedProfileDefinition.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// A data-only qualification profile executed against the real EventStore boundary.
/// </summary>
/// <param name="Schema">The profile schema.</param>
/// <param name="Id">The profile identity.</param>
/// <param name="Modules">The two module expectations.</param>
public sealed record PersistedProfileDefinition(string Schema, string Id, IReadOnlyList<PersistedProfileModule> Modules)
{
    /// <summary>The supported profile schema.</summary>
    public const string SupportedSchema = "hexalith.g4-persisted-profile.v1";
}
