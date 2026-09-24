// <copyright file="OrderAggregate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Orders;

using Hexalith.EventStore.Client.Aggregates;
using Hexalith.EventStore.Client.Attributes;
using Hexalith.EventStore.Contracts.Results;

/// <summary>
/// The fixture order aggregate: a pure command-to-event function over <see cref="OrderState"/>.
/// </summary>
[EventStoreDomain("p0-orders")]
public sealed class OrderAggregate : EventStoreAggregate<OrderState>
{
    /// <summary>
    /// Handles an order placement.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="state">The current state, or null for a new aggregate.</param>
    /// <returns>The emitted events.</returns>
    public static DomainResult Handle(PlaceOrder command, OrderState? state)
    {
        ArgumentNullException.ThrowIfNull(command);
        int sequence = (state?.LineCount ?? 0) + 1;
        return DomainResult.Success([new OrderPlaced(command.Quantity, sequence)]);
    }
}