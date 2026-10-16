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
    private Task? _renewal;

    /// <summary>Initializes a new instance of the <see cref="CompositionSelectedTestGate"/> class from the actual runner session.</summary>
    /// <param name="session">The owning ready session.</param>
    /// <param name="cancellationToken">The owner withdrawal token.</param>
    internal CompositionSelectedTestGate(CompositionRunSession session, CancellationToken cancellationToken)
    {
        _plan = session.Plan;
        _observation = session.Readiness.TestObservation;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    }

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

        if (_observation?.GatePath is not null)
        {
            Write(enabled: false);
        }

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

        Write(enabled: true);
        _renewal = RenewAsync();
    }

    private async Task RenewAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), _stop.Token).ConfigureAwait(false);
                Write(enabled: true);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            await _stop.CancelAsync().ConfigureAwait(false);
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

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (enabled)
        {
            _stop.Token.ThrowIfCancellationRequested();
            if (_plan.SelectedTest is null || now >= _plan.SelectedTest.ExecutionDeadlineUtc)
            {
                throw new InvalidDataException("The selected execution deadline has expired.");
            }
        }

        using JsonDocument current = JsonDocument.Parse(File.ReadAllBytes(expected));
        JsonElement root = current.RootElement;
        string? state = root.GetProperty("state").GetString();
        if (root.EnumerateObject().Count() != 4 || root.GetProperty("schemaVersion").GetInt32() != 1
            || state is not ("disabled" or "enabled") || (state == "enabled" && root.GetProperty("profileSha256").GetString() != _observation.ProfileHash))
        {
            throw new InvalidDataException("The selected gate ownership changed.");
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

        File.Move(temporary, expected, overwrite: true);
    }
}
