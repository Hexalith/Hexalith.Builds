// <copyright file="ManifestPathValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;

/// <summary>
/// Validates canonical repository-relative paths used by module manifests.
/// </summary>
public static class ManifestPathValidator
{
    /// <summary>
    /// Resolves the repository root for a manifest, falling back to its directory for isolated fixtures.
    /// </summary>
    /// <param name="manifestPath">The full manifest path.</param>
    /// <returns>The resolved repository root.</returns>
    public static string FindRepositoryRoot(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        DirectoryInfo? directory = new(Path.GetDirectoryName(manifestPath)!);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, ".git")) ||
                File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetDirectoryName(manifestPath)!;
    }

    /// <summary>
    /// Validates a repository-relative path and verifies that the target exists.
    /// </summary>
    /// <param name="path">The manifest path value.</param>
    /// <param name="repositoryRoot">The repository root.</param>
    /// <param name="field">The field identity.</param>
    /// <param name="diagnostics">The destination diagnostics.</param>
    /// <returns>The resolved full path when valid; otherwise, <see langword="null"/>.</returns>
    public static string? ValidateExistingFile(
        string? path,
        string repositoryRoot,
        string field,
        ICollection<ToolDiagnostic> diagnostics)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        ArgumentNullException.ThrowIfNull(diagnostics);

        if (string.IsNullOrWhiteSpace(path))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM015",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "A required manifest value is missing.",
                field,
                "Supply a canonical repository-relative path."));
            return null;
        }

        if (ContainsPlaceholder(path))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM006",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "The manifest contains an unresolved placeholder.",
                field,
                "Resolve placeholders before invoking the runner."));
            return null;
        }

        if (ManifestSecretDetector.ContainsSecret(path))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM007",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "The manifest contains a prohibited secret-bearing value.",
                field,
                "Remove secret-bearing values from the manifest."));
            return null;
        }

        if (Path.IsPathRooted(path) ||
            path.Contains('\\', StringComparison.Ordinal) ||
            path.Split('/', StringSplitOptions.None).Any(segment =>
                segment.Length == 0 || string.Equals(segment, ".", StringComparison.Ordinal) || string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM004",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "A manifest path is not canonical and repository-relative.",
                field,
                "Use a forward-slash repository-relative path without path traversal."));
            return null;
        }

        string lexicalPath;
        try
        {
            lexicalPath = Path.GetFullPath(Path.Combine(repositoryRoot, path.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            lexicalPath = string.Empty;
        }
        catch (NotSupportedException)
        {
            lexicalPath = string.Empty;
        }

        if (!File.Exists(lexicalPath))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM005",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "A manifest path does not resolve to a readable repository file.",
                field,
                "Supply an existing file beneath the repository root."));
            return null;
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(repositoryRoot, path, out string fullPath))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXM004",
                ToolPhase.Manifest,
                ToolFailureCategory.Manifest,
                "A manifest path is not canonical and repository-relative.",
                field,
                "Use a readable file physically contained by the repository root."));
            return null;
        }

        return fullPath;
    }

    /// <summary>
    /// Determines whether a string contains a manifest placeholder.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>A value indicating whether a placeholder was found.</returns>
    public static bool ContainsPlaceholder(string value) =>
        !string.IsNullOrEmpty(value) &&
        (value.Contains("${", StringComparison.Ordinal) ||
         value.Contains("{{", StringComparison.Ordinal) ||
         value.Contains('%', StringComparison.Ordinal));
}
