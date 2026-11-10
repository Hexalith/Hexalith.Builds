// <copyright file="NativeTestExecutionResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.TestReports;

/// <summary>
/// The outcome of one native test step.
/// </summary>
/// <param name="Result">The fail-closed command result.</param>
/// <param name="Report">The passing native report summary, or null.</param>
/// <param name="ReportBytes">The metadata-checked native report bytes to retain, or null.</param>
internal sealed record NativeTestExecutionResult(ToolCommandResult Result, NativeTestReport? Report, byte[]? ReportBytes)
{
    /// <summary>Gets the bounded inspected detailed native observations, retained beside TRX through existing artifact custody.</summary>
    internal byte[]? ObservationBytes { get; init; }

    /// <summary>Gets bounded public routing files held before the selected child starts.</summary>
    internal IReadOnlyDictionary<string, byte[]>? RoutingCatalogSnapshot { get; init; }

    /// <summary>Gets the runner-observed terminal cause, independent of later processing time.</summary>
    internal string? TerminalCause { get; init; }

    /// <summary>Gets the monotonic start of the shared selected stopping and diagnostic budget.</summary>
    internal long? StoppingTimestamp { get; init; }

    /// <summary>Gets the monotonic start of the finite metadata commit allowance after process shutdown.</summary>
    internal long? MetadataRecordingTimestamp { get; init; }

    /// <summary>Gets processes whose stop could not be confirmed within the stop allowance.</summary>
    internal IReadOnlyList<int> SurvivingProcessIds { get; init; } = [];

    /// <summary>Gets the expiry of the bearer handed only to the child.</summary>
    internal DateTimeOffset? CredentialExpiresAtUtc { get; init; }

    /// <summary>Gets the validated pre-launch descriptor bytes held by the runner.</summary>
    internal byte[]? DescriptorSnapshot { get; init; }

    /// <summary>Gets cumulative runner-held native observations, including earlier batch entries.</summary>
    internal CompositionOwnerTermination? OwnerTermination { get; init; }

    /// <summary>Gets the native launch identities observed by the runner.</summary>
    internal IReadOnlyList<int> ProcessIds { get; init; } = [];

    /// <summary>Gets a value indicating whether process exit or bounded stopping was observed independently of test success.</summary>
    internal bool StopObserved { get; init; }

    /// <summary>Gets the native stopping observation time.</summary>
    internal DateTimeOffset? ObservedAtUtc { get; init; }

    /// <summary>Gets a value indicating whether the native child process was launched.</summary>
    internal bool Launched { get; init; }
}