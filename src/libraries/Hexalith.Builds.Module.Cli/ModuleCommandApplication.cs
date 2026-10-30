// <copyright file="ModuleCommandApplication.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Cli;

using System.CommandLine;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
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
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Debug, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Down, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateCommand(ModuleInvocationCommand.Test, standardOutput, descriptorChildEntryAssemblyPath, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateValidateCommand(standardOutput, operationStarted));
        rootCommand.Subcommands.Add(CreateResultValidationCommand(standardOutput, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreatePrepareBuildCommand(standardOutput, operationStarted));
        rootCommand.Subcommands.Add(CreateListCommand(standardOutput, compositionOptions, operationStarted));
        rootCommand.Subcommands.Add(CreateAttachCommand(standardOutput, compositionOptions, operationStarted));
        return rootCommand;
    }

    private static Command CreateResultValidationCommand(TextWriter output, CompositionEngineOptions? options, Action<Task<int>> operationStarted)
    {
        Command command = new("validate-result", "Rechecks candidate policy, fresh GitHub authority and complete qualification before acceptance.");
        Option<string> request = new("--candidate-request") { Required = true };
        Option<string> environment = new("--environment") { Required = true };
        Option<string> result = new("--result") { Required = true };
        Option<string> format = new("--output") { DefaultValueFactory = _ => "human" };
        _ = format.AcceptOnlyFromAmong("human", "json");
        foreach (Option option in new Option[] { request, environment, result, format })
        {
            command.Options.Add(option);
        }

        command.SetAction((parsed, token) =>
        {
            Task<int> execution = ValidateAsync(token);
            operationStarted(execution);
            return execution;

            async Task<int> ValidateAsync(CancellationToken cancellation)
            {
                ToolCommandResult observation;
                try
                {
                    Hexalith.Builds.Tooling.CandidatePolicy.CandidateGateClient gate = options?.CandidateGateClientOverride ?? Hexalith.Builds.Tooling.CandidatePolicy.CandidateGateClient.FromHost();
                    await Hexalith.Builds.Tooling.CandidatePolicy.CandidatePolicyValidator.ValidateAcceptanceAsync(gate, parsed.GetValue(request)!, parsed.GetValue(environment)!, parsed.GetValue(result)!, cancellation).ConfigureAwait(false);
                    observation = new ToolCommandResult("validated", ToolOutcome.Passed(), []);
                }
                catch (Hexalith.Builds.Tooling.CandidatePolicy.CandidatePolicyException refusal)
                {
                    ToolDiagnostic diagnostic = new("HXP301", ToolPhase.Test, ToolFailureCategory.Manifest, refusal.Message, "candidatePolicy");
                    observation = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.Manifest, diagnostic.RuleId, ToolExitCode.UsageOrManifest), [diagnostic]);
                }
                catch (OperationCanceledException)
                {
                    ToolDiagnostic diagnostic = new("HXC130", ToolPhase.Test, ToolFailureCategory.Cancelled, "Candidate validation was cancelled.", "candidatePolicy");
                    observation = new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.Cancelled, diagnostic.RuleId, ToolExitCode.Cancelled), [diagnostic]);
                }

                await ToolDiagnosticFormatter.WriteAsync(output, observation, ParseOutputFormat(parsed.GetValue(format)), CancellationToken.None).ConfigureAwait(false);
                return (int)observation.Outcome.ExitCode;
            }
        });
        return command;
    }

    private static Command CreatePrepareBuildCommand(TextWriter output, Action<Task<int>> operationStarted)
    {
        Command command = new("prepare-build", "Prepares verified MSBuild imports without starting resources.");
        Option<string> manifest = new("--manifest") { Required = true };
        Option<string> workspace = new("--workspace") { Required = true };
        Option<string> mode = new("--mode") { Required = true };
        _ = mode.AcceptOnlyFromAmong("source", "package");
        Option<string> format = new("--output") { DefaultValueFactory = _ => "human" };
        _ = format.AcceptOnlyFromAmong("human", "json");
        command.Options.Add(manifest);
        command.Options.Add(workspace);
        command.Options.Add(mode);
        command.Options.Add(format);
        command.SetAction((parsed, token) =>
        {
            Task<int> execution = ExecuteAsync(token);
            operationStarted(execution);
            return execution;

            async Task<int> ExecuteAsync(CancellationToken cancellation)
            {
                ToolCommandResult result = await BuildPreparationService.PrepareAsync(
                    parsed.GetValue(manifest)!,
                    parsed.GetValue(mode) == "package" ? WorkspaceMode.Package : WorkspaceMode.Source,
                    parsed.GetValue(workspace)!,
                    cancellation).ConfigureAwait(false);
                await ToolDiagnosticFormatter.WriteAsync(output, result, ParseOutputFormat(parsed.GetValue(format)), CancellationToken.None).ConfigureAwait(false);
                return (int)result.Outcome.ExitCode;
            }
        });
        return command;
    }

    private static Command CreateListCommand(TextWriter output, CompositionEngineOptions? options, Action<Task<int>> operationStarted)
    {
        Command command = new("list", "Lists tracked runner-owned environments and their recorded disposition.");
        Option<string> format = new("--output") { DefaultValueFactory = _ => "human" };
        _ = format.AcceptOnlyFromAmong("human", "json");
        command.Options.Add(format);
        command.SetAction((parsed, token) =>
        {
            Task<int> execution = ExecuteAsync(token);
            operationStarted(execution);
            return execution;

            async Task<int> ExecuteAsync(CancellationToken cancellation)
            {
                ToolCommandResult result = await CompositionRunListing.ReadAsync(new CompositionRunStateStore(options?.StateDirectory ?? CompositionRunStateStore.DefaultDirectory()), DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false);
                await ToolDiagnosticFormatter.WriteAsync(output, result, ParseOutputFormat(parsed.GetValue(format)), CancellationToken.None).ConfigureAwait(false);
                return (int)result.Outcome.ExitCode;
            }
        });
        return command;
    }

    private static Command CreateAttachCommand(TextWriter output, CompositionEngineOptions? options, Action<Task<int>> operationStarted)
    {
        Command command = new("attach", "Admits a run to a compatible runner-owned environment with a finite hold, or ends one attachment.");
        Option<string> owner = new("--owner-run-id") { Required = true };
        Option<string> mode = new("--mode");
        _ = mode.AcceptOnlyFromAmong("source", "package");
        Option<string> composition = new("--composition-identity") { Description = "The composition content identity (manifest SHA-256) the attaching run expects." };
        Option<string> hold = new("--hold-seconds") { Description = "Whole seconds from 1 to 14400; omit for 1800." };
        Option<string> requestId = new("--attachment-request-id") { Description = "A runner-issued request or attached run identity being retried." };
        Option<string> target = new("--target") { DefaultValueFactory = _ => "run-owned" };
        _ = target.AcceptOnlyFromAmong("run-owned", "hosted", "developer-started");
        Option<string> environment = new("--environment-id");
        Option<string[]> artifacts = new("--artifact-identity") { Arity = ArgumentArity.ZeroOrMore, AllowMultipleArgumentsPerToken = false };
        Option<bool> end = new("--end") { Description = "Ends only the attachment named by --attached-run-id; the owner and other attachments keep running." };
        Option<string> attached = new("--attached-run-id");
        Option<string> outcome = new("--outcome") { Description = "The attachment's first terminal outcome: passed, failed or cancelled. Required with --end." };
        Option<string> format = new("--output") { DefaultValueFactory = _ => "human" };
        _ = format.AcceptOnlyFromAmong("human", "json");
        foreach (Option option in new Option[] { owner, mode, composition, hold, requestId, target, environment, artifacts, end, attached, outcome, format })
        {
            command.Options.Add(option);
        }

        command.SetAction((parsed, token) =>
        {
            Task<int> execution = ExecuteAsync(token);
            operationStarted(execution);
            return execution;

            async Task<int> ExecuteAsync(CancellationToken cancellation)
            {
                CompositionRunStateStore store = new(options?.StateDirectory ?? CompositionRunStateStore.DefaultDirectory());
                ToolOutputFormat outputFormat = ParseOutputFormat(parsed.GetValue(format));
                bool ending = parsed.GetValue(end);
                bool malformed = ending
                    ? parsed.GetValue(attached) is null || new Option[] { hold, requestId, mode, composition, target, environment, artifacts }.Any(option => parsed.GetResult(option) is { Implicit: false })
                    : parsed.GetValue(mode) is null || parsed.GetValue(composition) is null || parsed.GetResult(attached) is not null || parsed.GetResult(outcome) is not null;
                if (malformed)
                {
                    return await ToolCommandHost.WriteParseFailureAsync(output, outputFormat).ConfigureAwait(false);
                }

                ToolCommandResult result;
                if (ending)
                {
                    result = await CompositionAttachAdmission.EndAsync(store, parsed.GetValue(owner)!, parsed.GetValue(attached)!, parsed.GetValue(outcome), DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false);
                }
                else
                {
                    string? holdSeconds = parsed.GetResult(hold) is null ? null : parsed.GetValue(hold) ?? string.Empty;
                    string? presented = parsed.GetResult(requestId) is null ? null : parsed.GetValue(requestId) ?? string.Empty;
                    CompositionAttachRequest request = new(parsed.GetValue(owner)!, parsed.GetValue(mode)!, parsed.GetValue(composition)!)
                    {
                        HoldSeconds = holdSeconds,
                        PresentedRequestId = presented,
                        Target = parsed.GetValue(target)!,
                        EnvironmentId = parsed.GetValue(environment),
                        ArtifactIdentities = parsed.GetValue(artifacts) ?? [],
                    };
                    result = await CompositionAttachAdmission.AdmitAsync(store, request, DateTimeOffset.UtcNow, cancellation).ConfigureAwait(false);
                }

                await ToolDiagnosticFormatter.WriteAsync(output, result, outputFormat, CancellationToken.None).ConfigureAwait(false);
                return (int)result.Outcome.ExitCode;
            }
        });
        return command;
    }

    private static Command CreateValidateCommand(TextWriter standardOutput, Action<Task<int>> operationStarted)
    {
        Command command = new("validate", "Validates local Platform module declarations without starting resources.");
        Option<string[]> manifestOption = new("--manifest")
        {
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");
        Option<bool> adoptionOption = new("--adoption")
        {
            Description = "Reports bounded static module enrollment requirements; SM-2 requires human verification.",
        };
        Option<string> environmentOption = new("--environment")
        {
            Description = "Validates a test-environment descriptor with the supported offline contract reader.",
        };
        command.Options.Add(environmentOption);
        command.Options.Add(adoptionOption);
        command.Options.Add(manifestOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            Task<int> execution = ExecuteValidationAsync(
                parseResult.GetValue(manifestOption) ?? [],
                parseResult.GetValue(environmentOption),
                parseResult.GetValue(adoptionOption),
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
        string? environmentPath,
        bool adoption,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        if (manifestPaths.Any(string.IsNullOrWhiteSpace)
            || (environmentPath is null ? manifestPaths.Length == 0 : string.IsNullOrWhiteSpace(environmentPath) || manifestPaths.Length != 0 || adoption))
        {
            return await ToolCommandHost.WriteParseFailureAsync(writer, format).ConfigureAwait(false);
        }

        ToolCommandResult commandResult;
        try
        {
            if (environmentPath is not null)
            {
                TestEnvironmentContractReadResult validation = TestEnvironmentContractReader.ReadDescriptor(environmentPath, cancellationToken);
                ToolOutcome outcome = validation.IsValid ? ToolOutcome.Passed() : ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest);
                commandResult = new ToolCommandResult(validation.IsValid ? "validated" : "failed", outcome, validation.Diagnostics);
            }
            else if (adoption)
            {
                PlatformAdoptionValidationResult report = await PlatformAdoptionValidator.ValidateAsync(manifestPaths, cancellationToken: cancellationToken).ConfigureAwait(false);
                ToolOutcome adoptionOutcome = report.IsValid ? ToolOutcome.Passed() : ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, report.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest);
                commandResult = new ToolCommandResult(report.IsValid ? "validated" : "failed", adoptionOutcome, report.Diagnostics)
                {
                    Adoption = report.Rows,
                    AdoptionScope = PlatformAdoptionValidator.Scope,
                };
            }
            else
            {
                PlatformManifestValidationResult validation = PlatformManifestValidator.Validate(manifestPaths, cancellationToken);
                ToolOutcome outcome = validation.IsValid ? ToolOutcome.Passed() : ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest);
                commandResult = new ToolCommandResult(validation.IsValid ? "validated" : "failed", outcome, validation.Diagnostics);
            }
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
            Required = command != ModuleInvocationCommand.Down,
        };
        Option<string[]> profileOption = new("--profile")
        {
            Required = command == ModuleInvocationCommand.Test,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };
        Option<string> filterOption = new("--filter");
        Option<string> evidenceOption = new("--evidence");
        Option<string> candidateOption = new("--candidate-request") { Description = "Signed GitHub gate issuance; requires independently enrolled host authority." };
        Option<string> runIdOption = new("--run-id");
        Option<string> outputOption = new("--output")
        {
            DefaultValueFactory = _ => "human",
        };
        _ = outputOption.AcceptOnlyFromAmong("human", "json");
        Option<string> modeOption = new("--mode") { DefaultValueFactory = _ => "source" };
        _ = modeOption.AcceptOnlyFromAmong(command == ModuleInvocationCommand.Debug ? ["source"] : ["source", "package"]);

        commandDefinition.Options.Add(manifestOption);
        commandDefinition.Options.Add(profileOption);
        commandDefinition.Options.Add(filterOption);
        commandDefinition.Options.Add(evidenceOption);
        if (command == ModuleInvocationCommand.Test)
        {
            commandDefinition.Options.Add(candidateOption);
        }

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
            WorkspaceMode mode = WorkspaceMode.Source;
            if (command is not ModuleInvocationCommand.Debug and not ModuleInvocationCommand.Down
                && string.Equals(parseResult.GetValue(modeOption), "package", StringComparison.Ordinal))
            {
                mode = WorkspaceMode.Package;
            }

            if (command == ModuleInvocationCommand.Down && string.IsNullOrWhiteSpace(parseResult.GetValue(manifestOption))
                && (string.IsNullOrWhiteSpace(parseResult.GetValue(runIdOption)) || !string.IsNullOrWhiteSpace(parseResult.GetValue(evidenceOption))))
            {
                return ToolCommandHost.WriteParseFailureAsync(standardOutput, ParseOutputFormat(parseResult.GetValue(outputOption)));
            }

            Task<int> execution = ModuleCommandExecutionService.ExecuteAsync(
                command,
                parseResult.GetValue(manifestOption) ?? "unused-manifest-for-exact-run",
                parseResult.GetValue(profileOption)?.FirstOrDefault(),
                parseResult.GetValue(filterOption),
                parseResult.GetValue(evidenceOption),
                ParseOutputFormat(parseResult.GetValue(outputOption)),
                standardOutput,
                cancellationToken,
                descriptorChildEntryAssemblyPath,
                command == ModuleInvocationCommand.Down ? parseResult.GetValue(runIdOption) : null,
                compositionOptions,
                mode,
                parseResult.GetValue(profileOption),
                command == ModuleInvocationCommand.Test ? parseResult.GetValue(candidateOption) : null);
            operationStarted(execution);
            return execution;
        });
        return commandDefinition;
    }

    private static string CommandDescription(ModuleInvocationCommand command) => command switch
    {
        ModuleInvocationCommand.Run => "Starts a supported module runtime.",
        ModuleInvocationCommand.Debug => "Starts the active module and root-declared dependencies from source in Debug and reports launcher process ids.",
        ModuleInvocationCommand.Down => "Tears down runner-owned module resources.",
        ModuleInvocationCommand.Test => "Runs a named module qualification profile.",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported module command."),
    };

    private static string CommandName(ModuleInvocationCommand command) => command switch
    {
        ModuleInvocationCommand.Run => "run",
        ModuleInvocationCommand.Debug => "debug",
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
