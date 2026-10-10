// <copyright file="SubmoduleInitializer.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Initializes only required direct references at recorded gitlinks.</summary>
internal static class SubmoduleInitializer
{
    /// <summary>Checks every initialized nested submodule before changing any direct reference.</summary>
    /// <param name="root">The active superproject root.</param>
    /// <param name="references">The required direct paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for initialization.</returns>
    /// <exception cref="WorkspaceMappingException">A nested checkout or missing direct reference prevents initialization.</exception>
    public static async Task InitializeDirectAsync(string root, IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(references);
        await CheckNestedReferencesAsync(root, references, cancellationToken).ConfigureAwait(false);

        foreach (string reference in references)
        {
            string path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            string identity = Path.GetFileName(path);
            CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "submodule", "update", "--init", "--", reference).ConfigureAwait(false);
            if (!result.Started || result.ExitCode != 0)
            {
                throw Missing(identity, path, "Git could not check out the recorded gitlink.");
            }

            if (!IsInitialized(path) || !RepositoryPathResolver.TryResolvePathWithinRoot(root, reference, out string physical) || !Directory.Exists(physical))
            {
                throw Missing(identity, path, "The required source checkout is missing after initialization.");
            }

            // Git's update command checks out the stage-zero index gitlink, which may
            // already be staged ahead of HEAD in a developer's working tree.
            CompositionProcessResult recorded = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-files", "--stage", "--", reference).ConfigureAwait(false);
            CompositionProcessResult checkedOut = await GitWorkspaceProcess.RunAsync(path, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
            string[] parts = recorded.Output.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (!recorded.Started || recorded.ExitCode != 0 || parts.Length < 4 || parts[0] != "160000" || parts[2] != "0"
                || !checkedOut.Started || checkedOut.ExitCode != 0
                || !string.Equals(parts[1], checkedOut.Output.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw Missing(identity, path, $"Checkout HEAD '{checkedOut.Output.Trim()}' does not match the root's staged gitlink '{(parts.Length >= 2 ? parts[1] : "missing")}'.");
            }
        }
    }

    /// <summary>Checks initialized nested checkouts, including paths absent from the current gitlink.</summary>
    /// <param name="root">The active superproject root.</param>
    /// <param name="references">The direct reference paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for the preflight.</returns>
    internal static async Task CheckNestedReferencesAsync(string root, IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        foreach (string reference in references)
        {
            string path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(path))
            {
                await CheckNestedAsync(root, path, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsInitialized(string path) => Directory.Exists(path)
        && (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git")));

    private static async Task CheckNestedAsync(string root, string repository, CancellationToken cancellationToken)
    {
        Stack<string> pending = new();
        pending.Push(repository);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            foreach (string child in Directory.EnumerateDirectories(current))
            {
                if (Path.GetFileName(child) == ".git")
                {
                    continue;
                }

                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                if (IsInitialized(child))
                {
                    if (await IsOwnedSubmoduleAsync(root, repository, child, cancellationToken).ConfigureAwait(false))
                    {
                        string relative = Path.GetRelativePath(repository, child);
                        throw new WorkspaceMappingException(new ToolDiagnostic(
                            "HXW003",
                            ToolPhase.Topology,
                            ToolFailureCategory.TopologyOrLifecycle,
                            $"Nested submodule '{child}' is initialized below direct reference '{repository}'.",
                            child,
                            $"Deinitialize it with git -C '{repository}' submodule deinit -- '{relative}' before retrying."));
                    }

                    // An independent Git repository owns its own descendants.
                    continue;
                }

                pending.Push(child);
            }
        }
    }

    private static async Task<bool> IsOwnedSubmoduleAsync(string root, string repository, string child, CancellationToken cancellationToken)
    {
        CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(child, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
        if (parent.Started && parent.ExitCode == 0 && !string.IsNullOrWhiteSpace(parent.Output)
            && RepositoryPathResolver.TryResolvePathWithinRoot(repository, Path.GetRelativePath(repository, parent.Output.Trim()), out _))
        {
            return true;
        }

        string marker = Path.Combine(child, ".git");
        if (!File.Exists(marker))
        {
            // Old-form submodules retain a directory-form .git marker. A prior
            // gitlink in the direct parent's history establishes ownership.
            string relative = Path.GetRelativePath(repository, child).Replace('\\', '/');
            CompositionProcessResult history = await GitWorkspaceProcess.RunAsync(
                repository,
                cancellationToken,
                "log",
                "--all",
                "--format=",
                "--raw",
                "--",
                relative).ConfigureAwait(false);
            return history.Started && history.ExitCode == 0
                && history.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Any(line => line.StartsWith(":160000 ", StringComparison.Ordinal)
                        || line.Contains(" 160000 ", StringComparison.Ordinal));
        }

        string markerText = await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false);
        if (!markerText.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            return false;
        }

        string gitDirectory = Path.GetFullPath(Path.Combine(child, markerText["gitdir: ".Length..].Trim()));
        return await IsWithinGitModulesAsync(root, gitDirectory, cancellationToken).ConfigureAwait(false)
            || await IsWithinGitModulesAsync(repository, gitDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsWithinGitModulesAsync(string repository, string gitDirectory, CancellationToken cancellationToken)
    {
        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(repository, cancellationToken, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        if (!result.Started || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.Output))
        {
            return false;
        }

        string modules = Path.Combine(result.Output.Trim(), "modules");
        if (Directory.Exists(modules)
            && RepositoryPathResolver.TryResolvePathWithinRoot(modules, Path.GetRelativePath(modules, gitDirectory), out _))
        {
            return true;
        }

        // A deleted Git directory must not turn a stale submodule marker into an
        // independent repository. The marker still identifies Git's modules tree.
        string fullModules = Path.GetFullPath(modules).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return !Directory.Exists(gitDirectory)
            && Path.GetFullPath(gitDirectory).StartsWith(fullModules, FilesystemPathRules.Comparison(repository));
    }

    private static WorkspaceMappingException Missing(string identity, string path, string reason) => new(new ToolDiagnostic(
        "HXW004",
        ToolPhase.Prerequisite,
        ToolFailureCategory.PrerequisiteUnavailable,
        $"Required source '{identity}' at '{path}' is unavailable: {reason}",
        path,
        "Check the root gitlink and local submodule source, then retry."));
}
