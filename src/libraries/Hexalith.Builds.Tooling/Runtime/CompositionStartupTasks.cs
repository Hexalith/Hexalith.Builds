// <copyright file="CompositionStartupTasks.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

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
    public static IReadOnlyList<CompositionStartupTask> Select(string manifestPath, IReadOnlyList<PlatformModuleDeclaration>? declarations)
    {
        if (declarations is null || declarations.Count == 0)
        {
            return [];
        }

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        List<CompositionStartupTask> tasks = [];
        foreach (JsonElement module in declarations.Select(declaration => declaration.Declaration))
        {
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
                string resolved = ManifestPathValidator.ValidateExistingFile(executable, root, "lifecycle.tasks.executable", pathDiagnostics) ?? string.Empty;
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
                    ExecutableError = pathDiagnostics.Count == 0 ? null : pathDiagnostics[0].RuleId + ": " + pathDiagnostics[0].Message,
                });
            }
        }

        return tasks;
    }

    /// <summary>
    /// Runs tasks until one fails or the deadline passes.
    /// </summary>
    /// <param name="tasks">The selected tasks.</param>
    /// <param name="deadlineUtc">The absolute startup deadline.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The failing module task when one started and failed, whether the deadline elapsed before the next task started, or neither when every task succeeded.</returns>
    public static async Task<(string? FailingTask, bool DeadlineElapsed)> RunAsync(IReadOnlyList<CompositionStartupTask> tasks, DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        foreach (CompositionStartupTask task in tasks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CompositionStartupClock.Remaining(deadlineUtc, DateTimeOffset.UtcNow) <= TimeSpan.Zero)
            {
                return (null, true);
            }

            bool? succeeded = await SucceededAsync(task, deadlineUtc, cancellationToken).ConfigureAwait(false);
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

    private static async Task<bool?> SucceededAsync(CompositionStartupTask task, DateTimeOffset deadlineUtc, CancellationToken cancellationToken)
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
                WorkingDirectory = Path.GetDirectoryName(task.ExecutablePath) ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (string argument in task.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.OutputDataReceived += static (_, _) => { };
        process.ErrorDataReceived += static (_, _) => { };
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

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(remaining);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CompositionProcess.Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        return process.ExitCode == 0;
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
