// <copyright file="CalculatorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Sample.UnitTests;

using Xunit;

/// <summary>
/// Exercises the sample product from the UnitTests lane.
/// </summary>
public sealed class CalculatorTests
{
    /// <summary>
    /// Verifies the product through this lane's execution boundary.
    /// </summary>
    [Fact]
    public void AddsNumbers() => Assert.Equal(5, Calculator.Add(2, 3));

    /// <summary>
    /// Rejects a sum that exceeds the supported integer range.
    /// </summary>
    [Fact]
    public void RejectsOverflow() => Assert.Throws<OverflowException>(() => Calculator.Add(int.MaxValue, 1));
}
