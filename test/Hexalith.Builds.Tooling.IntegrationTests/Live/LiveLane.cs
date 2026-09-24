// <copyright file="LiveLane.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

/// <summary>
/// Names the serial live lane: every live run owns real containers, Dapr processes, and ports, so all live
/// test classes share one xUnit collection and never run concurrently with each other.
/// </summary>
internal static class LiveLane
{
    /// <summary>
    /// The shared collection name.
    /// </summary>
    public const string Name = "G4 live composition";
}