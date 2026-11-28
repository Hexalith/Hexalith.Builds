// <copyright file="NativeBuildInventory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Checks an exact regular-file tree and copies the same bytes it hashes.</summary>
internal static class NativeBuildInventory
{
    private const int _maxFiles = 16384;

    /// <summary>Resolves a platform-independent relative path without allowing traversal.</summary>
    /// <param name="root">The validated base directory.</param>
    /// <param name="path">The relative path using slash delimiters.</param>
    /// <returns>The combined absolute path.</returns>
    /// <exception cref="InvalidDataException">The relative path is unsafe.</exception>
    internal static string Resolve(string root, string path)
    {
        bool unsafePath = string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':')
            || path.Split('/').Any(segment => segment is "" or "." or ".." || segment.Any(char.IsControl));
        return unsafePath
            ? throw new InvalidDataException("Prepared native build contains an unsafe relative path.")
            : Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>Rejects linked, missing and mis-typed paths, including their ancestors.</summary>
    /// <param name="path">An absolute existing path.</param>
    /// <param name="directory">Whether the selected leaf is a directory.</param>
    /// <exception cref="InvalidDataException">The path is missing, linked or does not have the expected type.</exception>
    internal static void RequireReal(string path, bool directory)
    {
        FileSystemInfo item = directory ? new DirectoryInfo(path) : new FileInfo(path);
        for (FileSystemInfo? current = item; current is not null; current = current switch
        {
            FileInfo file => file.Directory,
            DirectoryInfo folder => folder.Parent,
            _ => null,
        })
        {
            current.Refresh();
            if (!current.Exists || current.LinkTarget is not null || (current.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            {
                throw new InvalidDataException("Prepared native build contains a missing or linked path.");
            }
        }
    }

    /// <summary>Checks a complete directory, optionally copying the verified stream to a private destination.</summary>
    /// <param name="source">The source directory.</param>
    /// <param name="expected">The previously sealed inventory, or null when preparing it.</param>
    /// <param name="destination">A fresh private destination, or null for verification only.</param>
    /// <param name="cancellationToken">The original operation token.</param>
    /// <returns>The verified inventory.</returns>
    /// <exception cref="InvalidDataException">The file inventory differs or is unsafe.</exception>
    internal static IReadOnlyDictionary<string, string> VerifyDirectory(string source, IReadOnlyDictionary<string, string>? expected, string? destination, CancellationToken cancellationToken)
    {
        RequireReal(source, directory: true);
        if (destination is not null)
        {
            if (Path.Exists(destination) || new DirectoryInfo(destination).LinkTarget is not null)
            {
                throw new InvalidDataException("Prepared native build destination is already occupied.");
            }

            CompositionWorkspace.CreatePrivateDirectory(destination);
            RequireReal(destination, directory: true);
        }

        SortedDictionary<string, string> files = [];
        HashSet<string> portablePaths = [with(StringComparer.OrdinalIgnoreCase)];
        Visit(source, 0);
        return files.Count == 0 || (expected is not null && expected.Count != files.Count)
            ? throw new InvalidDataException("Prepared native build file inventory is incomplete.")
            : files;

        void Visit(string directory, int depth)
        {
            if (depth > 32)
            {
                throw new InvalidDataException("Prepared native build directory nesting exceeds its limit.");
            }

            foreach (FileSystemInfo item in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(source, item.FullName).Replace(Path.DirectorySeparatorChar, '/');
                _ = Resolve(source, relative);
                if (portablePaths.Count >= _maxFiles || !portablePaths.Add(relative)
                    || item.LinkTarget is not null || (item.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new InvalidDataException("Prepared native build contains linked, ambiguous or excessive paths.");
                }

                if (item is DirectoryInfo)
                {
                    if (expected?.Keys.Any(path => path.StartsWith(relative + "/", StringComparison.Ordinal)) == false)
                    {
                        throw new InvalidDataException("Prepared native build has an unrecorded directory.");
                    }

                    Visit(item.FullName, depth + 1);
                    continue;
                }

                string? recorded = null;
                if (expected is not null && !expected.TryGetValue(relative, out recorded))
                {
                    throw new InvalidDataException("Prepared native build has an unrecorded file.");
                }

                string? copy = destination is null ? null : Resolve(destination, relative);
                string actual = HashFile(item.FullName, cancellationToken, copy);
                if (recorded is not null && actual != recorded)
                {
                    throw new InvalidDataException("Prepared native build artifact SHA-256 differs from the producer receipt.");
                }

                files.Add(relative, actual);
            }
        }
    }

    /// <summary>Hashes the checked file, optionally writing the same bytes to a fresh private file.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="cancellationToken">The operation token.</param>
    /// <param name="copy">A new file path inside the already owned destination.</param>
    /// <returns>The lowercase SHA-256 digest.</returns>
    /// <exception cref="InvalidDataException">The file exceeds the supported size bound.</exception>
    internal static string HashFile(string path, CancellationToken cancellationToken, string? copy = null)
    {
        using FileStream input = OpenFile(path);
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (copy is not null)
        {
            CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(copy)!);
        }

        using FileStream? output = copy is null ? null : new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        byte[] buffer = new byte[65536];
        long length = 0;
        int count;
        while ((count = input.Read(buffer)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((length += count) > 536_870_912)
            {
                throw new InvalidDataException("Prepared native build artifact exceeds its size limit.");
            }

            digest.AppendData(buffer, 0, count);
            output?.Write(buffer, 0, count);
        }

        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    /// <summary>Opens a verified artifact without following its final path or waiting on special files.</summary>
    /// <param name="path">The file path.</param>
    /// <returns>The held regular-file stream.</returns>
    /// <exception cref="InvalidDataException">The file is linked, missing, or not a regular artifact.</exception>
    internal static FileStream OpenFile(string path)
    {
        RequireReal(path, directory: false);
        FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path);
        try
        {
            return stream.CanSeek && NativeBuildFileStatus.IsRegular(stream.SafeFileHandle)
                ? stream
                : throw new InvalidDataException("Prepared native build contains a linked or nonregular file.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Hashes the same bytes retained for parsing or copying.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The lowercase SHA-256 digest.</returns>
    internal static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
