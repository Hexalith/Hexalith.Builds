// <copyright file="CompositionResourceEndpoint.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>An endpoint observed from the running orchestrator, never a proposed port.</summary>
/// <param name="Name">The resource-local endpoint name.</param>
/// <param name="Protocol">The allocated protocol.</param>
/// <param name="Uri">The allocated absolute URI.</param>
public sealed record CompositionResourceEndpoint(string Name, string Protocol, Uri Uri);
