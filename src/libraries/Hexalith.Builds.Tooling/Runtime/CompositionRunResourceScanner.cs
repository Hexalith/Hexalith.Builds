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

    /// <summary>Gets the exact environment entry that distinguishes the processes being observed.</summary>
    internal string ProcessEnvironmentVariable { get; init; } = CompositionEnvironment.RunId;

    /// <summary>Gets an internal exact-process discovery seam for supported-host verification.</summary>
    internal Func<string, CancellationToken, (IReadOnlyList<int> ProcessIds, bool Verified)>? ProcessDiscoveryOverride { get; init; }

    /// <summary>Gets a test-only observation after an owned process identity has been discovered.</summary>
    internal Action<int>? ProcessMatchedOverride { get; init; }

    private Dictionary<int, long> PreexistingProcessStarts { get; init; } = [];

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
        (IReadOnlyList<int> processes, bool verified) = ProcessDiscoveryOverride?.Invoke(runId, cancellationToken)
            ?? FindTaggedProcesses(runId, cancellationToken);
        return new CompositionRunResources(containers ?? [], processes, Unverified: containers is null || !verified)
        {
            DiscoveryFailures = [.. containers is null ? new string[] { "containers" } : [], .. !verified ? new string[] { "processes" } : []],
        };
    }

    /// <summary>
    /// Removes every resource tagged with the run identity, with bounded retries.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resources still present after the bound.</returns>
    public async Task<CompositionRunResources> RemoveAsync(string runId, CancellationToken cancellationToken)
    {
        CompositionRunResources remaining = new([], [], true);
        try
        {
            remaining = await FindAsync(runId, cancellationToken).ConfigureAwait(false);
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
                CompositionRunResources observed = await FindAsync(runId, cancellationToken).ConfigureAwait(false);
                remaining = observed with
                {
                    ContainerIds = observed.DiscoveryFailures.Contains("containers", StringComparer.Ordinal) ? [.. observed.ContainerIds.Concat(remaining.ContainerIds).Distinct(StringComparer.Ordinal)] : observed.ContainerIds,
                    ProcessIds = observed.DiscoveryFailures.Contains("processes", StringComparer.Ordinal) ? [.. observed.ProcessIds.Concat(remaining.ProcessIds).Distinct()] : observed.ProcessIds,
                };
            }

            return remaining;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or UnauthorizedAccessException)
        {
            return remaining with { Unverified = true, DiscoveryFailures = [.. remaining.DiscoveryFailures, "cleanup-verification"] };
        }
    }

    /// <summary>Captures pre-existing process identities before a fresh private native tag can be issued.</summary>
    /// <returns>A scanner that distinguishes new unreadable native work from unrelated pre-existing processes.</returns>
    internal static CompositionRunResourceScanner CreateNativeScanner()
    {
        Dictionary<int, long> existing = [];
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (int processId in processes.Select(process => process.Id))
            {
                if (ProcessStartTicks(processId) is { } started)
                {
                    existing.Add(processId, started);
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return new CompositionRunResourceScanner(string.Empty)
        {
            ProcessEnvironmentVariable = NativeTestHandoff._nativeExecutionId,
            PreexistingProcessStarts = existing,
        };
    }

    /// <summary>Checks both PID and start identity before excluding a process that predates a native execution.</summary>
    /// <param name="processId">The current process identity.</param>
    /// <param name="baseline">The original pre-launch process observations.</param>
    /// <returns>Whether the same process was already running before launch.</returns>
    internal static bool WasRunningBefore(int processId, Dictionary<int, long> baseline) =>
        baseline.TryGetValue(processId, out long started) && ProcessStartTicks(processId) == started;

    /// <summary>Lists only processes tagged with a run identity, such as an attached run's children.</summary>
    /// <param name="runId">The run identity carried by the process environment tag.</param>
    /// <param name="cancellationToken">The scan allowance.</param>
    /// <returns>The tagged processes and whether absence is verified.</returns>
    /// <exception cref="ArgumentException">The run identity is malformed.</exception>
    internal (IReadOnlyList<int> ProcessIds, bool Verified) FindTaggedProcesses(string runId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        _ = CompositionRunPlanFactory.IsRunId(runId) ? runId : throw new ArgumentException("The run identity must be 32 lowercase hexadecimal characters.", nameof(runId));
        return ProcessDiscoveryOverride?.Invoke(runId, cancellationToken)
            ?? (OperatingSystem.IsLinux() ? FindProcesses(runId, ProcessEnvironmentVariable, PreexistingProcessStarts, ProcessMatchedOverride, cancellationToken) : CompositionProcessEnvironmentReader.Find(runId, cancellationToken, ProcessEnvironmentVariable, PreexistingProcessStarts));
    }

    /// <summary>Kills processes tagged with a run identity with bounded retries, never touching other runs.</summary>
    /// <param name="runId">The run identity carried by the process environment tag.</param>
    /// <param name="cancellationToken">The stop allowance.</param>
    /// <returns>The processes still tagged after the bound and whether absence is verified.</returns>
    internal async Task<(IReadOnlyList<int> ProcessIds, bool Verified)> RemoveTaggedProcessesAsync(string runId, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> remaining = [];
        bool verified;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            (remaining, verified) = FindTaggedProcesses(runId, cancellationToken);
            for (int attempt = 0; attempt < 5 && remaining.Count > 0; attempt++)
            {
                foreach (int processId in remaining)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    TryKill(processId);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
                (remaining, verified) = FindTaggedProcesses(runId, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // The allowance ended: report what is still known rather than claiming a stop.
            return (remaining, false);
        }

        return (remaining, verified);
    }

    private static (IReadOnlyList<int> ProcessIds, bool Verified) FindProcesses(string runId, string environmentVariable, Dictionary<int, long> preexistingProcessStarts, Action<int>? onMatched, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
        {
            return ([], false);
        }

        byte[] tag = Encoding.ASCII.GetBytes(environmentVariable + "=" + runId);
        int self = Environment.ProcessId;
        List<int> matches = [];
        bool verified = true;
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return ([.. matches.Order()], false);
            }

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
                    onMatched?.Invoke(processId);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (environmentVariable == CompositionEnvironment.RunId)
                {
                    // Preserve the established owner scanner behavior for inaccessible foreign processes.
                    continue;
                }

                if (WasRunningBefore(processId, preexistingProcessStarts))
                {
                    // A process that predates the private tag cannot have inherited this native execution's identity.
                    continue;
                }

                try
                {
                    string[] status = File.ReadAllLines(Path.Combine(directory, "status"));
                    bool zombie = status.Any(line => line.StartsWith("State:", StringComparison.Ordinal) && line.Contains("Z (zombie)", StringComparison.Ordinal));
                    if (!zombie)
                    {
                        verified = false;
                    }
                }
                catch (Exception readFailure) when (readFailure is IOException or UnauthorizedAccessException)
                {
                    verified &= !Directory.Exists(directory);
                }
            }
        }

        return ([.. matches.Order()], verified && !cancellationToken.IsCancellationRequested);
    }

    private static long? ProcessStartTicks(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
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
        string[] ids = result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return result.Started && result.ExitCode == 0 && !result.TimedOut && ids.All(id => id.Length is >= 12 and <= 64 && id.All(char.IsAsciiHexDigit))
            ? [.. ids.Order(StringComparer.Ordinal)]
            : null;
    }
}
