// <copyright file="OrdersUiMarker.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.Ui;

using Hexalith.FrontComposer.Contracts.Attributes;

/// <summary>
/// FrontComposer marker for the p0-orders bounded context.
/// </summary>
[BoundedContext("p0-orders", DisplayLabel = "P0 Orders")]
public sealed class OrdersUiMarker;