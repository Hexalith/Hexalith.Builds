// <copyright file="CalculatorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Sample.WindowsTests;

using Xunit;

/// <summary>
/// Exercises the sample product from the WindowsTests lane.
/// </summary>
public sealed class CalculatorTests
{
    /// <summary>
    /// Verifies the product through this lane's execution boundary.
    /// </summary>
    [Fact]
    public void UsesTheProductOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.Equal(42, Calculator.Add(19, 23));
    }

    /// <summary>Proves the shared prepare action checked out a tracked path beyond MAX_PATH.</summary>
    [Fact]
    public void ChecksOutTrackedLongPathsOnWindows()
    {
        Assert.True(OperatingSystem.IsWindows());
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "fixtures", "windows-checkout")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        string path = Path.Combine(directory.FullName, "fixtures", "windows-checkout", "segment-0-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "segment-1-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "segment-2-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "segment-3-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "segment-4-xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "checkout-proof.txt");
        Assert.True(path.Length > 260);
        Assert.Equal("tracked long-path checkout proof", File.ReadAllText(path).Trim());
    }
}
