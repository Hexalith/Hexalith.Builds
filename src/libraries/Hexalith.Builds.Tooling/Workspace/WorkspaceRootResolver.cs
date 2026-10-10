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
        string directory = ExistingAncestor(Path.GetDirectoryName(manifest)!);
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(directory, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        return await ResolveAsync(manifest, mode, probe, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Resolves from a completed initial Git probe, including an unavailable executable.</summary>
    /// <param name="manifestPath">The validated manifest path.</param>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="probe">The initial Git probe result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="WorkspaceMappingException">The active root or a required reference is invalid.</exception>
    internal static async Task<SourceMapping> ResolveAsync(string manifestPath, WorkspaceMode mode, CompositionProcessResult probe, CancellationToken cancellationToken)
    {
        string manifest = Path.GetFullPath(manifestPath);
        string directory = ExistingAncestor(Path.GetDirectoryName(manifest)!);
        if (mode is not (WorkspaceMode.Source or WorkspaceMode.Package))
        {
            throw Failure("HXW001", $"Unsupported workspace mode '{mode}'.", manifest);
        }

        if (probe.TimedOut || probe.OutputTruncated)
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

        string root = Path.GetFullPath(TrimGitLineEnding(probe.Output));
        while (true)
        {
            CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
            if (!parent.Started || parent.TimedOut || parent.OutputTruncated || parent.ExitCode != 0)
            {
                throw Failure("HXW001", $"Cannot inspect parent Git workspace of '{root}'.", root);
            }

            if (string.IsNullOrWhiteSpace(parent.Output))
            {
                break;
            }

            string next = Path.GetFullPath(TrimGitLineEnding(parent.Output));
            if (next == root)
            {
                break;
            }

            root = next;
        }

        string declaredManifest = Path.GetRelativePath(root, manifest);
        if (!RepositoryPathResolver.TryResolvePathWithinRoot(root, declaredManifest, out _))
        {
            throw Failure("HXW001", $"Manifest '{manifest}' physically escapes active root '{root}'.", manifest);
        }

        IReadOnlyList<string> references = await ReadDirectReferencesAsync(root, cancellationToken).ConfigureAwait(false);
        string relativeManifest = declaredManifest.Replace('\\', '/');
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
        if (!RepositoryPathResolver.TryResolvePathWithinRoot(root, Path.GetRelativePath(root, activeRoot), out _))
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
        string? emptySuffix = entries.Select(entry => entry.Identity)
            .FirstOrDefault(identity => SourceMapping.PropertySuffix(identity).Length == 0);
        if (emptySuffix is not null)
        {
            throw Failure("HXW002", $"Identity '{emptySuffix}' generates an empty MSBuild property suffix.", root);
        }

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

        foreach (string reference in references)
        {
            string path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path) && !RepositoryPathResolver.TryResolvePathWithinRoot(root, reference, out _))
            {
                throw Failure("HXW002", $"Direct reference '{Path.GetFileName(reference)}' at '{path}' physically escapes active root '{root}'.", path);
            }
        }

        await SubmoduleInitializer.CheckNestedReferencesAsync(root, references, cancellationToken).ConfigureAwait(false);
        IReadOnlyDictionary<string, string> recordedGitlinks = await SubmoduleInitializer.ValidateRecordedGitlinksAsync(root, references, cancellationToken).ConfigureAwait(false);
        if (mode == WorkspaceMode.Source)
        {
            await SubmoduleInitializer.InitializeDirectAsync(root, references, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await SubmoduleInitializer.ValidateInitializedPackageRootsAsync(root, references.Where(reference => reference != activeReference), recordedGitlinks, cancellationToken).ConfigureAwait(false);
            if (activeReference is not null)
            {
                await SubmoduleInitializer.InitializeDirectAsync(root, [activeReference], cancellationToken).ConfigureAwait(false);
            }
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(root, declaredManifest, out string physicalManifest)
            || !RepositoryPathResolver.TryResolvePathWithinRoot(root, ".", out string physicalRoot))
        {
            throw Failure("HXW001", $"Manifest '{manifest}' physically escapes active root '{root}' or is unavailable after checkout.", manifest);
        }

        string physicalRelative = Path.GetRelativePath(physicalRoot, physicalManifest).Replace('\\', '/');
        string? physicalReference = references.FirstOrDefault(reference => physicalRelative.StartsWith(reference + "/", pathComparison));
#pragma warning disable IDE0046 // Keep a single diagnostic for both physical ownership failures.
        if ((physicalRelative.StartsWith("references/", pathComparison) && physicalReference is null)
            || !string.Equals(activeReference, physicalReference, StringComparison.Ordinal))
        {
            throw Failure("HXW001", $"Manifest '{manifest}' does not physically belong to its declared active module '{activeRoot}'.", manifest);
        }
#pragma warning restore IDE0046

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
            if (new FileInfo(gitmodules).LinkTarget is not null)
            {
                throw Failure("HXW002", $"Reference declarations '{gitmodules}' point to a missing target.", gitmodules);
            }

            CompositionProcessResult tracked = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-files", "--stage", "-z", "--", ".gitmodules", "references").ConfigureAwait(false);
            if (!tracked.Started || tracked.TimedOut || tracked.OutputTruncated || tracked.ExitCode != 0)
            {
                throw Failure("HXW002", $"Cannot inspect tracked reference declarations in '{root}'.", gitmodules);
            }

#pragma warning disable IDE0046 // Keep separate diagnostics for Git inspection and missing working-tree declarations.
            if (!string.IsNullOrEmpty(tracked.Output)
                && tracked.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Any(record =>
                {
                    int separator = record.IndexOf('\t', StringComparison.Ordinal);
                    if (separator < 0)
                    {
                        throw Failure("HXW002", $"Invalid tracked reference record in '{root}'.", gitmodules);
                    }

                    string path = record[(separator + 1)..];
                    return path == ".gitmodules" || (record.StartsWith("160000 ", StringComparison.Ordinal) && path.StartsWith("references/", StringComparison.Ordinal));
                }))
            {
                throw Failure("HXW002", $"Working-tree '{gitmodules}' is missing while Git still tracks direct reference declarations or gitlinks.", gitmodules);
            }
#pragma warning restore IDE0046

            return [];
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(root, ".gitmodules", out _))
        {
            throw Failure("HXW002", $"Reference declarations '{gitmodules}' physically escape active root '{root}'.", gitmodules);
        }

        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "config", "-z", "--file", gitmodules, "--get-regexp", "^submodule\\..*\\.path$").ConfigureAwait(false);
        if (!result.Started || result.TimedOut || result.OutputTruncated || (result.ExitCode != 0 && result.ExitCode != 1))
        {
            throw Failure("HXW002", $"Cannot read direct references from '{gitmodules}'.", gitmodules);
        }

        List<string> references = [];
        foreach (string record in result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = record.IndexOf('\n', StringComparison.Ordinal);
            if (separator < 0)
            {
                throw Failure("HXW002", $"Invalid reference declaration in '{gitmodules}': '{record}'.", gitmodules);
            }

            string path = record[(separator + 1)..];
            string[] parts = path.Split('/');
            if (path.Contains('\\', StringComparison.Ordinal))
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
                    string absolute = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
                    throw Failure("HXW002", $"Direct reference '{Path.GetFileName(path)}' at '{absolute}' physically escapes active root '{root}'.", absolute);
                }

                references.Add(path);
            }
        }

        return references.Count != references.Distinct(StringComparer.OrdinalIgnoreCase).Count()
            || references.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Count
                ? throw Failure("HXW002", $"Duplicate direct reference identity in '{gitmodules}'.", gitmodules)
                : [.. references.Order(StringComparer.Ordinal)];
    }

    /// <summary>Determines whether a nonexecutable manifest is physically in the active root before checkout.</summary>
    /// <param name="manifestPath">The manifest file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the manifest is physically in the active root.</returns>
    internal static async Task<bool> IsRootManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        string manifest = Path.GetFullPath(manifestPath);
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(Path.GetDirectoryName(manifest)!, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        string root;
        if (!probe.Started || probe.TimedOut || probe.OutputTruncated || probe.ExitCode != 0 || string.IsNullOrWhiteSpace(probe.Output))
        {
            if (HasGitMarker(Path.GetDirectoryName(manifest)!))
            {
                return false;
            }

            root = Path.GetFullPath(ManifestPathValidator.FindRepositoryRoot(manifest));
        }
        else
        {
            root = Path.GetFullPath(TrimGitLineEnding(probe.Output));
            while (true)
            {
                CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
                if (!parent.Started || parent.TimedOut || parent.OutputTruncated || parent.ExitCode != 0)
                {
                    return false;
                }

                if (string.IsNullOrWhiteSpace(parent.Output))
                {
                    break;
                }

                string next = Path.GetFullPath(TrimGitLineEnding(parent.Output));
                if (next == root)
                {
                    break;
                }

                root = next;
            }
        }

        string declaredPath = Path.GetRelativePath(root, manifest);
        if (!File.Exists(manifest) && new FileInfo(manifest).LinkTarget is null
            && RepositoryPathResolver.TryResolvePathWithinRoot(root, declaredPath, out _))
        {
            string missingRelative = declaredPath.Replace('\\', '/');
            return missingRelative != ".." && !missingRelative.StartsWith("../", StringComparison.Ordinal)
                && !missingRelative.StartsWith("references/", FilesystemPathRules.Comparison(root));
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(root, declaredPath, out string physicalManifest)
            || !RepositoryPathResolver.TryResolvePathWithinRoot(root, ".", out string physicalRoot))
        {
            return false;
        }

        string relative = Path.GetRelativePath(physicalRoot, physicalManifest).Replace('\\', '/');
        return relative != ".." && !relative.StartsWith("../", StringComparison.Ordinal)
            && !relative.StartsWith("references/", FilesystemPathRules.Comparison(root));
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

    private static string ExistingAncestor(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
        }

        return directory;
    }

    private static string TrimGitLineEnding(string output)
    {
#pragma warning disable IDE0046 // Keep distinct CRLF and LF handling without nested conditionals.
        if (output.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return output[..^2];
        }
#pragma warning restore IDE0046

        return output.EndsWith('\n') ? output[..^1] : output;
    }

    private static bool HasGitMarker(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            string marker = Path.Combine(current, ".git");
            if (File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }
}
