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

            string name = Path.GetFileName(current.TrimEnd(Path.DirectorySeparatorChar));
            int index = -1;
            for (int position = 0; position < name.Length; position++)
            {
                if (char.IsAsciiLetter(name[position]))
                {
                    index = position;
                    break;
                }
            }

            if (index >= 0)
            {
                char replacement = char.IsUpper(name[index]) ? char.ToLowerInvariant(name[index]) : char.ToUpperInvariant(name[index]);
                string parent = Path.GetDirectoryName(current)!;
                string alternate = Path.Combine(parent, name[..index] + replacement + name[(index + 1)..]);
                bool caseVariantsExist;
                try
                {
                    caseVariantsExist = Directory.EnumerateFileSystemEntries(parent)
                        .Count(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)) > 1;
                }
                catch (IOException)
                {
                    return StringComparison.Ordinal;
                }
                catch (UnauthorizedAccessException)
                {
                    return StringComparison.Ordinal;
                }

                return !caseVariantsExist && Directory.Exists(alternate) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            }

            current = Path.GetDirectoryName(current);
        }

        return OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    }
}
