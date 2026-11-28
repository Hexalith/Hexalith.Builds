// <copyright file="NativeBuildComponent.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Identifies one complete runnable Release output.</summary>
/// <param name="Role">The stable purpose, scoped to one declared profile or the Platform model.</param>
/// <param name="Entry">The DLL relative to the component root.</param>
/// <param name="Project">The producer's selected project identity.</param>
/// <param name="ProjectSha256">The selected project digest.</param>
/// <param name="Framework">The restored target framework.</param>
/// <param name="RuntimeIdentifier">The restored runtime identifier, empty for portable output.</param>
/// <param name="AssetsSha256">The producer's restore assets digest.</param>
/// <param name="Packages">Resolved package names, versions and content hashes.</param>
/// <param name="Files">The complete relative file inventory and SHA-256 digests.</param>
public sealed record NativeBuildComponent(
    string Role,
    string Entry,
    string Project,
    string ProjectSha256,
    string Framework,
    string RuntimeIdentifier,
    string AssetsSha256,
    IReadOnlyDictionary<string, NativeBuildPackageIdentity> Packages,
    IReadOnlyDictionary<string, string> Files);
