// <copyright file="CatalogVersionSnapshot.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Isolated versions from the Platform-pinned Builds catalog.</summary>
/// <param name="MappingHash">The owning source mapping hash.</param>
/// <param name="CatalogCommit">The pinned Builds commit.</param>
/// <param name="CatalogFileHash">The SHA-256 hash of the pinned catalog file.</param>
/// <param name="PinnedFiles">Hashes of the catalog and every relative import read from the pinned tree.</param>
/// <param name="Versions">Default package IDs and their pinned versions.</param>
/// <param name="ProjectVersions">Versions for projects named in catalog conditions.</param>
/// <param name="Hash">The snapshot content hash.</param>
public sealed record CatalogVersionSnapshot(
    string MappingHash,
    string CatalogCommit,
    string CatalogFileHash,
    IReadOnlyDictionary<string, string> PinnedFiles,
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ProjectVersions,
    string Hash)
{
    /// <summary>Computes the snapshot content hash.</summary>
    /// <param name="mappingHash">The mapping hash.</param>
    /// <param name="catalogCommit">The pinned commit.</param>
    /// <param name="catalogFileHash">The pinned file hash.</param>
    /// <param name="pinnedFiles">Hashes of all materialized pinned files.</param>
    /// <param name="versions">The default evaluated versions.</param>
    /// <param name="projectVersions">The project-specific evaluated versions.</param>
    /// <returns>A lowercase SHA-256 hash.</returns>
    public static string ComputeHash(
        string mappingHash,
        string catalogCommit,
        string catalogFileHash,
        IReadOnlyDictionary<string, string> pinnedFiles,
        IReadOnlyDictionary<string, string> versions,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> projectVersions) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            MappingHash = mappingHash,
            CatalogCommit = catalogCommit,
            CatalogFileHash = catalogFileHash,
            PinnedFiles = pinnedFiles.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            Versions = versions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase),
            ProjectVersions = projectVersions.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new { pair.Key, Versions = pair.Value.OrderBy(version => version.Key, StringComparer.OrdinalIgnoreCase) }),
        })));
}
