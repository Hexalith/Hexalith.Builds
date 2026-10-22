// <copyright file="PromotionStopRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Sets an environment-bound promotion stop; clearance is an Administrator action.
/// </summary>
/// <param name="Revision">The store-controlled stop revision.</param>
/// <param name="PreviousStop">The preceding stop, if any.</param>
/// <param name="Cause">The metadata-only cause identifier.</param>
/// <param name="EvidenceDigests">The access-controlled evidence digests.</param>
public sealed record PromotionStopRecord(
    long Revision,
    RecordReference? PreviousStop,
    string Cause,
    ImmutableArray<string> EvidenceDigests);