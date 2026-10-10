// <copyright file="WorkspaceRootResolver.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Finds the outermost Git superproject and its required direct references.</summary>
public static class WorkspaceRootResolver
{
    /// <summary>Resolves one immutable workspace mapping.</summary>
    /// <param name="manifestPath">The validated manifest path.</param>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="WorkspaceMappingException">The active root or a required reference is invalid.</exception>
    public static async Task<SourceMapping> ResolveAsync(string manifestPath, WorkspaceMode mode, CancellationToken cancellationToken)
    {
        string manifest = Path.GetFullPath(manifestPath);
        string directory = Path.GetDirectoryName(manifest)!;
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(directory, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        if (!probe.Started || probe.TimedOut)
        {
            throw Failure("HXW001", $"Cannot inspect Git workspace containing manifest '{manifest}': Git did not complete.", manifest);
        }

        if (probe.ExitCode != 0 || string.IsNullOrWhiteSpace(probe.Output))
        {
            if (HasGitMarker(directory))
            {
                throw Failure("HXW001", $"Cannot inspect Git workspace containing manifest '{manifest}': Git metadata is invalid or inaccessible.", manifest);
            }

            string standalone = Path.GetFullPath(ManifestPathValidator.FindRepositoryRoot(manifest));
            if (!RepositoryPathResolver.TryResolveExistingFile(standalone, Path.GetRelativePath(standalone, manifest), out _))
            {
                throw Failure("HXW001", $"Manifest '{manifest}' physically escapes active module '{standalone}' or is unavailable.", manifest);
            }

            string identity = Path.GetFileName(standalone.TrimEnd(Path.DirectorySeparatorChar));
            return new SourceMapping(mode, standalone, identity, Array.AsReadOnly([new SourceMappingEntry(identity, "source", standalone)]), false);
        }

        string root = Path.GetFullPath(probe.Output.Trim());
        while (true)
        {
            CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
            if (!parent.Started || parent.TimedOut || parent.ExitCode != 0)
            {
                throw Failure("HXW001", $"Cannot inspect parent Git workspace of '{root}'.", root);
            }

            if (string.IsNullOrWhiteSpace(parent.Output))
            {
                break;
            }

            string next = Path.GetFullPath(parent.Output.Trim());
            if (next == root)
            {
                break;
            }

            root = next;
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(root, Path.GetRelativePath(root, manifest), out string physicalManifest)
            || !RepositoryPathResolver.TryResolvePathWithinRoot(root, ".", out string physicalRoot))
        {
            throw Failure("HXW001", $"Manifest '{manifest}' physically escapes active root '{root}' or is unavailable.", manifest);
        }

        IReadOnlyList<string> references = await ReadDirectReferencesAsync(root, cancellationToken).ConfigureAwait(false);
        string relativeManifest = Path.GetRelativePath(physicalRoot, physicalManifest).Replace('\\', '/');
        if (relativeManifest == ".." || relativeManifest.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relativeManifest))
        {
            throw Failure("HXW001", $"Manifest '{manifest}' is outside active root '{root}'.", manifest);
        }

        StringComparison pathComparison = FilesystemPathRules.Comparison(root);
        string? activeReference = references.FirstOrDefault(reference => relativeManifest.StartsWith(reference + "/", pathComparison));
        if (relativeManifest.StartsWith("references/", pathComparison) && activeReference is null)
        {
            throw Failure("HXW001", $"Manifest '{manifest}' is not inside a direct reference of active root '{root}'.", manifest);
        }

        string activeRoot = activeReference is null ? root : Path.Combine(root, activeReference.Replace('/', Path.DirectorySeparatorChar));
        string activeIdentity = Path.GetFileName(activeRoot);
        if (!RepositoryPathResolver.TryResolvePathWithinRoot(root, Path.GetRelativePath(root, activeRoot) == "." ? Path.GetFileName(manifest) : Path.GetRelativePath(root, activeRoot), out _))
        {
            throw Failure("HXW001", $"Active module '{activeIdentity}' escapes active root '{root}': '{activeRoot}'.", activeRoot);
        }

        List<SourceMappingEntry> entries = [new SourceMappingEntry(activeIdentity, "source", activeRoot)];
        foreach (string reference in references)
        {
            string identity = Path.GetFileName(reference);
            if (entries.Any(entry => string.Equals(entry.Identity, identity, StringComparison.OrdinalIgnoreCase)))
            {
                if (reference == activeReference)
                {
                    continue;
                }

                throw Failure("HXW002", $"Reference '{reference}' duplicates active module identity '{activeIdentity}'.", reference);
            }

            string path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            entries.Add(mode == WorkspaceMode.Source
                ? new SourceMappingEntry(identity, "source", path)
                : new SourceMappingEntry(identity, "package", null));
        }

        string[] fixedNames =
        [
            "HexalithSourceMappingApplies", "HexalithSourceMappingMode", "HexalithSourceMappingHash",
            "HexalithSourceMappingRoot", "HexalithSourceMappingActiveModule", "HexalithSourceMappingHasGit",
        ];
        string? reserved = entries.SelectMany(entry => GeneratedPropertyNames(entry.Identity).Select(name => (Name: name, entry.Identity)))
            .FirstOrDefault(property => fixedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase)
                || ((string.Equals(property.Name, "HexalithCommonsHttpFromSource", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "HexalithCommonsServiceDefaultsFromSource", StringComparison.OrdinalIgnoreCase))
                    && !string.Equals(property.Identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
                || (string.Equals(property.Name, "HexalithTenantsBasePath", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(property.Identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))) is { Identity: not null } reservedProperty
                ? reservedProperty.Identity
                : null;
        if (reserved is not null)
        {
            throw Failure("HXW002", $"Identity '{reserved}' generates a reserved MSBuild property name.", root);
        }

        string? collision = entries
            .SelectMany(entry => GeneratedPropertyNames(entry.Identity).Select(name => (Name: name, entry.Identity)))
            .GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Select(property => property.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray())
            .FirstOrDefault(identities => identities.Length > 1) is { } collidingIdentities
                ? string.Join(" and ", collidingIdentities.Select(identity => $"'{identity}'"))
                : null;
        if (collision is not null)
        {
            throw Failure("HXW002", $"Generated MSBuild property names collide for identities {collision}.", root);
        }

        await SubmoduleInitializer.CheckNestedReferencesAsync(root, references, cancellationToken).ConfigureAwait(false);
        if (mode == WorkspaceMode.Source)
        {
            await SubmoduleInitializer.InitializeDirectAsync(root, references, cancellationToken).ConfigureAwait(false);
        }

        return new SourceMapping(mode, root, activeIdentity, Array.AsReadOnly(entries.OrderBy(entry => entry.Identity, StringComparer.Ordinal).ToArray()), true);
    }

    /// <summary>Reads direct <c>references/*</c> paths from a .gitmodules file.</summary>
    /// <param name="root">The repository root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Validated direct paths.</returns>
    /// <exception cref="WorkspaceMappingException">The direct reference declaration is invalid.</exception>
    internal static async Task<IReadOnlyList<string>> ReadDirectReferencesAsync(string root, CancellationToken cancellationToken)
    {
        string gitmodules = Path.Combine(root, ".gitmodules");
        if (!File.Exists(gitmodules))
        {
            CompositionProcessResult tracked = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-files", "--stage", "--", ".gitmodules", "references").ConfigureAwait(false);
            if (!tracked.Started || tracked.ExitCode != 0)
            {
                throw Failure("HXW002", $"Cannot inspect tracked reference declarations in '{root}'.", gitmodules);
            }

#pragma warning disable IDE0046 // Keep separate diagnostics for Git inspection and missing working-tree declarations.
            if (!string.IsNullOrWhiteSpace(tracked.Output)
                && tracked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(line => line.Contains("\t.gitmodules", StringComparison.Ordinal)
                    || (line.StartsWith("160000 ", StringComparison.Ordinal) && line.Contains("\treferences/", StringComparison.Ordinal))))
            {
                throw Failure("HXW002", $"Working-tree '{gitmodules}' is missing while Git still tracks direct reference declarations or gitlinks.", gitmodules);
            }
#pragma warning restore IDE0046

            return [];
        }

        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "config", "--file", gitmodules, "--get-regexp", "^submodule\\..*\\.path$").ConfigureAwait(false);
        if (!result.Started || (result.ExitCode != 0 && result.ExitCode != 1))
        {
            throw Failure("HXW002", $"Cannot read direct references from '{gitmodules}'.", gitmodules);
        }

        List<string> references = [];
        foreach (string line in result.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = line.IndexOf(' ', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw Failure("HXW002", $"Invalid reference declaration in '{gitmodules}': '{line}'.", gitmodules);
            }

            string path = line[(separator + 1)..].Trim();
            string[] parts = path.Split('/');
            if (path.StartsWith("references\\", StringComparison.Ordinal))
            {
                throw Failure("HXW002", $"Reference '{path}' must use a direct references/* path.", path);
            }

            if (parts[0] == "references")
            {
                if (parts.Length != 2 || parts[1].Length == 0 || parts[1] is "." or "..")
                {
                    throw Failure("HXW002", $"Reference '{path}' is not a direct references/* path.", path);
                }

                if (!RepositoryPathResolver.TryResolvePathWithinRoot(root, path, out _))
                {
                    throw Failure("HXW002", $"Reference '{path}' escapes active root '{root}'.", path);
                }

                references.Add(path);
            }
        }

        return references.Count != references.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || references.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Count
                ? throw Failure("HXW002", $"Duplicate direct reference identity in '{gitmodules}'.", gitmodules)
                : [.. references.Order(StringComparer.Ordinal)];
    }

    private static WorkspaceMappingException Failure(string rule, string message, string path) => new(new ToolDiagnostic(
        rule,
        ToolPhase.Topology,
        ToolFailureCategory.TopologyOrLifecycle,
        message,
        path,
        "Use a manifest in the active root or one of its direct references."));

    private static IEnumerable<string> GeneratedPropertyNames(string identity)
    {
        string prefix = "Hexalith" + SourceMapping.PropertySuffix(identity);
        yield return prefix + "Root";
        yield return prefix + "FromSource";
        if (string.Equals(identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
        {
            yield return "HexalithCommonsHttpFromSource";
            yield return "HexalithCommonsServiceDefaultsFromSource";
        }

        if (string.Equals(identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
        {
            yield return "HexalithTenantsBasePath";
        }
    }

    private static bool HasGitMarker(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            string marker = Path.Combine(current, ".git");
            if (File.Exists(marker) || Directory.Exists(marker))
            {
                return true;
            }
        }

        return false;
    }
}
