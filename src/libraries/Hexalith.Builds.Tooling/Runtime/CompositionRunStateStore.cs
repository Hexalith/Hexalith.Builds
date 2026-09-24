// <copyright file="CompositionRunStateStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Stores metadata-only run state in a per-user directory, keyed by run identity.
/// </summary>
/// <param name="directory">The absolute state directory.</param>
public sealed class CompositionRunStateStore(string directory)
{
    /// <summary>
    /// Gets the absolute state directory.
    /// </summary>
    public string Directory { get; } = Path.IsPathFullyQualified(directory)
        ? Path.GetFullPath(directory)
        : throw new ArgumentException("The state directory must be absolute.", nameof(directory));

    /// <summary>
    /// Gets the default per-user state directory.
    /// </summary>
    /// <returns>The directory path.</returns>
    public static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create),
        "hexalith-builds",
        "g4-runs");

    /// <summary>
    /// Gets the state path of one run.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <returns>The state file path.</returns>
    /// <exception cref="ArgumentException">The run identity is malformed.</exception>
    public string PathFor(string runId) => CompositionRunPlanFactory.IsRunId(runId)
        ? Path.Combine(Directory, runId + ".json")
        : throw new ArgumentException("The run identity must be 32 lowercase hexadecimal characters.", nameof(runId));

    /// <summary>
    /// Writes one run state atomically.
    /// </summary>
    /// <param name="state">The state.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after the write.</returns>
    public async Task WriteAsync(CompositionRunState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        CompositionWorkspace.CreatePrivateDirectory(Directory);
        await CompositionDocumentStore.WriteAsync(PathFor(state.RunId), state, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one run state.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state, or null when absent or unreadable.</returns>
    public Task<CompositionRunState?> TryReadAsync(string runId, CancellationToken cancellationToken) =>
        CompositionDocumentStore.TryReadAsync<CompositionRunState>(PathFor(runId), cancellationToken);

    /// <summary>
    /// Finds valid run states for one exact manifest hash. Unreadable state fails closed.
    /// </summary>
    /// <param name="manifestHash">The SHA-256 hash of the validated manifest bytes.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Matching run states ordered by run identity.</returns>
    /// <exception cref="IOException">Runner-owned state cannot be verified.</exception>
    public async Task<IReadOnlyList<CompositionRunState>> FindByManifestHashAsync(string manifestHash, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestHash);
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        List<CompositionRunState> matches = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runId = Path.GetFileNameWithoutExtension(path);
            if (!CompositionRunPlanFactory.IsRunId(runId))
            {
                throw new IOException("Runner-owned run state has an invalid identity.");
            }

            CompositionRunState? state = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
            if (state is null || !string.Equals(state.Schema, CompositionRunState.SupportedSchema, StringComparison.Ordinal)
                || !string.Equals(state.RunId, runId, StringComparison.Ordinal))
            {
                throw new IOException("Runner-owned run state cannot be verified.");
            }

            if (string.Equals(state.ManifestHash, manifestHash, StringComparison.Ordinal))
            {
                matches.Add(state);
            }
        }

        return [.. matches.OrderBy(state => state.RunId, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deletes one run state best-effort.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <returns>True when no state remains for the run.</returns>
    public bool Delete(string runId)
    {
        string path = PathFor(runId);
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return !File.Exists(path);
        }
    }
}
