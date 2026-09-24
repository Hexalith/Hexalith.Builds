// <copyright file="OrderState.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Orders;

/// <summary>
/// The fixture order state, folded from <see cref="OrderPlaced"/> events.
/// </summary>
public sealed class OrderState
{
    /// <summary>
    /// Gets the number of placed order lines.
    /// </summary>
    public int LineCount { get; private set; }

    /// <summary>
    /// Gets the total placed quantity.
    /// </summary>
    public int TotalQuantity { get; private set; }

    /// <summary>
    /// Applies one placed order line.
    /// </summary>
    /// <param name="e">The placed order line event.</param>
    public void Apply(OrderPlaced e)
    {
        ArgumentNullException.ThrowIfNull(e);
        LineCount++;
        TotalQuantity += e.Quantity;
    }
}