// <copyright file="AdministratorRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Records an action authorized by independently enrolled exclusive Administrator custody.
/// </summary>
/// <param name="Action">The authorized Administrator action.</param>
/// <param name="Target">The action target identity.</param>
/// <param name="Attempt">The exact approved attempt, when applicable.</param>
/// <param name="ObservedStop">The stop content approved for clearance, when applicable.</param>
/// <param name="ExpectedStopRevision">The observed stop revision for compare-and-set clearance.</param>
/// <param name="Predecessor">The predecessor Administrator authority record, where applicable.</param>
/// <param name="Reason">The metadata-only reason identifier.</param>
/// <param name="CauseDispositions">The reviewed per-cause dispositions.</param>
/// <param name="NamedRecovery">The separately planned retained-data recovery, when required.</param>
public sealed record AdministratorRecord(
    string Action,
    string Target,
    RecordReference? Attempt,
    RecordReference? ObservedStop,
    long? ExpectedStopRevision,
    RecordReference? Predecessor,
    string Reason,
    ImmutableDictionary<string, string> CauseDispositions,
    NamedRecovery? NamedRecovery);