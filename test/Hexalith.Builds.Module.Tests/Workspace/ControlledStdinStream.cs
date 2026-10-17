// <copyright file="ControlledStdinStream.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

/// <summary>An in-flight reader whose cancellation behavior is explicit.</summary>
/// <param name="ignoreCancellation">Whether the pending read deliberately ignores cancellation.</param>
internal sealed class ControlledStdinStream(bool ignoreCancellation) : Stream
{
    private readonly TaskCompletionSource<int> _pending = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _disposed;

    /// <summary>Gets a value indicating whether the listener closed its owned stream.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Gets the signal that the listener has entered a pending read.</summary>
#pragma warning disable VSTHRD003 // This stream owns the handshake; tests release or cancel its read before cleanup.
    public Task ReadStarted => _started.Task;
#pragma warning restore VSTHRD003

    /// <inheritdoc />
    public override bool CanRead => true;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Releases the deliberate pending read with end of input.</summary>
    public void Complete() => _ = _pending.TrySetResult(0);

    /// <summary>Fails the pending read as a disconnected standard input pipe.</summary>
    public void Fail() => _ = _pending.TrySetException(new IOException("stdin unavailable"));

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _ = _started.TrySetResult();
        return new(ignoreCancellation ? _pending.Task : _pending.Task.WaitAsync(cancellationToken));
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Flush() => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void SetLength(long value) => throw new NotSupportedException();

    /// <inheritdoc />
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }
}
