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
}
