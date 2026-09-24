// <copyright file="StockState.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Inventory;

/// <summary>
/// The fixture stock state, folded from <see cref="StockReserved"/> events.
/// </summary>
public sealed class StockState
{
    /// <summary>
    /// Gets the number of reservations.
    /// </summary>
    public int ReservationCount { get; private set; }

    /// <summary>
    /// Gets the total reserved quantity.
    /// </summary>
    public int ReservedQuantity { get; private set; }

    /// <summary>
    /// Applies one reservation.
    /// </summary>
    /// <param name="e">The reservation event.</param>
    public void Apply(StockReserved e)
    {
        ArgumentNullException.ThrowIfNull(e);
        ReservationCount++;
        ReservedQuantity += e.Quantity;
    }
}