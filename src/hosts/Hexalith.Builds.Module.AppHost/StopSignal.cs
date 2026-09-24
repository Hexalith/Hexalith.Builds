// <copyright file="StopSignal.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Signals a stop when the runner writes the stop command, or when a nonpersistent run loses standard input.
/// </summary>
internal sealed class StopSignal : IAsyncDisposable
{
    private readonly CompositionRunPlan _plan;

    private readonly CancellationTokenSource _stop = new();

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Usage",
        "CA2213:Disposable fields should be disposed",
        Justification = "The stream is disposed on a bounded worker during DisposeAsync so a blocked close cannot hang shutdown.")]
    private readonly Stream _input = Console.OpenStandardInput();

    private readonly Task _listener;

    private StopSignal(CompositionRunPlan plan)
    {
        _plan = plan;
        _listener = ListenAsync();
    }

    /// <summary>
    /// Gets the token cancelled when the run must stop.
    /// </summary>
    public CancellationToken Token => _stop.Token;

    /// <summary>
    /// Gets a value indicating whether a stop was requested.
    /// </summary>
    public bool IsStopRequested => _stop.IsCancellationRequested;

    /// <summary>
    /// Starts listening on standard input.
    /// </summary>
    /// <param name="plan">The run plan used for a metadata-only listener failure.</param>
    /// <returns>The signal.</returns>
    public static StopSignal Listen(CompositionRunPlan plan) => new(plan);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
#pragma warning disable VSTHRD003 // The listener was started by this instance and is completed here exactly once.
        try
        {
            // Cancellation callbacks and standard-input disposal may block an active read. Bound both operations
            // and the listener together so none can delay the AppHost exit indefinitely.
            Task cancelStop = Task.Run(async () => await _stop.CancelAsync().ConfigureAwait(false), CancellationToken.None);
            Task closeInput = Task.Run(_input.Dispose, CancellationToken.None);
            await Task.WhenAll(cancelStop, closeInput, _listener).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            string path = CompositionStartupFailure.PathFor(_plan.Workspace);
            if (!File.Exists(path))
            {
                await RunReadiness.ReportFailureAsync(_plan, "HXR028", "stdin-listener", "-", "blocked-after-stop").ConfigureAwait(false);
            }

            throw new TimeoutException("HXR028 stdin-listener blocked-after-stop");
        }
        finally
        {
            _stop.Dispose();
        }
#pragma warning restore VSTHRD003
    }

    private async Task ListenAsync()
    {
        await Task.Yield();
        try
        {
            using StreamReader input = new(_input, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
            while (!_stop.IsCancellationRequested)
            {
                string? line = await input.ReadLineAsync(_stop.Token).ConfigureAwait(false);
                if (line is null && string.Equals(
                    Environment.GetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit),
                    "1",
                    StringComparison.Ordinal))
                {
                    return;
                }

                if (line is null || string.Equals(line.Trim(), CompositionEnvironment.StopCommand, StringComparison.Ordinal))
                {
                    await _stop.CancelAsync().ConfigureAwait(false);
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Stopping for another reason.
        }
        catch (IOException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Disposing the input stream is part of bounded shutdown.
        }
    }
}
