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
    /// <param name="descriptorChildEntryAssemblyPath">The optional private descriptor child entry assembly.</param>
    /// <param name="compositionOptions">The optional runner-owned composition settings.</param>
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> InvokeAsync(
        string[] arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken,
        string? descriptorChildEntryAssemblyPath = null,
        CompositionEngineOptions? compositionOptions = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        Task<int>? runningCommand = null;
        RootCommand rootCommand = CreateRootCommand(
            standardOutput,
            descriptorChildEntryAssemblyPath,
            compositionOptions,
            task => runningCommand = task);
        ParseResult parseResult = rootCommand.Parse(arguments);
        if (parseResult.Errors.Count > 0 || HasBlankManifestValue(arguments))
        {
            return await ToolCommandHost.WriteParseFailureAsync(
                standardOutput,
                ToolCommandHost.RequestedOutputFormat(arguments)).ConfigureAwait(false);
        }

        int result = await parseResult.InvokeAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result == (int)ToolExitCode.Cancelled && runningCommand is not null
            ? await runningCommand.ConfigureAwait(false)
            : result;
    }

    private static RootCommand CreateRootCommand(
        TextWriter standardOutput,
        string? descriptorChildEntryAssemblyPath,
        CompositionEngineOptions? compositionOptions,
        Action<Task<int>> operationStarted)
    {
        RootCommand rootCommand = new("Runs supported Hexalith module qualifications.");
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Run, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Down, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Test, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        return rootCommand;
    }

    private static Command CreateCommand(
        ModuleInvocationCommand command,
        TextWriter standardOutput,
        string? descriptorChildEntryAssemblyPath,
        CompositionEngineOptions? compositionOptions,
        Action<Task<int>> operationStarted)
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
        Option<string> runIdOption = new("--run-id");
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");

        commandDefinition.Options.Add(manifestOption);
        commandDefinition.Options.Add(profileOption);
        commandDefinition.Options.Add(filterOption);
        commandDefinition.Options.Add(evidenceOption);
        if (command == ModuleInvocationCommand.Down)
        {
            commandDefinition.Options.Add(runIdOption);
        }

        commandDefinition.Options.Add(outputOption);
        commandDefinition.SetAction((parseResult, cancellationToken) =>
        {
            Task<int> execution = ModuleCommandExecutionService.ExecuteAsync(
                command,
                parseResult.GetValue(manifestOption)!,
                parseResult.GetValue(profileOption),
                parseResult.GetValue(filterOption),
                parseResult.GetValue(evidenceOption),
                ParseOutputFormat(parseResult.GetValue(outputOption)),
                standardOutput,
                cancellationToken,
                descriptorChildEntryAssemblyPath,
                command == ModuleInvocationCommand.Down ? parseResult.GetValue(runIdOption) : null,
                compositionOptions);
            operationStarted(execution);
            return execution;
        });
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

    private static bool HasBlankManifestValue(string[] arguments)
    {
        const string manifestOption = "--manifest";
        for (int index = 0; index < arguments.Length; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, manifestOption, StringComparison.Ordinal))
            {
                return index + 1 >= arguments.Length || string.IsNullOrWhiteSpace(arguments[index + 1]);
            }

            if (argument.StartsWith($"{manifestOption}=", StringComparison.Ordinal))
            {
                return string.IsNullOrWhiteSpace(argument[(manifestOption.Length + 1)..]);
            }
        }

        return false;
    }

    private static ToolOutputFormat ParseOutputFormat(string? output) =>
        string.Equals(output, "json", StringComparison.Ordinal)
            ? ToolOutputFormat.Json
            : ToolOutputFormat.Human;
}
