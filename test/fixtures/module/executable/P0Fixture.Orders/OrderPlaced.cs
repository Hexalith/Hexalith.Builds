// <copyright file="OrderPlaced.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Orders;

using Hexalith.EventStore.Contracts.Events;

/// <summary>
/// Records that an order line was placed.
/// </summary>
/// <param name="Quantity">The ordered quantity.</param>
/// <param name="Sequence">The one-based domain sequence folded from the prior state.</param>
public sealed record OrderPlaced(int Quantity, int Sequence) : IEventPayload;