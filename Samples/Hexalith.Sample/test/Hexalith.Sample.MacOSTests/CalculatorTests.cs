// <copyright file="CalculatorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Sample.MacOSTests;

using Xunit;

/// <summary>
/// Exercises the sample product after shared preparation on macOS.
/// </summary>
public sealed class CalculatorTests
{
    /// <summary>
    /// Verifies that the built product executes on the requested platform.
    /// </summary>
    [Fact]
    public void UsesTheProductOnMacOS()
    {
        Assert.True(OperatingSystem.IsMacOS());
        Assert.Equal(42, Calculator.Add(19, 23));
    }
}
