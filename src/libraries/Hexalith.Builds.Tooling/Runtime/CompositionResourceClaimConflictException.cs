// <copyright file="CompositionResourceClaimConflictException.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Refuses implicit reuse of an exclusive resource managed by another run.</summary>
/// <param name="resourceId">The blocked exclusive resource.</param>
/// <param name="ownerRunId">The owning run, or unknown when its claim cannot be read.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "An exclusive-resource refusal must always identify the resource and its blocking owner.")]
public sealed class CompositionResourceClaimConflictException(string resourceId, string ownerRunId)
    : IOException($"Exclusive resource '{resourceId}' is held by run '{ownerRunId}'.")
{
    /// <summary>Gets the blocking owner.</summary>
    public string OwnerRunId { get; } = ownerRunId;
}
