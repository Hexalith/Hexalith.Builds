// <copyright file="ReleaseAvailableRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Binds publication availability to the successful staging attempt.
/// </summary>
/// <param name="Release">The release record.</param>
/// <param name="StagingAttempt">The terminal working staging attempt.</param>
/// <param name="ComposedImageDigest">The staged composed host image digest.</param>
public sealed record ReleaseAvailableRecord(
    RecordReference Release,
    RecordReference StagingAttempt,
    string ComposedImageDigest);