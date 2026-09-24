// <copyright file="CompositionProcess.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>
/// Runs bounded helper processes with a scrubbed environment and captures only bounded standard output.
/// </summary>
public static class CompositionProcess
{
    private const int _maximumOutputCharacters = 65_536;

    private static readonly string[] _inheritedVariables =
    [
        "PATH",
        "HOME",
        "USER",
        "LOGNAME",
        "LANG",
        "TMPDIR",
        "TEMP",
        "TMP",
        "SYSTEMROOT",
        "WINDIR",
        "PROGRAMDATA",
        "APPDATA",
        "LOCALAPPDATA",
        "USERPROFILE",
        "XDG_RUNTIME_DIR",
        "DOCKER_HOST",
        "DOCKER_CONFIG",
        "DOCKER_CONTEXT",
        "DOTNET_ROOT",
        "DOTNET_CLI_HOME",
        "DOTNET_NOLOGO",
        "DOTNET_CLI_TELEMETRY_OPTOUT",
        "NUGET_PACKAGES",
    ];

    /// <summary>
    /// Creates a process start description whose environment contains only an allowlist plus explicit values.
    /// </summary>
    /// <param name="fileName">The executable.</param>
    /// <param name="arguments">The arguments.</param>
    /// <param name="workingDirectory">The optional working directory.</param>
    /// <param name="environment">The explicit environment values.</param>
    /// <returns>The start description.</returns>
    public static ProcessStartInfo CreateStartInfo(
        string fileName,
        IEnumerable<string> arguments,
        string? workingDirectory,
        IReadOnlyDictionary<string, string>? environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo start = new(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory ?? string.Empty,
        };
        Dictionary<string, string?> inherited = new(StringComparer.Ordinal);
        foreach (string name in _inheritedVariables)
        {
            string? value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                inherited[name] = value;
            }
        }

        start.Environment.Clear();
        foreach (KeyValuePair<string, string?> pair in inherited)
        {
            start.Environment[pair.Key] = pair.Value;
        }

        if (environment is not null)
        {
            foreach (KeyValuePair<string, string> pair in environment)
            {
                start.Environment[pair.Key] = pair.Value;
            }
        }

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    /// <summary>
    /// Runs a process to completion within a bound.
    /// </summary>
    /// <param name="start">The start description.</param>
    /// <param name="timeout">The bound.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The bounded result.</returns>
    public static async Task<CompositionProcessResult> RunAsync(
        ProcessStartInfo start,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        using Process process = new() { StartInfo = start };
        if (!TryStart(process))
        {
            return new CompositionProcessResult(false, -1, string.Empty, false);
        }

        process.StandardInput.Close();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        Task<string> output = ReadBoundedAsync(process.StandardOutput, bound.Token);
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, bound.Token);
        try
        {
            await process.WaitForExitAsync(bound.Token).ConfigureAwait(false);
            string text = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            return new CompositionProcessResult(true, process.ExitCode, text, false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new CompositionProcessResult(true, -1, string.Empty, true);
        }
    }

    /// <summary>
    /// Starts a long-running process, returns the first standard-output line containing a marker, and stops the process.
    /// </summary>
    /// <param name="start">The start description.</param>
    /// <param name="marker">The ordinal marker text.</param>
    /// <param name="timeout">The bound.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The first matching line, or null.</returns>
    public static async Task<string?> ReadFirstLineContainingAsync(
        ProcessStartInfo start,
        string marker,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentException.ThrowIfNullOrWhiteSpace(marker);
        using Process process = new() { StartInfo = start };
        if (!TryStart(process))
        {
            return null;
        }

        process.StandardInput.Close();
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timeout);
        Task error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, bound.Token);
        try
        {
            while (await process.StandardOutput.ReadLineAsync(bound.Token).ConfigureAwait(false) is string line)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    return line.Length > 4096 ? line[..4096] : line;
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
        finally
        {
            Kill(process);
            try
            {
                await error.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
            {
                // The helper was stopped deliberately; its diagnostic stream is not retained.
            }
        }
    }

    /// <summary>
    /// Kills a process tree best-effort.
    /// </summary>
    /// <param name="process">The process.</param>
    public static void Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(10_000);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited or no longer accessible.
        }
    }

    private static bool TryStart(Process process)
    {
        try
        {
            return process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            int accepted = Math.Min(read, _maximumOutputCharacters - result.Length);
            if (accepted > 0)
            {
                _ = result.Append(buffer, 0, accepted);
            }
        }
    }
}