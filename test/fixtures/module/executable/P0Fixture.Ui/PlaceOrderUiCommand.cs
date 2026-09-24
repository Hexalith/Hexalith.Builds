// <copyright file="PlaceOrderUiCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Ui;

using Hexalith.FrontComposer.Contracts.Attributes;

/// <summary>
/// Places an order line.
/// </summary>
[Command]
[BoundedContext("p0-orders", DisplayLabel = "P0 Orders")]
public sealed class PlaceOrderUiCommand
{
    /// <summary>
    /// Gets or sets the requested quantity.
    /// </summary>
    public int Quantity { get; set; }
}