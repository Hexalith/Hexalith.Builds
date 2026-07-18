// <copyright file="ModuleCommandApplication.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Cli;

using System.CommandLine;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Hosts the public <c>hexalith-module</c> command contract.
/// </summary>
internal static class ModuleCommandApplication
{
    /// <summary>
    /// Invokes the module command application.
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
        return parseResult.Errors.Count > 0
            ? await ToolCommandHost.WriteParseFailureAsync(
                standardOutput,
                ToolCommandHost.RequestedOutputFormat(arguments)).ConfigureAwait(false)
            : await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand CreateRootCommand(TextWriter standardOutput)
    {
        RootCommand rootCommand = new("Runs supported Hexalith module qualifications.");
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Run, standardOutput));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Down, standardOutput));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Test, standardOutput));
        return rootCommand;
    }

    private static Command CreateCommand(ModuleInvocationCommand command, TextWriter standardOutput)
    {
        Command commandDefinition = new(CommandName(command), CommandDescription(command));
        Option<string> manifestOption = new("--manifest")
        {
            Required = true,
        };
        Option<string> profileOption = new("--profile")
        {
            Required = command == ModuleInvocationCommand.Test,
        };
        Option<string> filterOption = new("--filter");
        Option<string> evidenceOption = new("--evidence");
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");

        commandDefinition.Options.Add(manifestOption);
        commandDefinition.Options.Add(profileOption);
        commandDefinition.Options.Add(filterOption);
        commandDefinition.Options.Add(evidenceOption);
        commandDefinition.Options.Add(outputOption);
        commandDefinition.SetAction((parseResult, cancellationToken) => ModuleCommandExecutionService.ExecuteAsync(
            command,
            parseResult.GetValue(manifestOption)!,
            parseResult.GetValue(profileOption),
            parseResult.GetValue(filterOption),
            parseResult.GetValue(evidenceOption),
            ParseOutputFormat(parseResult.GetValue(outputOption)),
            standardOutput,
            cancellationToken));
        return commandDefinition;
    }

    private static string CommandDescription(ModuleInvocationCommand command) => command switch
    {
        ModuleInvocationCommand.Run => "Starts a supported module runtime.",
        ModuleInvocationCommand.Down => "Tears down runner-owned module resources.",
        ModuleInvocationCommand.Test => "Runs a named module qualification profile.",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported module command."),
    };

    private static string CommandName(ModuleInvocationCommand command) => command switch
    {
        ModuleInvocationCommand.Run => "run",
        ModuleInvocationCommand.Down => "down",
        ModuleInvocationCommand.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported module command."),
    };

    private static ToolOutputFormat ParseOutputFormat(string? output) =>
        string.Equals(output, "json", StringComparison.Ordinal)
            ? ToolOutputFormat.Json
            : ToolOutputFormat.Human;
}