// <copyright file="ModuleRunTestCounts.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Captures native test results when they are available to the runner.
/// </summary>
/// <param name="Reported">Whether a supported test runner supplied a report.</param>
/// <param name="Total">The total matching test count.</param>
/// <param name="Passed">The passed test count.</param>
/// <param name="Failed">The failed test count.</param>
/// <param name="Skipped">The skipped test count.</param>
public sealed record ModuleRunTestCounts(bool Reported, int Total, int Passed, int Failed, int Skipped);