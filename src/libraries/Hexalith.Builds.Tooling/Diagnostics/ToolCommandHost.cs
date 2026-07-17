// <copyright file="ToolCommandHost.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Provides shared public-command hosting behavior for cancellation and parser failures.
/// </summary>
public static class ToolCommandHost
{
    /// <summary>
    /// Invokes a tool operation with Ctrl+C mapped to its cooperative cancellation token.
    /// </summary>
    /// <param name="operation">The operation to invoke.</param>
    /// <returns>The operation exit code.</returns>
    public static async Task<int> RunWithConsoleCancellationAsync(Func<CancellationToken, Task<int>> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);

        using CancellationTokenSource cancellationTokenSource = new();
        ConsoleCancelEventHandler handler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellationTokenSource.Cancel();
        };
        Console.CancelKeyPress += handler;
        try
        {
            return await operation(cancellationTokenSource.Token).ConfigureAwait(false);
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    /// <summary>
    /// Resolves the requested public diagnostic format without retaining command-line values.
    /// </summary>
    /// <param name="arguments">The raw command-line argument sequence.</param>
    /// <returns>The requested safe output format.</returns>
    public static ToolOutputFormat RequestedOutputFormat(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--output=json", StringComparison.Ordinal))
            {
                return ToolOutputFormat.Json;
            }

            if (string.Equals(argument, "--output", StringComparison.Ordinal)
                && index + 1 < arguments.Count
                && string.Equals(arguments[index + 1], "json", StringComparison.Ordinal))
            {
                return ToolOutputFormat.Json;
            }
        }

        return ToolOutputFormat.Human;
    }

    /// <summary>
    /// Writes the stable metadata-only result for a command-line parse failure.
    /// </summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="format">The requested output format.</param>
    /// <returns>The stable usage exit code.</returns>
    public static async Task<int> WriteParseFailureAsync(TextWriter writer, ToolOutputFormat format)
    {
        ArgumentNullException.ThrowIfNull(writer);

        ToolDiagnostic diagnostic = new(
            "HXC001",
            ToolPhase.Usage,
            ToolFailureCategory.Usage,
            "The command line is invalid.",
            "arguments",
            "Use --help to supply a supported command and required options.");
        ToolCommandResult result = new(
            "failed",
            ToolOutcome.Passed().Fail(
                ToolPhase.Usage,
                ToolFailureCategory.Usage,
                diagnostic.RuleId,
                ToolExitCode.UsageOrManifest),
            [diagnostic]);
        await ToolDiagnosticFormatter.WriteAsync(writer, result, format, CancellationToken.None).ConfigureAwait(false);
        return (int)ToolExitCode.UsageOrManifest;
    }
}
