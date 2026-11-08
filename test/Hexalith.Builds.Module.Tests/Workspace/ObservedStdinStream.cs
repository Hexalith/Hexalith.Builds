// <copyright file="ObservedStdinStream.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

/// <summary>Observes the real production input's pending read without replacing its cancellation behavior.</summary>
/// <param name="input">The actual owned input stream.</param>
internal sealed class ObservedStdinStream(Stream input) : Stream
{
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _pendingReadObserved;

    /// <inheritdoc />
    public override bool CanRead => input.CanRead;

    /// <inheritdoc />
    public override bool CanSeek => false;

    /// <inheritdoc />
    public override bool CanWrite => false;

    /// <inheritdoc />
    public override long Length => throw new NotSupportedException();

    /// <inheritdoc />
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    /// <summary>Gets a value indicating whether an underlying read returned an incomplete operation.</summary>
    public bool WasReadPending => Volatile.Read(ref _pendingReadObserved) != 0;

    /// <summary>Gets the handshake after the underlying asynchronous read was requested.</summary>
#pragma warning disable VSTHRD003 // The caller owns the read handshake and drains or bounds it before fixture cleanup.
    public Task ReadStarted => _started.Task;
#pragma warning restore VSTHRD003

    /// <inheritdoc />
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        StdinChildProgress.Mark("read-call", reader: true);
        ValueTask<int> pending = input.ReadAsync(buffer, cancellationToken);
        if (!pending.IsCompleted)
        {
            _ = Interlocked.Exchange(ref _pendingReadObserved, 1);
        }

        StdinChildProgress.Mark(pending.IsCompleted ? "read-completed" : "read-pending", reader: true);
        _ = _started.TrySetResult();
        return pending;
    }

    /// <inheritdoc />
    public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);

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
        if (disposing)
        {
            StdinChildProgress.Mark("input-dispose", reader: true);
            input.Dispose();
            StdinChildProgress.Mark("input-disposed", reader: true);
        }

        base.Dispose(disposing);
    }
}
