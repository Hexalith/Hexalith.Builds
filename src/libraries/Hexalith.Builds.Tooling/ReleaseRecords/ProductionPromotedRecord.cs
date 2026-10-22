// <copyright file="ProductionPromotedRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Binds production promotion to the issuing attempt and active profile.
/// </summary>
/// <param name="Release">The release record.</param>
/// <param name="IssuingAttempt">The production attempt issuing this record.</param>
/// <param name="Qualification">The production qualification record.</param>
/// <param name="ProfileDigest">The active production profile digest.</param>
public sealed record ProductionPromotedRecord(
    RecordReference Release,
    RecordReference IssuingAttempt,
    RecordReference Qualification,
    string ProfileDigest);