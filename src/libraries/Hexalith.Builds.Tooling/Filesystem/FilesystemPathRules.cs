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

            string probe = Path.Combine(current, ".hexalith-case-" + Guid.NewGuid().ToString("N"));
            try
            {
                File.WriteAllText(probe, string.Empty);

                string alternate = Path.Combine(current, ".Hexalith-case-" + Path.GetFileName(probe)[".hexalith-case-".Length..]);
                return File.Exists(alternate) ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            }
            catch (IOException)
            {
                return StringComparison.Ordinal;
            }
            catch (UnauthorizedAccessException)
            {
                return StringComparison.Ordinal;
            }
            finally
            {
                if (File.Exists(probe))
                {
                    File.Delete(probe);
                }
            }
        }

        return StringComparison.Ordinal;
    }
}
