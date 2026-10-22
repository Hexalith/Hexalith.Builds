// <copyright file="QualificationRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Extends the qualified sets for one release and environment.
/// </summary>
/// <param name="Release">The release record.</param>
/// <param name="IssuingAttempt">The attempt issuing this qualification.</param>
/// <param name="ProfileDigest">The profile actually verified.</param>
/// <param name="QualifiedEnvironmentLayer">The qualified environment-layer set extension.</param>
/// <param name="QualifiedSharedInfrastructure">The qualified shared-infrastructure set extension.</param>
/// <param name="EvidenceDigests">The verification evidence digests.</param>
public sealed record QualificationRecord(
    RecordReference Release,
    RecordReference IssuingAttempt,
    string ProfileDigest,
    ImmutableArray<string> QualifiedEnvironmentLayer,
    ImmutableArray<string> QualifiedSharedInfrastructure,
    ImmutableArray<string> EvidenceDigests);