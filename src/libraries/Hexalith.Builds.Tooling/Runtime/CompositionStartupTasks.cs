// <copyright file="CompositionStartupTasks.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Selects and runs declared per-start verification tasks inside the startup deadline.
/// </summary>
public static class CompositionStartupTasks
{
    /// <summary>
    /// Selects tasks whose scope is <c>per-start-verify</c>. Other scopes are not part of startup.
    /// </summary>
    /// <param name="manifestPath">The declaration file used to resolve repository-relative executables.</param>
    /// <param name="declarations">The validated modules.</param>
    /// <returns>The tasks in declaration order.</returns>
    public static IReadOnlyList<CompositionStartupTask> Select(string manifestPath, IReadOnlyList<PlatformModuleDeclaration>? declarations) =>
        Select(manifestPath, declarations, null);

    /// <summary>
    /// Runs tasks until one fails or the deadline passes.
    /// </summary>
    /// <param name="tasks">The selected tasks.</param>
    /// <param name="deadlineUtc">The absolute startup deadline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The failing module task when one started and failed, whether the deadline elapsed before the next task started, or neither when every task succeeded.</returns>
    public static Task<(string? FailingTask, bool DeadlineElapsed)> RunAsync(IReadOnlyList<CompositionStartupTask> tasks, DateTimeOffset deadlineUtc, CancellationToken cancellationToken) =>
        RunOwnedAsync(tasks, deadlineUtc, null, cancellationToken);

    /// <summary>Selects tasks using the same declaring-owner root as Platform composition.</summary>
    /// <param name="manifestPath">The supplied manifest, used by the compatibility fallback.</param>
    /// <param name="declarations">The complete authoritative dependency closure.</param>
    /// <param name="mapping">The command-selected source/package mapping.</param>
    /// <returns>The per-start verification tasks.</returns>
    internal static IReadOnlyList<CompositionStartupTask> Select(string manifestPath, IReadOnlyList<PlatformModuleDeclaration>? declarations, SourceMapping? mapping)
    {
        if (declarations is null || declarations.Count == 0)
        {
            return [];
        }

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        List<CompositionStartupTask> tasks = [];
        foreach (PlatformModuleDeclaration declaration in declarations)
        {
            JsonElement module = declaration.Declaration;
            string declaringRoot = root;
            if (mapping is not null)
            {
                declaringRoot = CompositionDeclarationClosure.TaskRepositoryRoot(mapping, declaration);
            }
            else if (Path.IsPathFullyQualified(declaration.Source))
            {
                declaringRoot = ManifestPathValidator.FindRepositoryRoot(declaration.Source);
            }

            if (!TryString(module, "identity", "moduleId", out string moduleId)
                || module.ValueKind != JsonValueKind.Object
                || !module.TryGetProperty("lifecycle", out JsonElement lifecycle)
                || lifecycle.ValueKind != JsonValueKind.Object
                || !lifecycle.TryGetProperty("tasks", out JsonElement taskList)
                || taskList.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement task in taskList.EnumerateArray())
            {
                if (!TryPropertyString(task, "scope", out string scope)
                    || !string.Equals(scope, "per-start-verify", StringComparison.Ordinal)
                    || !TryPropertyString(task, "name", out string name))
                {
                    continue;
                }

                string executable = TryPropertyString(task, "executable", out string relative) ? relative : string.Empty;
                List<ToolDiagnostic> pathDiagnostics = [];
                string resolved = ManifestPathValidator.ValidateExistingFile(executable, declaringRoot, "lifecycle.tasks.executable", pathDiagnostics) ?? string.Empty;
                List<string> arguments = [];
                if (task.TryGetProperty("arguments", out JsonElement argumentList) && argumentList.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement argument in argumentList.EnumerateArray())
                    {
                        if (argument.ValueKind == JsonValueKind.String && argument.GetString() is { Length: > 0 } value)
                        {
                            arguments.Add(value);
                        }
                    }
                }

                tasks.Add(new CompositionStartupTask(moduleId, name, resolved, arguments)
                {
                    WorkingDirectory = declaringRoot,
                    ExecutableError = pathDiagnostics.Count == 0 ? null : pathDiagnostics[0].RuleId + ": " + pathDiagnostics[0].Message,
                });
            }
        }

        return tasks;
    }

    /// <summary>Runs startup tasks with the owning run tag inherited by all descendants.</summary>
    /// <param name="tasks">The selected tasks.</param>
    /// <param name="deadlineUtc">The original startup deadline.</param>
    /// <param name="runId">The owning run identity.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The failing task or an elapsed deadline.</returns>
    internal static async Task<(string? FailingTask, bool DeadlineElapsed)> RunOwnedAsync(IReadOnlyList<CompositionStartupTask> tasks, DateTimeOffset deadlineUtc, string? runId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        foreach (CompositionStartupTask task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow) <= TimeSpan.Zero)
            {
                return (null, true);
            }

            bool? succeeded = await SucceededAsync(task, deadlineUtc, runId, cancellationToken).ConfigureAwait(false);
            if (succeeded is null)
            {
                return (null, true);
            }

            if (succeeded is false)
            {
                return (task.ModuleId + "/" + task.Name, false);
            }
        }

        return (null, false);
    }

    private static async Task<bool?> SucceededAsync(CompositionStartupTask task, DateTimeOffset deadlineUtc, string? runId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(task.ExecutablePath) || !File.Exists(task.ExecutablePath))
        {
            return false;
        }

        TimeSpan remaining = CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow);
        if (remaining <= TimeSpan.Zero)
        {
            return null;
        }

        using Process process = new()
        {
            StartInfo = new ProcessStartInfo(task.ExecutablePath)
            {
                WorkingDirectory = task.WorkingDirectory ?? Path.GetDirectoryName(task.ExecutablePath) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        if (runId is not null)
        {
            process.StartInfo.Environment[CompositionEnvironment.RunId] = runId;
        }

        foreach (string argument in task.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        try
        {
            if (!process.Start())
            {
                return false;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            return false;
        }

        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(remaining);
        try
        {
            await Task.WhenAll(
                process.WaitForExitAsync(bound.Token),
                DrainAsync(process.StandardOutput.BaseStream, bound.Token),
                DrainAsync(process.StandardError.BaseStream, bound.Token)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompositionProcess.Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        return process.ExitCode == 0;
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            await stream.CopyToAsync(Stream.Null, 8192, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Discarded output cannot change the verifier's exit result.
        }
    }

    private static bool TryString(JsonElement element, string objectName, string propertyName, out string value)
    {
        value = string.Empty;
        return element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(objectName, out JsonElement child)
            && TryPropertyString(child, propertyName, out value);
    }

    private static bool TryPropertyString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(propertyName, out JsonElement property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            return false;
        }

        value = property.GetString()!;
        return true;
    }
}
