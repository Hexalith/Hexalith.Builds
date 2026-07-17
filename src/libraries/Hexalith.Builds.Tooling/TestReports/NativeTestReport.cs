// <copyright file="NativeTestReport.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestReports;

/// <summary>
/// Captures the normalized counters and hash from a native VSTest or Microsoft Testing Platform TRX report.
/// </summary>
/// <param name="FileName">The metadata-only report file name.</param>
/// <param name="Sha256">The SHA-256 content fingerprint.</param>
/// <param name="Total">The total matching test count.</param>
/// <param name="Passed">The passed test count.</param>
/// <param name="Failed">The failed, errored, timed-out, or aborted test count.</param>
/// <param name="Skipped">The not-executed or inconclusive test count.</param>
public sealed record NativeTestReport(
    string FileName,
    string Sha256,
    int Total,
    int Passed,
    int Failed,
    int Skipped);