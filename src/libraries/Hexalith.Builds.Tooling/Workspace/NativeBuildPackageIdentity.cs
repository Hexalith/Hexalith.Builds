// <copyright file="NativeBuildPackageIdentity.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Records the restored NuGet content and exact package archive as separate identities.</summary>
/// <param name="ContentHash">The NuGet normalized package SHA-512 used by restore and runtime metadata.</param>
/// <param name="ArchiveSha512">The SHA-512 of the actual archive bytes, including its signature.</param>
public sealed record NativeBuildPackageIdentity(string ContentHash, string ArchiveSha512);
