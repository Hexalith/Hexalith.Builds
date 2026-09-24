// <copyright file="CompositionRunResourceScanner.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Text;

/// <summary>
/// Finds and removes resources tagged with one run identity: containers by label and processes by environment tag.
/// </summary>
/// <param name="dockerCommand">The Docker CLI command.</param>
public sealed class CompositionRunResourceScanner(string dockerCommand)
{
    private static readonly TimeSpan _dockerTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Lists every resource still tagged with the run identity.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The tagged resources.</returns>
    /// <exception cref="ArgumentException">The run identity is malformed.</exception>
    public async Task<CompositionRunResources> FindAsync(string runId, CancellationToken cancellationToken)
    {
        if (!CompositionRunPlanFactory.IsRunId(runId))
        {
            throw new ArgumentException("The run identity must be 32 lowercase hexadecimal characters.", nameof(runId));
        }

        IReadOnlyList<string>? containers = await FindContainersAsync(runId, cancellationToken).ConfigureAwait(false);
        return new CompositionRunResources(containers ?? [], FindProcesses(runId), Unverified: containers is null);
    }

    /// <summary>
    /// Removes every resource tagged with the run identity, with bounded retries.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resources still present after the bound.</returns>
    public async Task<CompositionRunResources> RemoveAsync(string runId, CancellationToken cancellationToken)
    {
        CompositionRunResources remaining = await FindAsync(runId, cancellationToken).ConfigureAwait(false);
        for (int attempt = 0; attempt < 5 && !remaining.IsEmpty; attempt++)
        {
            foreach (int processId in remaining.ProcessIds)
            {
                TryKill(processId);
            }

            if (remaining.ContainerIds.Count > 0)
            {
                _ = await CompositionProcess.RunAsync(
                    CompositionProcess.CreateStartInfo(dockerCommand, ["rm", "--force", "--volumes", .. remaining.ContainerIds], null, null),
                    _dockerTimeout,
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            remaining = await FindAsync(runId, cancellationToken).ConfigureAwait(false);
        }

        return remaining;
    }

    private static IReadOnlyList<int> FindProcesses(string runId)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return [];
        }

        byte[] tag = Encoding.ASCII.GetBytes(CompositionEnvironment.RunId + "=" + runId);
        int self = Environment.ProcessId;
        List<int> matches = [];
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(directory), out int processId) || processId == self)
            {
                continue;
            }

            try
            {
                byte[] environment = File.ReadAllBytes(Path.Combine(directory, "environ"));
                if (ContainsEntry(environment, tag))
                {
                    matches.Add(processId);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Exited, or owned by another user and therefore never started by this runner.
            }
        }

        return [.. matches.Order()];
    }

    private static bool ContainsEntry(byte[] environment, byte[] entry)
    {
        int start = 0;
        while (start < environment.Length)
        {
            int end = Array.IndexOf(environment, (byte)0, start);
            if (end < 0)
            {
                end = environment.Length;
            }

            if (environment.AsSpan(start, end - start).SequenceEqual(entry))
            {
                return true;
            }

            start = end + 1;
        }

        return false;
    }

    private static void TryKill(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception
            or NotSupportedException or AggregateException)
        {
            // Already exited.
        }
    }

    /// <summary>
    /// Lists the containers labelled with the run.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The container identities, or null when the query could not be verified (never "none").</returns>
    private async Task<IReadOnlyList<string>?> FindContainersAsync(string runId, CancellationToken cancellationToken)
    {
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo(
                dockerCommand,
                ["ps", "--all", "--quiet", "--no-trunc", "--filter", $"label={CompositionEnvironment.ContainerRunLabel}={runId}"],
                null,
                null),
            _dockerTimeout,
            cancellationToken).ConfigureAwait(false);
        return result.Started && result.ExitCode == 0 && !result.TimedOut
            ? [.. result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal)]
            : null;
    }
}