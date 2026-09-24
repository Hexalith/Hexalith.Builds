// <copyright file="LiveEngineScope.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Owns a composition engine with test-private state and workspace directories and tears down every run it started.
/// </summary>
internal sealed class LiveEngineScope : IAsyncDisposable
{
    private readonly List<string> _runIds = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="LiveEngineScope"/> class.
    /// </summary>
    /// <param name="name">A short scope name used for diagnostic log files.</param>
    public LiveEngineScope(string name)
    {
        Directory = Path.Combine(Path.GetTempPath(), "hexalith-g4-it-" + Guid.NewGuid().ToString("N")[..12]);
        string? logDirectory = Environment.GetEnvironmentVariable("HEXALITH_G4_LOG_DIR");
        Engine = new CompositionEngine(new CompositionEngineOptions(LiveRepository.AppHostAssembly, LiveRepository.DescriptorChildAssembly)
        {
            StateDirectory = Path.Combine(Directory, "state"),
            WorkspaceRoot = Path.Combine(Directory, "w"),
            ReadinessTimeout = TimeSpan.FromMinutes(6),
            AppHostLogPath = string.IsNullOrWhiteSpace(logDirectory)
                ? null
                : Path.Combine(logDirectory, $"{name}-{Guid.NewGuid():N}.log"),
        });
    }

    /// <summary>
    /// Gets the private root directory.
    /// </summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the engine.
    /// </summary>
    public CompositionEngine Engine { get; }

    /// <summary>
    /// Starts one run of the executable fixture and records it for teardown.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The start result.</returns>
    public async Task<CompositionStartResult> StartAsync(CancellationToken cancellationToken)
    {
        CompositionStartResult result = await Engine.StartAsync(LiveRepository.Manifest, cancellationToken).ConfigureAwait(false);
        if (result.RunId is not null)
        {
            lock (_runIds)
            {
                _runIds.Add(result.RunId);
            }
        }

        return result;
    }

    /// <summary>
    /// Starts one run and fails with its diagnostics unless it is ready.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The ready session.</returns>
    /// <exception cref="InvalidOperationException">The run did not become ready.</exception>
    public async Task<CompositionRunSession> StartReadyAsync(CancellationToken cancellationToken)
    {
        CompositionStartResult result = await StartAsync(cancellationToken).ConfigureAwait(false);
        return result.Session ?? throw new InvalidOperationException(
            "The live run did not become ready: " + CompositionDocumentStore.Serialize(result.Result));
    }

    /// <summary>
    /// Records a run identity discovered outside <see cref="StartAsync"/> for teardown.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    public void Track(string runId)
    {
        lock (_runIds)
        {
            _runIds.Add(runId);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        using CancellationTokenSource bound = new(TimeSpan.FromMinutes(5));
        foreach (string runId in _runIds.Distinct(StringComparer.Ordinal))
        {
            _ = await Engine.DownAsync(runId, bound.Token).ConfigureAwait(false);
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // Already removed.
        }
        catch (IOException)
        {
            // Best-effort temporary cleanup.
        }
    }
}