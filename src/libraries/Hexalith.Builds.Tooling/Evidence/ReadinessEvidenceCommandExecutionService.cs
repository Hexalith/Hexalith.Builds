// <copyright file="ReadinessEvidenceCommandExecutionService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Evidence;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Executes the public readiness-evidence validation command contract.
/// </summary>
internal static class ReadinessEvidenceCommandExecutionService
{
    /// <summary>
    /// Validates readiness evidence and renders its deterministic result.
    /// </summary>
    /// <param name="evidencePath">The YAML readiness-evidence path.</param>
    /// <param name="format">The requested diagnostic output format.</param>
    /// <param name="writer">The destination writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> ExecuteAsync(
        string evidencePath,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentNullException.ThrowIfNull(writer);

        ToolCommandResult result;
        try
        {
            result = await ReadinessEvidenceValidator.ValidateAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ToolDiagnostic diagnostic = new(
                "HXC130",
                ToolPhase.Evidence,
                ToolFailureCategory.Cancelled,
                "The evidence validation was cancelled.",
                "cancellation");
            result = new ToolCommandResult(
                "cancelled",
                ToolOutcome.Passed().Fail(
                    ToolPhase.Evidence,
                    ToolFailureCategory.Cancelled,
                    diagnostic.RuleId,
                    ToolExitCode.Cancelled),
                [diagnostic]);
            await ToolDiagnosticFormatter.WriteAsync(writer, result, format, CancellationToken.None).ConfigureAwait(false);
            return (int)result.Outcome.ExitCode;
        }

        await ToolDiagnosticFormatter.WriteAsync(writer, result, format, cancellationToken).ConfigureAwait(false);
        return (int)result.Outcome.ExitCode;
    }
}