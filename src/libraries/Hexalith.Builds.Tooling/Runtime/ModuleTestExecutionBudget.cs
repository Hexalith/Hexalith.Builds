// <copyright file="ModuleTestExecutionBudget.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;

/// <summary>Keeps one monotonically decreasing lifetime through selection, mapping, startup and execution.</summary>
internal sealed class ModuleTestExecutionBudget : IDisposable
{
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly CancellationTokenSource _bound;
    private int _seconds = 14400;

    /// <summary>Initializes a new instance of the <see cref="ModuleTestExecutionBudget"/> class.</summary>
    /// <param name="owner">The owner's independent cancellation token.</param>
    internal ModuleTestExecutionBudget(CancellationToken owner)
    {
        _bound = CancellationTokenSource.CreateLinkedTokenSource(owner);
        _bound.CancelAfter(TimeSpan.FromSeconds(_seconds));
    }

    /// <summary>Gets the same deadline token for every setup and execution operation.</summary>
    public CancellationToken Token => _bound.Token;

    /// <inheritdoc />
    public void Dispose() => _bound.Dispose();

    /// <summary>Tightens the original lifetime without renewing time already consumed.</summary>
    /// <param name="seconds">The validated lifetime.</param>
    internal void Restrict(int seconds)
    {
        _seconds = Math.Min(_seconds, seconds);
        TimeSpan remaining = TimeSpan.FromSeconds(_seconds) - Stopwatch.GetElapsedTime(_started);
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
