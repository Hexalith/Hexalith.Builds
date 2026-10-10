// <copyright file="FilesystemPathRules.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Filesystem;

/// <summary>Uses the containing filesystem's path casing rather than an operating-system guess.</summary>
internal static class FilesystemPathRules
{
    /// <summary>Gets the comparison used for paths below an existing directory.</summary>
    /// <param name="directory">The containing directory.</param>
    /// <returns>The filesystem's path comparison.</returns>
    internal static StringComparison Comparison(string directory)
    {
        string? current = Path.GetFullPath(directory);
        while (current is not null)
        {
            if (!Directory.Exists(current))
            {
                current = Path.GetDirectoryName(current);
                continue;
            }

            try
            {
                string[] entries = [.. Directory.EnumerateFileSystemEntries(current)];
                foreach (string entry in entries)
                {
                    string name = Path.GetFileName(entry);
                    int letter = -1;
                    for (int index = 0; index < name.Length; index++)
                    {
                        if (char.IsAsciiLetter(name[index]))
                        {
                            letter = index;
                            break;
                        }
                    }

                    if (letter < 0 || (!File.Exists(entry) && !Directory.Exists(entry)))
                    {
                        continue;
                    }

                    char replacement = char.IsUpper(name[letter]) ? char.ToLowerInvariant(name[letter]) : char.ToUpperInvariant(name[letter]);
                    string alternateName = name[..letter] + replacement + name[(letter + 1)..];
                    if (entries.Any(candidate => string.Equals(Path.GetFileName(candidate), alternateName, StringComparison.Ordinal)))
                    {
                        return StringComparison.Ordinal;
                    }

                    string alternate = Path.Combine(current, alternateName);
                    return File.Exists(alternate) || Directory.Exists(alternate)
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal;
                }

                return StringComparison.Ordinal;
            }
            catch (IOException)
            {
                return StringComparison.Ordinal;
            }
            catch (UnauthorizedAccessException)
            {
                return StringComparison.Ordinal;
            }
        }

        return StringComparison.Ordinal;
    }
}
