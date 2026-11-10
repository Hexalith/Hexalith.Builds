// <copyright file="CompositionTerminationItem.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>One runner observation that an attached run's owned work or environment access ended.</summary>
/// <param name="Id">The stable item identity, unique across both termination lists.</param>
/// <param name="Status">The v1 status: <c>pending</c>, <c>terminated</c>, <c>failed</c> or <c>not-applicable</c>.</param>
/// <param name="ObservedAtUtc">When the runner observed the status.</param>
/// <param name="Detail">A fixed, credential-free metadata explanation.</param>
public sealed record CompositionTerminationItem(string Id, string Status, DateTimeOffset ObservedAtUtc, string Detail)
{
    /// <summary>Checks the closed status vocabulary and bounded metadata.</summary>
    /// <returns>Whether the item can be projected into the v1 result.</returns>
    public bool IsConsistent() => Id is { Length: > 0 and <= 256 } && Id == Id.Trim() && !Id.Any(char.IsControl)
        && Status is "pending" or "terminated" or "failed" or "not-applicable"
        && Detail is { Length: > 0 and <= 1024 } && !Detail.Any(char.IsControl);
}
