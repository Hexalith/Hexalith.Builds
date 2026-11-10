// <copyright file="AttachedSuiteRun.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The live owner's in-memory handle to one runner-owned attached child and its first stop reason.</summary>
/// <param name="hold">The picked-up attachment.</param>
internal sealed class AttachedSuiteRun(CompositionAttachment hold) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private string? _reason;
    private int _processId;

    /// <summary>Gets the picked-up attachment.</summary>
    internal CompositionAttachment Hold { get; } = hold ?? throw new ArgumentNullException(nameof(hold));

    /// <summary>Gets the attached run identity.</summary>
    internal string AttachedRunId => Hold.AttachedRunId;

    /// <summary>Gets the token that stops the attached child.</summary>
    internal CancellationToken StopToken => _stop.Token;

    /// <summary>Gets the first stop reason: <c>expired</c>, <c>run-cancelled</c> or an environment withdrawal reason.</summary>
    internal string? StopReason => Volatile.Read(ref _reason);

    /// <summary>Gets the launched child process identity, when known.</summary>
    internal int? ProcessId => Volatile.Read(ref _processId) is int id and > 0 ? id : null;

    /// <summary>Gets or sets the task that runs, stops and finalizes the attached child.</summary>
    internal Task Completion { get; set; } = Task.CompletedTask;

    /// <inheritdoc/>
    public void Dispose() => _stop.Dispose();

    /// <summary>Records the launched child.</summary>
    /// <param name="processId">The child process identity.</param>
    internal void Started(int processId) => Volatile.Write(ref _processId, processId);

    /// <summary>Stops the child for the first reason only; later reasons never replace it.</summary>
    /// <param name="reason">The stop reason.</param>
    /// <returns>Whether this call set the reason.</returns>
    internal bool Signal(string reason)
    {
        if (Interlocked.CompareExchange(ref _reason, reason, null) is not null)
        {
            return false;
        }

        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The run already finished and released its stop source.
        }

        return true;
    }
}
