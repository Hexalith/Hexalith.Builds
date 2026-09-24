// <copyright file="CompositionRunResources.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The live resources still tagged with one run identity.
/// </summary>
/// <param name="ContainerIds">The identities of containers labelled with the run.</param>
/// <param name="ProcessIds">The identities of processes whose environment carries the run tag.</param>
/// <param name="Unverified">A value indicating whether the resources could not be listed, so leftovers must be assumed.</param>
public sealed record CompositionRunResources(IReadOnlyList<string> ContainerIds, IReadOnlyList<int> ProcessIds, bool Unverified = false)
{
    /// <summary>
    /// Gets a value indicating whether nothing tagged with the run remains. An unverifiable listing is never empty.
    /// </summary>
    public bool IsEmpty => !Unverified && ContainerIds.Count == 0 && ProcessIds.Count == 0;
}