// <copyright file="ModuleCommandApplication.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Cli;

using System.CommandLine;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

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
        rootCommand.Subcommands.Add(CreateValidateCommand(standardOutput, operationStarted));
        return rootCommand;
    }

    private static Command CreateValidateCommand(TextWriter standardOutput, Action<Task<int>> operationStarted)
    {
        Command command = new("validate", "Validates local Platform module declarations without starting resources.");
        Option<string[]> manifestOption = new("--manifest")
        {
            Required = true,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");
        command.Options.Add(manifestOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            Task<int> execution = ExecuteValidationAsync(
                parseResult.GetValue(manifestOption)!,
                ParseOutputFormat(parseResult.GetValue(outputOption)),
                standardOutput,
                cancellationToken);
            operationStarted(execution);
            return execution;
        });
        return command;
    }

    private static async Task<int> ExecuteValidationAsync(
        string[] manifestPaths,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        if (manifestPaths.Any(string.IsNullOrWhiteSpace))
        {
            return await ToolCommandHost.WriteParseFailureAsync(writer, format).ConfigureAwait(false);
        }

        ToolCommandResult commandResult;
        try
        {
            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate(manifestPaths, cancellationToken);
            ToolOutcome outcome = validation.IsValid ? ToolOutcome.Passed() : ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest);
            commandResult = new ToolCommandResult(validation.IsValid ? "validated" : "failed", outcome, validation.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            ToolDiagnostic diagnostic = new("HXC130", ToolPhase.Manifest, ToolFailureCategory.Cancelled, "Platform declaration validation was cancelled.", "manifest");
            commandResult = new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Cancelled, diagnostic.RuleId, ToolExitCode.Cancelled), [diagnostic]);
        }

        await ToolDiagnosticFormatter.WriteAsync(writer, commandResult, format, CancellationToken.None).ConfigureAwait(false);
        return (int)commandResult.Outcome.ExitCode;
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
        Option<string> modeOption = new("--mode") { DefaultValueFactory = _ => "source" };
        _ = modeOption.AcceptOnlyFromAmong("source", "package");

        commandDefinition.Options.Add(manifestOption);
        commandDefinition.Options.Add(profileOption);
        commandDefinition.Options.Add(filterOption);
        commandDefinition.Options.Add(evidenceOption);
        if (command == ModuleInvocationCommand.Down)
        {
            commandDefinition.Options.Add(runIdOption);
        }

        commandDefinition.Options.Add(outputOption);
        if (command != ModuleInvocationCommand.Down)
        {
            commandDefinition.Options.Add(modeOption);
        }

        commandDefinition.SetAction((parseResult, cancellationToken) =>
        {
            WorkspaceMode mode = command != ModuleInvocationCommand.Down && string.Equals(parseResult.GetValue(modeOption), "package", StringComparison.Ordinal)
                ? WorkspaceMode.Package
                : WorkspaceMode.Source;
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
                compositionOptions,
                mode);
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
