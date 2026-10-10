// <copyright file="PlatformIdentity.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>The committed Platform identity and the source selected for composition.</summary>
/// <param name="Commit">The Platform identity commit.</param>
/// <param name="CompositionCommit">The staged Platform gitlink selected for composition.</param>
/// <param name="CheckoutCommit">The actual Platform checkout HEAD.</param>
/// <param name="PlatformPath">The Platform checkout path.</param>
/// <param name="ReleaseTag">The local release tag for a module workspace.</param>
/// <param name="CatalogCommit">The Builds commit pinned by Platform.</param>
/// <param name="ToolVersionRange">The Platform-supported tool range.</param>
/// <param name="RecordPath">The identity record location.</param>
/// <param name="IsPlatformRepository">Whether the active root is Platform.</param>
public sealed record PlatformIdentity(
    string Commit,
    string CompositionCommit,
    string? CheckoutCommit,
    string PlatformPath,
    string? ReleaseTag,
    string? CatalogCommit,
    string? ToolVersionRange,
    string RecordPath,
    bool IsPlatformRepository);
