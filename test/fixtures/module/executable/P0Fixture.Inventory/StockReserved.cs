// <copyright file="StockReserved.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Inventory;

using Hexalith.EventStore.Contracts.Events;

/// <summary>
/// Records that stock was reserved.
/// </summary>
/// <param name="Quantity">The reserved quantity.</param>
/// <param name="Sequence">The one-based domain sequence folded from the prior state.</param>
public sealed record StockReserved(int Quantity, int Sequence) : IEventPayload;