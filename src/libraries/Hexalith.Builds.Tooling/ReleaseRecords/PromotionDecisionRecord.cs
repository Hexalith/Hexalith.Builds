// <copyright file="PromotionDecisionRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Text.Json.Serialization;

/// <summary>Binds one authorized conditional action on a pending promotion decision.</summary>
/// <param name="Action">The reservation, supersession, completion or release action.</param>
/// <param name="StagingEvidence">The exact policy and target decision identity.</param>
/// <param name="Release">The exact retained release record for the target decision.</param>
/// <param name="Attempt">The exact initiating or terminal attempt revision, when applicable.</param>
/// <param name="LockEpoch">The epoch of the action's environment, when applicable.</param>
/// <param name="Reservation">The exact signed admission reservation being completed or released.</param>
/// <param name="WorkingPointer">The exact working pointer bound by completion.</param>
public sealed record PromotionDecisionRecord(
    string Action,
    StagingEvidenceBinding StagingEvidence,
    RecordReference Release,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecordReference? Attempt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? LockEpoch = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecordReference? Reservation = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RecordReference? WorkingPointer = null);