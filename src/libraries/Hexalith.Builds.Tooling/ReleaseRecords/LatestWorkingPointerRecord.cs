// <copyright file="LatestWorkingPointerRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Names the terminal working baseline at a monotonic environment epoch.
/// </summary>
/// <param name="Release">The working release record.</param>
/// <param name="TerminalAttempt">The working terminal attempt.</param>
/// <param name="Epoch">The strictly advancing environment epoch.</param>
public sealed record LatestWorkingPointerRecord(
    RecordReference Release,
    RecordReference TerminalAttempt,
    long Epoch);