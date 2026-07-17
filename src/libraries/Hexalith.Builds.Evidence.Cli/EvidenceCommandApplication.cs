// <copyright file="EvidenceCommandApplication.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Cli;

using System.CommandLine;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Evidence;

/// <summary>
/// Hosts the public <c>hexalith-evidence</c> command contract.
/// </summary>
internal static class EvidenceCommandApplication
{
    /// <summary>
    /// Invokes the evidence command application.
    /// </summary>
    /// <param name="arguments">The command-line arguments.</param>
    /// <param name="standardOutput">The standard output writer.</param>
    /// <param name="standardError">The standard error writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> InvokeAsync(
        string[] arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        RootCommand rootCommand = CreateRootCommand(standardOutput);
        ParseResult parseResult = rootCommand.Parse(arguments);
        if (parseResult.Errors.Count > 0)
        {
            return await ToolCommandHost.WriteParseFailureAsync(
                standardOutput,
                ToolCommandHost.RequestedOutputFormat(arguments)).ConfigureAwait(false);
        }

        return await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand CreateRootCommand(TextWriter standardOutput)
    {
        RootCommand rootCommand = new("Validates deterministic Hexalith readiness evidence.");
        rootCommand.Subcommands.Add(CreateValidateCommand(standardOutput));
        return rootCommand;
    }

    private static Command CreateValidateCommand(TextWriter standardOutput)
    {
        Command command = new("validate", "Validates a hexalith.readiness-evidence.v1 YAML matrix.");
        Argument<string> evidencePathArgument = new("evidence-path")
        {
            Arity = ArgumentArity.ExactlyOne,
        };
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");

        command.Arguments.Add(evidencePathArgument);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) => ReadinessEvidenceCommandExecutionService.ExecuteAsync(
            parseResult.GetValue(evidencePathArgument)!,
            ParseOutputFormat(parseResult.GetValue(outputOption)),
            standardOutput,
            cancellationToken));
        return command;
    }

    private static ToolOutputFormat ParseOutputFormat(string? output) =>
        string.Equals(output, "json", StringComparison.Ordinal)
            ? ToolOutputFormat.Json
            : ToolOutputFormat.Human;
}
