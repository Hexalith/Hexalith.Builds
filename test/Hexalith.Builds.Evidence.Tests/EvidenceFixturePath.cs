// <copyright file="EvidenceFixturePath.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

/// <summary>
/// Resolves repository-contained evidence fixtures from test output directories.
/// </summary>
internal static class EvidenceFixturePath
{
    /// <summary>
    /// Resolves one path beneath the evidence fixture root.
    /// </summary>
    /// <param name="relativePath">The slash-delimited path beneath the fixture root.</param>
    /// <returns>The absolute fixture path.</returns>
    /// <exception cref="DirectoryNotFoundException">Thrown when the repository fixture root cannot be located.</exception>
    public static string Get(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string fixtureRoot = Path.Combine(directory.FullName, "test", "fixtures", "evidence");
            if (Directory.Exists(fixtureRoot))
            {
                return Path.Combine(fixtureRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("The repository evidence fixture root was not found.");
    }
}