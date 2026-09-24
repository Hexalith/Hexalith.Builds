// <copyright file="ReserveStockUiCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Ui;

using Hexalith.FrontComposer.Contracts.Attributes;

/// <summary>
/// Reserves stock.
/// </summary>
[Command]
[BoundedContext("p0-inventory", DisplayLabel = "P0 Inventory")]
public sealed class ReserveStockUiCommand
{
    /// <summary>
    /// Gets or sets the requested quantity.
    /// </summary>
    public int Quantity { get; set; }
}