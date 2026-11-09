// <copyright file="Calculator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Sample;

/// <summary>
/// Provides the small product exercised by every sample test lane.
/// </summary>
public static class Calculator
{
    /// <summary>
    /// Adds two numbers with overflow checking.
    /// </summary>
    /// <param name="left">The first number.</param>
    /// <param name="right">The second number.</param>
    /// <returns>The sum of both numbers.</returns>
    public static int Add(int left, int right) => checked(left + right);
}
