// <copyright file="LivePersistedEvent.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

/// <summary>
/// One persisted event read directly from the run-scoped state store.
/// </summary>
/// <param name="EventTypeName">The event type.</param>
/// <param name="SequenceNumber">The aggregate sequence number.</param>
/// <param name="TenantId">The tenant.</param>
/// <param name="Domain">The domain.</param>
/// <param name="AggregateId">The aggregate identity.</param>
/// <param name="Quantity">The decoded payload quantity.</param>
/// <param name="FoldedSequence">The decoded payload sequence folded by the module from its rehydrated state.</param>
internal sealed record LivePersistedEvent(
    string EventTypeName,
    long SequenceNumber,
    string TenantId,
    string Domain,
    string AggregateId,
    int Quantity,
    int FoldedSequence);