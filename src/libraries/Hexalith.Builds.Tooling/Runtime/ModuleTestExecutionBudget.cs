// <copyright file="ModuleTestExecutionBudget.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;

/// <summary>Keeps one monotonically decreasing lifetime through actual execution only.</summary>
internal sealed class ModuleTestExecutionBudget : IDisposable
{
    private readonly CancellationTokenSource _bound;
    private long? _started;
    private int _seconds = 14400;

    /// <summary>Initializes a new instance of the <see cref="ModuleTestExecutionBudget"/> class.</summary>
    /// <param name="owner">The owner's independent cancellation token.</param>
    internal ModuleTestExecutionBudget(CancellationToken owner)
        => _bound = CancellationTokenSource.CreateLinkedTokenSource(owner);

    /// <summary>Gets the owner token, bounded by the combined budget only after execution activation.</summary>
    public CancellationToken Token => _bound.Token;

    /// <inheritdoc />
    public void Dispose() => _bound.Dispose();

    /// <summary>Starts the execution budget once; subsequent calls never renew it.</summary>
    /// <param name="seconds">The finite combined entry budget.</param>
    internal void Activate(int seconds)
    {
        if (_started is null)
        {
            _started = Stopwatch.GetTimestamp();
            _seconds = seconds;
        }

        Restrict(seconds);
    }

    /// <summary>Tightens the original lifetime without renewing time already consumed.</summary>
    /// <param name="seconds">The validated lifetime.</param>
    internal void Restrict(int seconds)
    {
        _seconds = Math.Min(_seconds, seconds);
        if (_started is null)
        {
            return;
        }

        TimeSpan remaining = TimeSpan.FromSeconds(_seconds) - Stopwatch.GetElapsedTime(_started.Value);
        if (remaining <= TimeSpan.Zero)
        {
            _bound.Cancel();
        }
        else
        {
            _bound.CancelAfter(remaining);
        }
    }
}
