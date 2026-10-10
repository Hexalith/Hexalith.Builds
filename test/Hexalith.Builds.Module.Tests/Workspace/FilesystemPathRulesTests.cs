// <copyright file="FilesystemPathRulesTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Filesystem;

using Shouldly;

using Xunit;

/// <summary>Verifies casing checks use existing entries in the containing directory.</summary>
public sealed class FilesystemPathRulesTests
{
    /// <summary>Verifies the containing directory is probed without changing its entries or timestamp.</summary>
    [Fact]
    public void ComparisonUsesContainingDirectoryWithoutMutation()
    {
        string root = Path.Combine(Path.GetTempPath(), "hexalith-path-rules-tests", Guid.NewGuid().ToString("N"));
        string child = Path.Combine(root, "child");
        _ = Directory.CreateDirectory(child);
        try
        {
            string probe = Path.Combine(child, "known-entry.txt");
            File.WriteAllText(probe, string.Empty);
            Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddDays(-1));
            DateTime beforeWrite = Directory.GetLastWriteTimeUtc(child);
            string[] beforeEntries = [.. Directory.EnumerateFileSystemEntries(child).Order(StringComparer.Ordinal)];
            StringComparison expected = File.Exists(Path.Combine(child, "Known-entry.txt"))
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            FilesystemPathRules.Comparison(child).ShouldBe(expected);

            Directory.EnumerateFileSystemEntries(child).Order(StringComparer.Ordinal).ShouldBe(beforeEntries);
            Directory.GetLastWriteTimeUtc(child).ShouldBe(beforeWrite);
            if (expected == StringComparison.Ordinal)
            {
                File.WriteAllText(Path.Combine(child, "Known-entry.txt"), string.Empty);
                Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddDays(-1));
                beforeWrite = Directory.GetLastWriteTimeUtc(child);
                beforeEntries = [.. Directory.EnumerateFileSystemEntries(child).Order(StringComparer.Ordinal)];

                FilesystemPathRules.Comparison(child).ShouldBe(StringComparison.Ordinal);

                Directory.EnumerateFileSystemEntries(child).Order(StringComparer.Ordinal).ShouldBe(beforeEntries);
                Directory.GetLastWriteTimeUtc(child).ShouldBe(beforeWrite);
            }

            string empty = Path.Combine(root, "empty");
            _ = Directory.CreateDirectory(empty);
            FilesystemPathRules.Comparison(empty).ShouldBe(StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
