// <copyright file="StockSummaryProjection.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Inventory;

using System.Text.Json;

using Hexalith.EventStore.Contracts.Projections;
using Hexalith.EventStore.DomainService;

/// <summary>
/// Rebuilds the persisted stock summary from the complete ordered event stream.
/// </summary>
public sealed class StockSummaryProjection : IDomainProjectionHandler
{
    /// <inheritdoc />
    public string Domain => "p0-inventory";

    /// <inheritdoc />
    public ProjectionResponse Project(ProjectionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        long sequence = 0;
        int total = 0;
        foreach (ProjectionEventDto item in request.Events)
        {
            if (!string.Equals(item.EventTypeName, typeof(StockReserved).FullName, StringComparison.Ordinal)
                || item.SequenceNumber != sequence + 1)
            {
                throw new InvalidOperationException("The stock projection requires contiguous reservation events.");
            }

            StockReserved reserved = JsonSerializer.Deserialize<StockReserved>(item.Payload)
                ?? throw new InvalidOperationException("The stock projection event payload is absent.");
            if (reserved.Sequence != item.SequenceNumber)
            {
                throw new InvalidOperationException("The stock projection event sequence is inconsistent.");
            }

            sequence = item.SequenceNumber;
            total = checked(total + reserved.Quantity);
        }

        return new ProjectionResponse(
            "p0-inventory-summary",
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
