// <copyright file="WorkspaceRootResolver.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Formats.Tar;

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

    /// <summary>Resolves only preinitialized identities after explicit authority verification.</summary>
    /// <param name="manifestPath">The validated declaration.</param>
    /// <param name="authority">The independently checked managed authority.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The package mapping with truthful provenance.</returns>
    internal static async Task<SourceMapping> ResolveManagedAsync(string manifestPath, ManagedBuildAuthority authority, CancellationToken cancellationToken)
    {
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(authority.ModuleRoot, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        return await ResolveAsync(manifestPath, WorkspaceMode.Package, probe, cancellationToken, authority).ConfigureAwait(false);
    }

    /// <summary>Resolves from a completed initial Git probe, including an unavailable executable.</summary>
    /// <param name="manifestPath">The validated manifest path.</param>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="probe">The initial Git probe result.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="managedAuthority">Explicit preverified managed authority; absent preserves legacy initialization.</param>
    /// <returns>The mapping.</returns>
    /// <exception cref="WorkspaceMappingException">The active root or a required reference is invalid.</exception>
    internal static async Task<SourceMapping> ResolveAsync(string manifestPath, WorkspaceMode mode, CompositionProcessResult probe, CancellationToken cancellationToken, ManagedBuildAuthority? managedAuthority = null)
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

            string identity = StandaloneIdentity(manifest, standalone, cancellationToken);
            PlatformIdentityFinding missing = PlatformIdentityResolver.NoGit(standalone);
            return mode == WorkspaceMode.Package
                ? throw new WorkspaceMappingException(PlatformIdentityResolver.Diagnostics([missing], warning: false))
                : new SourceMapping(mode, standalone, identity, Array.AsReadOnly([new SourceMappingEntry(identity, "source", standalone)]), false)
                {
                    IdentityFindings = [missing],
                };
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
        bool missingPlatformReference = false;
        bool platformRepository = false;
        if (!references.Contains("references/Hexalith.Platform", StringComparer.Ordinal))
        {
            CompositionProcessResult marker = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-tree", "-z", "HEAD", "--", "hexalith-platform.json").ConfigureAwait(false);
            missingPlatformReference = !marker.Started || marker.TimedOut || marker.OutputTruncated || marker.ExitCode != 0
                || !PlatformIdentityResolver.IsRegularBlob(marker.Output, "hexalith-platform.json");
            platformRepository = !missingPlatformReference;
        }

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

        // A Platform-owned deployment declaration does not identify the Platform repository itself.
        string activeIdentity = activeReference is null && !platformRepository
            ? StandaloneIdentity(manifest, activeRoot, cancellationToken)
            : Path.GetFileName(activeRoot);
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

        string? reserved = entries.SelectMany(entry => GeneratedPropertyNames(entry.Identity).Select(name => (Name: name, entry.Identity))).FirstOrDefault(property => fixedNames.Contains(property.Name, StringComparer.OrdinalIgnoreCase) || ((string.Equals(property.Name, "HexalithCommonsHttpFromSource", StringComparison.OrdinalIgnoreCase) || string.Equals(property.Name, "HexalithCommonsServiceDefaultsFromSource", StringComparison.OrdinalIgnoreCase)) && !string.Equals(property.Identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase)) || (string.Equals(property.Name, "HexalithTenantsBasePath", StringComparison.OrdinalIgnoreCase) && !string.Equals(property.Identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))) is { Identity: not null } reservedProperty ? reservedProperty.Identity : null;
        if (reserved is not null)
        {
            throw Failure("HXW002", $"Identity '{reserved}' generates a reserved MSBuild property name.", root);
        }

        string? collision = entries.SelectMany(entry => GeneratedPropertyNames(entry.Identity).Select(name => (Name: name, entry.Identity))).GroupBy(property => property.Name, StringComparer.OrdinalIgnoreCase).Select(group => group.Select(property => property.Identity).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()).FirstOrDefault(identities => identities.Length > 1) is { } collidingIdentities ? string.Join(" and ", collidingIdentities.Select(identity => $"'{identity}'")) : null;
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
        IReadOnlyDictionary<string, string> recordedGitlinks = await SubmoduleInitializer.ValidateRecordedGitlinksAsync(root, references, cancellationToken, collectMissingBuilds: true).ConfigureAwait(false);
        if (missingPlatformReference)
        {
            // A non-Platform workspace must fail before initializing any direct reference, while
            // preserving independently observable Builds findings in the same result.
            (_, IReadOnlyList<PlatformIdentityFinding> missingFindings) =
                await PlatformIdentityResolver.ResolveAsync(root, references, recordedGitlinks, cancellationToken).ConfigureAwait(false);
            throw MissingPlatformReference(root, missingFindings, mode);
        }

        string[] identityReferences = [.. references.Where(reference => reference is "references/Hexalith.Platform" or "references/Hexalith.Builds")];
        IReadOnlyList<ToolDiagnostic> initializationDiagnostics = [];
        foreach (string reference in identityReferences)
        {
            string checkout = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            if (recordedGitlinks.ContainsKey(reference) && !SubmoduleInitializer.IsInitialized(checkout))
            {
                if (managedAuthority is not null)
                {
                    if (reference == "references/Hexalith.Builds" && managedAuthority.Kind == "candidate-qualification")
                    {
                        continue;
                    }

                    throw Failure("HXW012", "Managed preparation requires exact preinitialized direct identities; no initialization is permitted.", checkout);
                }

                try
                {
                    await SubmoduleInitializer.InitializeDirectAsync(root, [reference], cancellationToken).ConfigureAwait(false);
                }
                catch (WorkspaceMappingException exception)
                {
                    initializationDiagnostics = [.. initializationDiagnostics, .. exception.Diagnostics];
                }
            }
        }

        (PlatformIdentity? platformIdentity, IReadOnlyList<PlatformIdentityFinding> findings) =
            await PlatformIdentityResolver.ResolveAsync(root, references, recordedGitlinks, cancellationToken, managedAuthority: managedAuthority).ConfigureAwait(false);
        if (!references.Contains("references/Hexalith.Platform", StringComparer.Ordinal) && platformIdentity?.IsPlatformRepository != true)
        {
            throw MissingPlatformReference(root, findings, mode);
        }

        if (initializationDiagnostics.Count > 0 || (mode == WorkspaceMode.Package && findings.Count > 0))
        {
            throw new WorkspaceMappingException([.. initializationDiagnostics, .. PlatformIdentityResolver.Diagnostics(findings, warning: mode == WorkspaceMode.Source)]);
        }

        try
        {
            if (mode == WorkspaceMode.Source)
            {
                await SubmoduleInitializer.InitializeDirectAsync(root, [.. references.Except(identityReferences, StringComparer.Ordinal)], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SubmoduleInitializer.ValidateInitializedPackageRootsAsync(root, references.Where(reference => reference != activeReference && !identityReferences.Contains(reference, StringComparer.Ordinal)), recordedGitlinks, cancellationToken).ConfigureAwait(false);
                if (activeReference is not null && !identityReferences.Contains(activeReference, StringComparer.Ordinal))
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

            if (mode == WorkspaceMode.Package
                && platformIdentity?.IsPlatformRepository == true
                && string.Equals(relativeManifest, PlatformManifestCommand.OwnerManifestPath, pathComparison)
                && recordedGitlinks.SingleOrDefault(pair => string.Equals(pair.Key, "references/Hexalith.McpCli", StringComparison.OrdinalIgnoreCase)).Value is { } toolGitlink)
            {
                int tool = entries.FindIndex(entry => string.Equals(entry.Identity, "Hexalith.McpCli", StringComparison.OrdinalIgnoreCase));
                if (tool >= 0)
                {
                    entries[tool] = entries[tool] with { ToolGitlink = toolGitlink };
                }
            }

            return new SourceMapping(mode, root, activeIdentity, Array.AsReadOnly(entries.OrderBy(entry => entry.Identity, StringComparer.Ordinal).ToArray()), true)
            {
                PlatformIdentity = platformIdentity,
                IdentityFindings = findings,
                ManagedAuthority = managedAuthority,
            };
        }
        catch (WorkspaceMappingException exception) when (mode == WorkspaceMode.Source && findings.Count > 0)
        {
            throw new WorkspaceMappingException([.. exception.Diagnostics, .. PlatformIdentityResolver.Diagnostics(findings, warning: true)]);
        }
    }

    /// <summary>Prepares the package run's tool project from its exact root-declared gitlink.</summary>
    /// <param name="mapping">The verified Platform mapping.</param>
    /// <param name="repository">The tool owner repository identity.</param>
    /// <param name="projectPath">The project path relative to that repository.</param>
    /// <param name="snapshot">The private run directory for the selected commit's files.</param>
    /// <param name="cancellationToken">The startup cancellation token.</param>
    /// <returns>The private source snapshot root.</returns>
    /// <exception cref="WorkspaceMappingException">The exact direct checkout or project is unavailable.</exception>
    internal static async Task<string> PreparePackageToolCheckoutAsync(
        SourceMapping mapping,
        string repository,
        string projectPath,
        string snapshot,
        CancellationToken cancellationToken)
    {
        SourceMappingEntry? tool = mapping.Entries.SingleOrDefault(entry => string.Equals(entry.Identity, repository, StringComparison.OrdinalIgnoreCase) && entry.Origin == "package");
        string declaredReference = "references/" + (tool?.Identity ?? repository);
        string checkout = Path.GetFullPath(declaredReference, mapping.Root);
        string project = Path.Combine(checkout, projectPath);
        string? selectedGitlink = tool?.ToolGitlink;
        string? reference = (await ReadDirectReferencesAsync(mapping.Root, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(path => string.Equals(path, declaredReference, StringComparison.OrdinalIgnoreCase));
        if (mapping is not { Mode: WorkspaceMode.Package, HasGit: true, PlatformIdentity.IsPlatformRepository: true }
            || selectedGitlink is null
            || reference is null)
        {
            throw PlatformManifestCommand.MissingMcpCliProject(project, "the verified package mapping has no direct McpCli gitlink");
        }

        try
        {
            IReadOnlyDictionary<string, string> recorded = await SubmoduleInitializer.ValidateRecordedGitlinksAsync(mapping.Root, [reference], cancellationToken).ConfigureAwait(false);
            if (!string.Equals(recorded[reference], selectedGitlink, StringComparison.OrdinalIgnoreCase))
            {
                throw PlatformManifestCommand.MissingMcpCliProject(project, "the staged McpCli gitlink changed after workspace selection");
            }

            await SubmoduleInitializer.InitializeDirectAsync(mapping.Root, [reference], cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceMappingException exception)
        {
            throw new WorkspaceMappingException([.. PlatformManifestCommand.MissingMcpCliProject(project, "the direct gitlink checkout is unavailable").Diagnostics, .. exception.Diagnostics]);
        }

        CompositionProcessResult head = await GitWorkspaceProcess.RunAsync(checkout, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        CompositionProcessResult tracked = await GitWorkspaceProcess.RunAsync(checkout, cancellationToken, "ls-tree", "-z", "HEAD", "--", projectPath.Replace('\\', '/')).ConfigureAwait(false);
        CompositionProcessResult status = await GitWorkspaceProcess.RunAsync(checkout, cancellationToken, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false);
        bool verified = head.Started && !head.TimedOut && !head.OutputTruncated && head.ExitCode == 0
            && string.Equals(head.Output.Trim(), selectedGitlink, StringComparison.OrdinalIgnoreCase)
            && tracked.Started && !tracked.TimedOut && !tracked.OutputTruncated && tracked.ExitCode == 0
            && PlatformIdentityResolver.IsRegularBlob(tracked.Output, projectPath.Replace('\\', '/'))
            && status.Started && !status.TimedOut && !status.OutputTruncated && status.ExitCode == 0 && status.Output.Length == 0
            && RepositoryPathResolver.TryResolveExistingFile(checkout, projectPath, out _);
        if (!verified)
        {
            throw PlatformManifestCommand.MissingMcpCliProject(project, "the project requires a clean checkout and a tracked project at the direct gitlink");
        }

        CompositionWorkspace.CreatePrivateDirectory(snapshot);
        string archive = Path.Combine(Path.GetDirectoryName(snapshot)!, "mcpcli-source.tar");
        try
        {
            CompositionProcessResult archived = await GitWorkspaceProcess.RunAsync(
                checkout, cancellationToken, "archive", "--format=tar", "--output=" + archive, selectedGitlink).ConfigureAwait(false);
            if (!archived.Started || archived.TimedOut || archived.ExitCode != 0 || !File.Exists(archive))
            {
                throw PlatformManifestCommand.MissingMcpCliProject(project, "the selected commit could not be materialized");
            }

            await TarFile.ExtractToDirectoryAsync(archive, snapshot, overwriteFiles: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw PlatformManifestCommand.MissingMcpCliProject(project, "the selected commit could not be extracted into the private run workspace");
        }
        finally
        {
            File.Delete(archive);
        }

        return RepositoryPathResolver.TryResolveExistingFile(snapshot, projectPath, out _)
            ? snapshot
            : throw PlatformManifestCommand.MissingMcpCliProject(project, "the selected commit does not supply the tracked tool project");
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

            CompositionProcessResult tracked = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-files", "--stage", "-z", "--", ".gitmodules", ":(glob)references/*").ConfigureAwait(false);
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
                    return path == ".gitmodules" || (record.StartsWith("160000 ", StringComparison.Ordinal) && path.StartsWith("references/", FilesystemPathRules.Comparison(root)));
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

            if (string.Equals(parts[0], "references", FilesystemPathRules.Comparison(root)))
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

        string? duplicate = references.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"identity '{group.Key}' from paths {string.Join(" and ", group.Select(path => $"'{path}'"))}")
            .FirstOrDefault();
        return duplicate is not null
            ? throw Failure("HXW002", $"Duplicate direct reference {duplicate} in '{gitmodules}'.", gitmodules)
            : [.. references.Order(StringComparer.Ordinal)];
    }

    /// <summary>Determines whether a nonexecutable manifest is physically in the active root before checkout.</summary>
    /// <param name="manifestPath">The manifest file path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when the manifest is physically in the active root.</returns>
    internal static async Task<bool> IsRootManifestAsync(string manifestPath, CancellationToken cancellationToken)
    {
        string manifest = Path.GetFullPath(manifestPath);
        CompositionProcessResult probe = await GitWorkspaceProcess.RunAsync(ExistingAncestor(Path.GetDirectoryName(manifest)!), cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
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

    private static string StandaloneIdentity(string manifest, string root, CancellationToken cancellationToken)
    {
        string fallback = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar));
        if (!PlatformManifestCommand.IsV2(manifest))
        {
            return fallback;
        }

        PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifest], cancellationToken);
        if (!validation.IsValid)
        {
            throw new WorkspaceMappingException(validation.Diagnostics);
        }

        string? module = PlatformManifestCommand.ActiveModule(validation.Declarations);
        return module is null ? fallback : CompositionDeclarationClosure.Repository(module);
    }

    /// <summary>Fails on the missing Platform reference while other source-mode findings stay warnings.</summary>
    /// <param name="root">The active Git root.</param>
    /// <param name="findings">Every identity finding.</param>
    /// <param name="mode">The selected mode.</param>
    /// <returns>The refusal, led by the missing-reference failure.</returns>
    private static WorkspaceMappingException MissingPlatformReference(string root, IReadOnlyList<PlatformIdentityFinding> findings, WorkspaceMode mode)
    {
        PlatformIdentityFinding missing = PlatformIdentityResolver.MissingReference(root);
        return new WorkspaceMappingException(
        [
            .. PlatformIdentityResolver.Diagnostics([missing], warning: false),
            .. PlatformIdentityResolver.Diagnostics(findings.Where(finding => finding != missing), warning: mode == WorkspaceMode.Source),
        ]);
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
