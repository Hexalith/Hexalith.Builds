// <copyright file="SourceMapping.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Filesystem;

/// <summary>The immutable dependency choice made once for a command.</summary>
/// <param name="Mode">The tool-selected mode.</param>
/// <param name="Root">The active workspace root.</param>
/// <param name="ActiveModule">The active module identity.</param>
/// <param name="Entries">The selected root-declared identities.</param>
/// <param name="HasGit">Whether the workspace has a Git superproject.</param>
public sealed record SourceMapping(
    WorkspaceMode Mode,
    string Root,
    string ActiveModule,
    IReadOnlyList<SourceMappingEntry> Entries,
    bool HasGit)
{
    /// <summary>Gets a content hash shared by the plan and all project builds.</summary>
    public string ContentHash => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        Mode,
        Root,
        ActiveModule,
        Entries,
        HasGit,
    })));

    /// <summary>Loads persisted mapping only when its content matches the command's hash.</summary>
    /// <param name="path">The persisted mapping file.</param>
    /// <param name="expectedHash">The hash selected by the command.</param>
    /// <returns>The verified mapping.</returns>
    /// <exception cref="InvalidDataException">The mapping is empty or has changed.</exception>
    public static SourceMapping LoadVerified(string path, string expectedHash)
    {
        SourceMapping mapping = JsonSerializer.Deserialize<SourceMapping>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Source mapping '{path}' is empty.");
        return !string.IsNullOrWhiteSpace(expectedHash)
            && string.Equals(mapping.ContentHash, expectedHash, StringComparison.OrdinalIgnoreCase)
                ? mapping
                : throw new InvalidDataException($"Source mapping '{path}' differs from the command-resolved hash '{expectedHash}'.");
    }

    /// <summary>Tests whether a project is inside the active module or a source identity.</summary>
    /// <param name="projectPath">The candidate project path.</param>
    /// <returns>True when mapped.</returns>
    public bool ContainsProject(string projectPath)
    {
        string fullPath = Path.GetFullPath(projectPath);
        string referencesRoot = Path.Combine(Root, "references");
        bool insideReferences = Directory.Exists(referencesRoot)
            && RepositoryPathResolver.TryResolvePathWithinRoot(referencesRoot, Path.GetRelativePath(referencesRoot, fullPath), out _);
        return Entries.Any(entry => entry.Origin == "source"
            && entry.Path is not null
            && (!HasGit || !insideReferences || entry.Path != Root)
            && RepositoryPathResolver.TryResolveExistingFile(entry.Path, Path.GetRelativePath(entry.Path, fullPath), out _));
    }

    /// <summary>Gets the MSBuild property suffix generated for an identity.</summary>
    /// <param name="identity">The module identity.</param>
    /// <returns>The suffix.</returns>
    internal static string PropertySuffix(string identity)
    {
        string name = identity.StartsWith("Hexalith.", StringComparison.OrdinalIgnoreCase)
            ? identity["Hexalith.".Length..]
            : identity;
        return new string([.. name.Where(character => char.IsAsciiLetterOrDigit(character) || character == '_')]);
    }
}
