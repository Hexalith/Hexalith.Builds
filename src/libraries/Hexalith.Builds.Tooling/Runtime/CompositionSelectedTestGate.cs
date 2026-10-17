// <copyright file="CompositionSelectedTestGate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

/// <summary>Renews only a selected run's existing short gate, until its immutable deadline or owner withdrawal.</summary>
internal sealed class CompositionSelectedTestGate : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly CompositionRunPlan _plan;
    private readonly CompositionTestObservation? _observation;
    private readonly Lock _sync = new();
    private readonly TimeProvider _clock;
    private readonly Func<CancellationToken, Task> _delay;
    private readonly CancellationTokenRegistration _withdrawal;
    private Task? _renewal;
    private bool _opened;

    /// <summary>Initializes a new instance of the <see cref="CompositionSelectedTestGate"/> class from the actual runner session.</summary>
    /// <param name="session">The owning ready session.</param>
    /// <param name="cancellationToken">The owner withdrawal token.</param>
    /// <param name="clock">The clock; tests may advance UTC without sleeping.</param>
    /// <param name="delay">The owned renewal wait; tests may release a controlled tick.</param>
    internal CompositionSelectedTestGate(CompositionRunSession session, CancellationToken cancellationToken, TimeProvider? clock = null, Func<CancellationToken, Task>? delay = null)
    {
        _plan = session.Plan;
        _observation = session.Readiness.TestObservation;
        _clock = clock ?? TimeProvider.System;
        _delay = delay ?? (token => Task.Delay(TimeSpan.FromMinutes(5), token));
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _withdrawal = _stop.Token.Register(Close);
    }

    /// <summary>Gets a value indicating whether renewal or closure failed without replacing a prior execution outcome.</summary>
    internal bool Failed { get; private set; }

    /// <summary>Gets the owner/renewal cancellation boundary used by the selected test.</summary>
    internal CancellationToken Token => _stop.Token;

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "VSTHRD003:Avoid awaiting foreign Tasks", Justification = "The renewal task is started and owned by this instance and ends on its cancellation token.")]
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_renewal is not null)
        {
            await _renewal.ConfigureAwait(false);
        }

        Close();
        await _withdrawal.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }

    /// <summary>Opens a complete run-owned binding and begins bounded renewal; other test kinds have no gate.</summary>
    /// <exception cref="InvalidDataException">The observation does not belong to this ready run.</exception>
    internal void Start()
    {
        if (_observation?.GatePath is null)
        {
            return;
        }

        if (!_observation.IsComplete || _observation.RunId != _plan.RunId)
        {
            throw new InvalidDataException("The selected observation is incomplete or foreign.");
        }

        lock (_sync)
        {
            Write(enabled: true);
            _opened = true;
        }

        _renewal = RenewAsync();
    }

    private static byte[] ReadGate(string path)
    {
        using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path);
        if (!stream.CanSeek || stream.Length is <= 0 or > 4096)
        {
            throw new InvalidDataException("The selected gate is not a positive bounded regular file.");
        }

        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private async Task RenewAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _delay(_stop.Token).ConfigureAwait(false);
                lock (_sync)
                {
                    Write(enabled: true);
                }
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
        {
            if (error is not OperationCanceledException)
            {
                Failed = true;
            }

            await _stop.CancelAsync().ConfigureAwait(false);
        }
    }

    private void Close()
    {
        lock (_sync)
        {
            if (!_opened)
            {
                return;
            }

            try
            {
                Write(enabled: false);
                _opened = false;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or NotSupportedException)
            {
                Failed = true;
            }
        }
    }

    private void Write(bool enabled)
    {
        string expected = Path.Combine(_plan.Workspace, "platform-components", "retention", "gate.json");
        if (_observation!.GatePath != expected || new FileInfo(expected).LinkTarget is not null)
        {
            throw new InvalidDataException("The selected gate is outside the run-owned binding.");
        }

        DirectoryInfo? directory = new(Path.GetDirectoryName(expected)!);
        while (directory is not null && directory.FullName != Path.GetFullPath(_plan.Workspace))
        {
            if (directory.LinkTarget is not null)
            {
                throw new InvalidDataException("The selected gate parent is a link.");
            }

            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidDataException("The selected gate workspace is unavailable.");
        }

        DateTimeOffset now = _clock.GetUtcNow();
        if (enabled)
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_plan.SelectedTest is null || now >= _plan.SelectedTest.ExecutionDeadlineUtc)
            {
                throw new InvalidDataException("The selected execution deadline has expired.");
            }
        }

        byte[] original = ReadGate(expected);
        using JsonDocument current = JsonDocument.Parse(original);
        JsonElement root = current.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 4
            || !root.TryGetProperty("schemaVersion", out JsonElement schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out int version) || version != 1
            || !root.TryGetProperty("state", out JsonElement stateValue) || stateValue.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("profileSha256", out JsonElement profile) || profile.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("expiresUtcMs", out JsonElement expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out long currentExpiry))
        {
            throw new InvalidDataException("The selected gate authority is malformed.");
        }

        string? state = stateValue.GetString();
        if (state == "disabled" && profile.GetString()!.Length == 0 && currentExpiry == 0)
        {
            if (enabled && _opened)
            {
                throw new InvalidDataException("The selected gate was withdrawn.");
            }

            if (!enabled)
            {
                return;
            }
        }
        else if (state != "enabled" || profile.GetString() != _observation.ProfileHash || currentExpiry <= 0
            || _plan.SelectedTest is null || currentExpiry > _plan.SelectedTest.ExecutionDeadlineUtc.ToUnixTimeMilliseconds()
            || (enabled && (currentExpiry <= now.ToUnixTimeMilliseconds() || currentExpiry > now.AddMinutes(15).AddSeconds(5).ToUnixTimeMilliseconds())))
        {
            throw new InvalidDataException("The selected gate ownership or expiry changed.");
        }

        DateTimeOffset expiry = now.AddMinutes(10);
        if (_plan.SelectedTest is { } selected && expiry > selected.ExecutionDeadlineUtc)
        {
            expiry = selected.ExecutionDeadlineUtc;
        }

        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, state = enabled ? "enabled" : "disabled", profileSha256 = enabled ? _observation.ProfileHash : string.Empty, expiresUtcMs = enabled ? expiry.ToUnixTimeMilliseconds() : 0 });
        string temporary = expected + "." + Guid.NewGuid().ToString("N");
        using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            output.Write(bytes);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        if (enabled)
        {
            _stop.Token.ThrowIfCancellationRequested();
        }

        if (!ReadGate(expected).SequenceEqual(original))
        {
            throw new InvalidDataException("The selected gate changed during renewal or closure.");
        }

        File.Move(temporary, expected, overwrite: true);
    }
}