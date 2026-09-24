// <copyright file="ReserveStock.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Inventory;

/// <summary>
/// Reserves stock on the fixture inventory aggregate.
/// </summary>
/// <param name="Quantity">The positive reserved quantity.</param>
public sealed record ReserveStock(int Quantity);