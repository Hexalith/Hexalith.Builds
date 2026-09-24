// <copyright file="StockAggregate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Inventory;

using Hexalith.EventStore.Client.Aggregates;
using Hexalith.EventStore.Client.Attributes;
using Hexalith.EventStore.Contracts.Results;

/// <summary>
/// The fixture stock aggregate: a pure command-to-event function over <see cref="StockState"/>.
/// </summary>
[EventStoreDomain("p0-inventory")]
public sealed class StockAggregate : EventStoreAggregate<StockState>
{
    /// <summary>
    /// Handles a stock reservation.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="state">The current state, or null for a new aggregate.</param>
    /// <returns>The emitted events.</returns>
    public static DomainResult Handle(ReserveStock command, StockState? state)
    {
        ArgumentNullException.ThrowIfNull(command);
        int sequence = (state?.ReservationCount ?? 0) + 1;
        return DomainResult.Success([new StockReserved(command.Quantity, sequence)]);
    }
}