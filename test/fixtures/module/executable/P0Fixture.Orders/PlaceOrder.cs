// <copyright file="PlaceOrder.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Orders;

/// <summary>
/// Places an order line on the fixture order aggregate.
/// </summary>
/// <param name="Quantity">The positive ordered quantity.</param>
public sealed record PlaceOrder(int Quantity);