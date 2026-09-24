// <copyright file="PersistedProfileModule.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Declares one domain's expected event and read-model state in a persisted profile.
/// </summary>
/// <param name="ModuleId">The manifest module identity.</param>
/// <param name="CommandType">The accepted command type.</param>
/// <param name="EventType">The emitted event type.</param>
/// <param name="ProjectionType">The persisted projection type.</param>
/// <param name="InitialQuantity">The first command quantity.</param>
/// <param name="RetryQuantity">The later distinct command quantity.</param>
public sealed record PersistedProfileModule(
    string ModuleId,
    string CommandType,
    string EventType,
    string ProjectionType,
    int InitialQuantity,
    int RetryQuantity);
