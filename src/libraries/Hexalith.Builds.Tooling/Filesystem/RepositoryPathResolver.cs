// <copyright file="RepositoryPathResolver.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Filesystem;

/// <summary>
/// Resolves canonical repository-relative paths without permitting symbolic-link escape.
/// </summary>
internal static class RepositoryPathResolver
{
    /// <summary>
    /// Resolves an existing file beneath a repository root after following existing links.
    /// </summary>
    /// <param name="repositoryRoot">The lexical repository root.</param>
    /// <param name="relativePath">The already syntax-validated relative path.</param>
    /// <param name="resolvedPath">The resolved physical file path.</param>
    /// <returns><see langword="true"/> when the file remains beneath the resolved root.</returns>
    public static bool TryResolveExistingFile(
        string repositoryRoot,
        string relativePath,
        out string resolvedPath) =>
        TryResolvePath(repositoryRoot, relativePath, requireExisting: true, requireFile: true, out resolvedPath);

    /// <summary>
    /// Resolves a path beneath a repository root after following all existing links.
    /// </summary>
    /// <param name="repositoryRoot">The lexical repository root.</param>
    /// <param name="relativePath">The already syntax-validated relative path.</param>
    /// <param name="resolvedPath">The resolved physical path.</param>
    /// <returns><see langword="true"/> when the path remains beneath the resolved root.</returns>
    public static bool TryResolvePathWithinRoot(
        string repositoryRoot,
        string relativePath,
        out string resolvedPath) =>
        TryResolvePath(repositoryRoot, relativePath, requireExisting: false, requireFile: false, out resolvedPath);

    private static bool IsContainedBy(string candidatePath, string rootPath)
    {
        string relativePath = Path.GetRelativePath(rootPath, candidatePath);
        return !relativePath.Equals("..", StringComparison.Ordinal)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relativePath);
    }

    private static bool TryResolveDirectory(string path, out string resolvedPath)
    {
        try
        {
            DirectoryInfo directory = new(path);
            FileSystemInfo? target = directory.ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = target?.FullName ?? directory.FullName;
            return true;
        }
        catch (IOException)
        {
            resolvedPath = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            resolvedPath = string.Empty;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static bool TryResolveFile(string path, out string resolvedPath)
    {
        try
        {
            FileInfo file = new(path);
            FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
            resolvedPath = target?.FullName ?? file.FullName;
            return true;
        }
        catch (IOException)
        {
            resolvedPath = string.Empty;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            resolvedPath = string.Empty;
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }

    private static bool TryResolvePath(
        string repositoryRoot,
        string relativePath,
        bool requireExisting,
        bool requireFile,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        string fullRoot;
        string candidatePath;
        try
        {
            fullRoot = Path.GetFullPath(repositoryRoot);
            candidatePath = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        if (!TryResolvePhysicalPath(fullRoot, requireExisting: true, out string physicalRoot)
            || !TryResolvePhysicalPath(candidatePath, requireExisting, out string physicalCandidate)
            || !IsContainedBy(physicalCandidate, physicalRoot)
            || (requireFile && !File.Exists(physicalCandidate)))
        {
            return false;
        }

        resolvedPath = physicalCandidate;
        return true;
    }

    private static bool TryResolvePhysicalPath(string path, bool requireExisting, out string resolvedPath)
    {
        resolvedPath = string.Empty;
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }

        string? pathRoot = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(pathRoot))
        {
            return false;
        }

        string current = pathRoot;
        string[] segments = fullPath[pathRoot.Length..].Split(
            Path.DirectorySeparatorChar,
            StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < segments.Length; index++)
        {
            string candidate = Path.Combine(current, segments[index]);
            bool isLastSegment = index == segments.Length - 1;
            if (Directory.Exists(candidate))
            {
                if (!TryResolveDirectory(candidate, out current))
                {
                    return false;
                }

                continue;
            }

            if (File.Exists(candidate))
            {
                if (!isLastSegment || !TryResolveFile(candidate, out current))
                {
                    return false;
                }

                continue;
            }

            if (requireExisting)
            {
                return false;
            }

            current = candidate;
        }

        resolvedPath = current;
        return true;
    }
}