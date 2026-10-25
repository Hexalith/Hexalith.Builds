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

    /// <summary>Gets the exclusive resource claims coordinated with this state store.</summary>
    public CompositionResourceClaimStore Claims { get; } = new(directory);

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
    public Task WriteAsync(CompositionRunState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        CompositionWorkspace.CreatePrivateDirectory(Directory);
        return CompositionDocumentStore.WriteAsync(PathFor(state.RunId), state, cancellationToken);
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
        return [.. (await EnumerateAsync(cancellationToken).ConfigureAwait(false)).Where(state => string.Equals(state.ManifestHash, manifestHash, StringComparison.Ordinal))];
    }

    /// <summary>Enumerates validated tracked states without loading declarations or recomputing identity.</summary>
    /// <param name="cancellationToken">The enumeration cancellation.</param>
    /// <returns>Durable states ordered by owner identity.</returns>
    /// <exception cref="IOException">Tracked state is invalid or unreadable; absence is never substituted.</exception>
    public Task<IReadOnlyList<CompositionRunState>> EnumerateAsync(CancellationToken cancellationToken = default)
        => EnumerateAsync(null, cancellationToken);

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
            _ = File.GetAttributes(path);
            return false;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Checks that recorded resource collections can be consumed without inventing missing observations.</summary>
    /// <param name="resources">The optional recorded observation.</param>
    /// <returns>Whether every represented collection is present.</returns>
    internal static bool ValidResources(CompositionRunResources? resources) => resources is null
        || (resources.ContainerIds?.All(id => id is not null) == true
            && resources.ProcessIds is not null && resources.WorkspaceIds?.All(id => id is not null) == true
            && resources.ClaimIds?.All(id => id is not null) == true && resources.DiscoveryFailures is not null);

    /// <summary>Validates a committed terminal observation before any nested resource is consumed.</summary>
    /// <param name="record">The recorded observation.</param>
    /// <param name="runId">The enclosing owner identity.</param>
    /// <returns>Whether required observations and their ownership agree.</returns>
    internal static bool ValidOutcome(ModuleTestRunRecord record, string runId) => record.RunId == runId && record.OwnerId == runId
        && record.TerminalOutcome is not null && record.Selection is not null && record.Resources?.All(resource => resource is not null) == true
        && record.RemainingResources is not null && ValidResources(record.RemainingResources)
        && record.DiagnosticRuleIds is not null && record.LaterDiagnosticRuleIds is not null
        && ValidCleanup(record.CleanupDisposition, record.RemainingResources);

    /// <summary>Checks represented cleanup states and rejects completed cleanup without verified absence.</summary>
    /// <param name="disposition">The represented cleanup state.</param>
    /// <param name="resources">The latest available cleanup observation.</param>
    /// <returns>Whether the demonstrated cleanup invariants hold.</returns>
    internal static bool ValidCleanup(string disposition, CompositionRunResources? resources) => disposition is "not-requested" or "pending" or "in-progress" or "incomplete"
        || (disposition == "complete" && resources is not null && ValidResources(resources) && resources.IsEmpty);

    /// <summary>Enumerates verified states while optionally reporting individual unreadable entries.</summary>
    /// <param name="onInvalid">Receives each unverifiable entry; absence preserves strict enumeration.</param>
    /// <param name="cancellationToken">The enumeration cancellation.</param>
    /// <returns>The verified states.</returns>
    /// <exception cref="IOException">An entry is invalid and no incomplete-list observer is provided.</exception>
    internal async Task<IReadOnlyList<CompositionRunState>> EnumerateAsync(Action? onInvalid, CancellationToken cancellationToken)
    {
        if (!System.IO.Directory.Exists(Directory))
        {
            return [];
        }

        List<CompositionRunState> states = [];
        foreach (string path in System.IO.Directory.EnumerateFiles(Directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string runId = Path.GetFileNameWithoutExtension(path);
            if (!CompositionRunPlanFactory.IsRunId(runId))
            {
                if (onInvalid is null)
                {
                    throw new IOException("Runner-owned run state has an invalid identity.");
                }

                onInvalid();
                continue;
            }

            CompositionRunState? state = await TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
            if (state is null || state.Schema != CompositionRunState.SupportedSchema || state.RunId != runId
                || string.IsNullOrEmpty(state.Workspace) || !Path.IsPathFullyQualified(state.Workspace) || !Enum.IsDefined(state.Status)
                || !ValidResources(state.RemainingResources)
                || !ValidCleanup(state.CleanupDisposition, state.RemainingResources ?? state.TestRun?.RemainingResources)
                || (state.TestRun is { } test && !ValidOutcome(test, runId))
                || (state.AttemptId is not null && (!CompositionRunPlanFactory.IsRunId(state.AttemptId) || state.AttemptId == runId || state.AttemptId == state.EnvironmentId))
                || (state.EnvironmentId is not null && (!CompositionRunPlanFactory.IsRunId(state.EnvironmentId) || state.EnvironmentId == runId)))
            {
                if (onInvalid is null)
                {
                    throw new IOException("Runner-owned run state cannot be verified.");
                }

                onInvalid();
                continue;
            }

            states.Add(state);
        }

        return [.. states.OrderBy(state => state.RunId, StringComparer.Ordinal)];
    }
}
