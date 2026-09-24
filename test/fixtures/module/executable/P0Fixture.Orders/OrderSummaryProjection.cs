// <copyright file="OrderSummaryProjection.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Orders;

using System.Text.Json;

using Hexalith.EventStore.Contracts.Projections;
using Hexalith.EventStore.DomainService;

/// <summary>
/// Rebuilds the persisted order summary from the complete ordered event stream.
/// </summary>
public sealed class OrderSummaryProjection : IDomainProjectionHandler
{
    /// <inheritdoc />
    public string Domain => "p0-orders";

    /// <inheritdoc />
    public ProjectionResponse Project(ProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long sequence = 0;
        int total = 0;
        foreach (ProjectionEventDto item in request.Events)
        {
            if (!string.Equals(item.EventTypeName, typeof(OrderPlaced).FullName, StringComparison.Ordinal)
                || item.SequenceNumber != sequence + 1)
            {
                throw new InvalidOperationException("The order projection requires contiguous order events.");
            }

            OrderPlaced placed = JsonSerializer.Deserialize<OrderPlaced>(item.Payload)
                ?? throw new InvalidOperationException("The order projection event payload is absent.");
            if (placed.Sequence != item.SequenceNumber)
            {
                throw new InvalidOperationException("The order projection event sequence is inconsistent.");
            }

            sequence = item.SequenceNumber;
            total = checked(total + placed.Quantity);
        }

        return new ProjectionResponse(
            "p0-orders-summary",
            JsonSerializer.SerializeToElement(new
            {
                request.TenantId,
                request.AggregateId,
                Sequence = sequence,
                Count = request.Events.Length,
                TotalQuantity = total,
            }));
    }
}
