// <copyright file="CompositionProcessResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The bounded result of one runner-owned helper process.
/// </summary>
/// <param name="Started">A value indicating whether the process started.</param>
/// <param name="ExitCode">The exit code, or -1 when the process did not complete.</param>
/// <param name="Output">The bounded standard output text.</param>
/// <param name="TimedOut">A value indicating whether the bound elapsed.</param>
public sealed record CompositionProcessResult(bool Started, int ExitCode, string Output, bool TimedOut)
{
    /// <summary>Gets the finite launch failure metadata when no process started.</summary>
    public CompositionProcessStartFailure? StartFailure { get; init; }

    /// <summary>Gets a value indicating whether standard output exceeded the capture bound.</summary>
    public bool OutputTruncated { get; init; }

    /// <summary>Gets a bounded tail of standard error for actionable process failures.</summary>
    public string ErrorOutput { get; init; } = string.Empty;

    /// <summary>Gets processes whose tree kill could not be confirmed within the stop allowance.</summary>
    public IReadOnlyList<int> SurvivingProcessIds { get; init; } = [];
}
