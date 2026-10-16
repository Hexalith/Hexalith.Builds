// <copyright file="CompositionStartupRequest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Owns the original run/debug startup clock while source mapping is prepared.</summary>
internal sealed class CompositionStartupRequest : IDisposable
{
    private readonly CancellationTokenSource _bound;
    private readonly TimeSpan _fallback;

    /// <summary>Initializes a new instance of the <see cref="CompositionStartupRequest"/> class before mapping.</summary>
    /// <param name="requestedAt">The command request time.</param>
    /// <param name="fallback">The default startup deadline.</param>
    /// <param name="ownerToken">The caller cancellation token.</param>
    internal CompositionStartupRequest(DateTimeOffset requestedAt, TimeSpan fallback, CancellationToken ownerToken)
    {
        RequestedAt = requestedAt;
        _fallback = fallback;
        _bound = CancellationTokenSource.CreateLinkedTokenSource(ownerToken);
        Deadline = CompositionStartupDeadlineResolver.Resolve(null, fallback);
        Apply(Deadline);
    }

    /// <summary>Gets the original lifecycle request time.</summary>
    internal DateTimeOffset RequestedAt { get; }

    /// <summary>Gets the validated effective deadline known before mapping.</summary>
    internal CompositionEffectiveDeadline Deadline { get; private set; }

    /// <summary>Gets the mapping cancellation token.</summary>
    internal CancellationToken Token => _bound.Token;

    /// <inheritdoc/>
    public void Dispose() => _bound.Dispose();

    /// <summary>Reads available root-declared owner metadata before expensive initialization.</summary>
    /// <param name="manifestPath">The root-owned or already refreshed manifest.</param>
    /// <param name="mode">The selected workspace mode.</param>
    /// <param name="mapping">The already selected mapping, when present.</param>
    /// <param name="mappingProbe">An optional metadata-only mapping probe for deterministic coordination tests.</param>
    /// <returns>A task that configures the finite startup bound.</returns>
    internal async Task ConfigureAsync(string manifestPath, WorkspaceMode mode, SourceMapping? mapping, Func<string, WorkspaceMode, CancellationToken, Task<SourceMapping>>? mappingProbe = null)
    {
        if (!PlatformManifestCommand.IsV2(manifestPath))
        {
            return;
        }

        PlatformManifestValidationResult supplied = PlatformManifestValidator.Validate([manifestPath], Token);
        if (!supplied.IsValid)
        {
            return;
        }

        try
        {
            // Discover the complete available owner set under the original finite default bound.
            // A partial active-module override could cancel the linked token before a dependency's
            // larger override is known. Expensive source mapping receives only the selected bound.
            mapping ??= await (mappingProbe?.Invoke(manifestPath, mode, Token) ?? ProbeMappingAsync(manifestPath, mode, Token)).ConfigureAwait(false);
            IReadOnlyList<PlatformModuleDeclaration> declarations = CompositionDeclarationClosure.Load(
                new CompositionDeclarationOptions(mapping, [Path.GetFullPath(manifestPath)])
                {
                    ActiveModule = PlatformManifestCommand.ActiveModule(supplied.Declarations),
                });
            Token.ThrowIfCancellationRequested();
            Apply(CompositionStartupDeadlineResolver.Resolve(declarations, _fallback));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or WorkspaceMappingException)
        {
            // Missing checkouts are resolved by normal source mapping. They cannot grant an unvalidated override.
            // The engine always reloads the authoritative closure after mapping and checks this original clock.
        }
    }

    private static async Task<SourceMapping> ProbeMappingAsync(string manifestPath, WorkspaceMode mode, CancellationToken cancellationToken)
    {
        string active = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        string root = active;
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(active, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.Output))
        {
            root = probe.Output.Trim();
            while (true)
            {
                CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
                if (parent.ExitCode != 0 || string.IsNullOrWhiteSpace(parent.Output))
                {
                    break;
                }

                root = parent.Output.Trim();
            }
        }

        List<SourceMappingEntry> entries = [new(Path.GetFileName(active), "source", active)];
        foreach (string path in await WorkspaceRootResolver.ReadDirectReferencesAsync(root, cancellationToken).ConfigureAwait(false))
        {
            string identity = Path.GetFileName(path);
            if (!entries.Any(entry => entry.Identity.Equals(identity, StringComparison.OrdinalIgnoreCase)))
            {
                entries.Add(new SourceMappingEntry(identity, mode == WorkspaceMode.Source ? "source" : "package", mode == WorkspaceMode.Source ? Path.Combine(root, path) : null));
            }
        }

        if (File.Exists(Path.Combine(root, "hexalith-platform.json")))
        {
            _ = _ = entries.RemoveAll(entry => entry.Identity == "Hexalith.Platform");
            entries.Add(new SourceMappingEntry("Hexalith.Platform", "source", root));
        }

        return new SourceMapping(mode, root, Path.GetFileName(active), entries, false);
    }

    private void Apply(CompositionEffectiveDeadline deadline)
    {
        Deadline = deadline;
        _bound.CancelAfter(CompositionStartupClock.Remaining(RequestedAt + deadline.Timeout, DateTimeOffset.UtcNow));
    }
}
