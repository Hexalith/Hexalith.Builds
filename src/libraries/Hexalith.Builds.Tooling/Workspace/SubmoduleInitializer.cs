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
            CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "submodule", "update", "--init", "--checkout", "--", reference).ConfigureAwait(false);
            if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0)
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
            if (!recorded.Started || recorded.TimedOut || recorded.OutputTruncated || recorded.ExitCode != 0 || parts.Length < 4 || parts[0] != "160000" || parts[2] != "0"
                || !checkedOut.Started || checkedOut.TimedOut || checkedOut.OutputTruncated || checkedOut.ExitCode != 0
                || !string.Equals(parts[1], checkedOut.Output.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw Missing(identity, path, $"Checkout HEAD '{checkedOut.Output.Trim()}' does not match the root's staged gitlink '{(parts.Length >= 2 ? parts[1] : "missing")}'.");
            }
        }
    }

    /// <summary>Requires exactly one staged gitlink for every declared direct reference in either mode.</summary>
    /// <param name="root">The active superproject root.</param>
    /// <param name="references">The declared direct paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The staged commit for each validated direct reference.</returns>
    /// <exception cref="WorkspaceMappingException">A declaration has no matching stage-zero gitlink.</exception>
    internal static async Task<IReadOnlyDictionary<string, string>> ValidateRecordedGitlinksAsync(string root, IReadOnlyList<string> references, CancellationToken cancellationToken)
    {
        Dictionary<string, string> recorded = new(FilesystemPathRules.Comparison(root) == StringComparison.OrdinalIgnoreCase
            ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (string reference in references)
        {
            string identity = Path.GetFileName(reference);
            CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "ls-files", "--stage", "-z", "--", reference).ConfigureAwait(false);
            string[] records = result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
            if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0 || records.Length != 1)
            {
                throw InvalidGitlink(identity, reference, root, "expected exactly one stage-zero gitlink in the index");
            }

            int separator = records[0].IndexOf('\t', StringComparison.Ordinal);
            string[] fields = separator < 0 ? [] : records[0][..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string indexedPath = separator < 0 ? string.Empty : records[0][(separator + 1)..];
            if (fields.Length != 3 || fields[0] != "160000" || fields[2] != "0"
                || fields[1].Length is not (40 or 64) || !fields[1].All(Uri.IsHexDigit)
                || !string.Equals(indexedPath, reference, FilesystemPathRules.Comparison(root)))
            {
                throw InvalidGitlink(identity, reference, root, $"index entry '{records[0]}' is not the declared stage-zero gitlink");
            }

            recorded.Add(reference, fields[1]);
        }

        return recorded;
    }

    /// <summary>Rejects initialized package-origin checkouts that do not match their staged gitlinks.</summary>
    /// <param name="root">The active superproject root.</param>
    /// <param name="references">Package-origin direct references.</param>
    /// <param name="recorded">Validated staged gitlink commits.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for checkout validation.</returns>
    /// <exception cref="WorkspaceMappingException">An initialized checkout is stale or cannot be inspected.</exception>
    internal static async Task ValidateInitializedPackageRootsAsync(
        string root,
        IEnumerable<string> references,
        IReadOnlyDictionary<string, string> recorded,
        CancellationToken cancellationToken)
    {
        foreach (string reference in references)
        {
            string path = Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar));
            if (!IsInitialized(path))
            {
                continue;
            }

            string identity = Path.GetFileName(path);
            CompositionProcessResult checkedOut = await GitWorkspaceProcess.RunAsync(path, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
            string actual = TrimGitLineEnding(checkedOut.Output);
            if (!checkedOut.Started || checkedOut.TimedOut || checkedOut.OutputTruncated || checkedOut.ExitCode != 0
                || !string.Equals(actual, recorded[reference], StringComparison.OrdinalIgnoreCase))
            {
                throw Missing(identity, path, $"Package-origin checkout HEAD '{actual}' does not match the root's staged gitlink '{recorded[reference]}'; deinitialize the stale checkout or restore the recorded commit.");
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

    /// <summary>Inspects a direct reference's nested directories and reports filesystem failures with HXW context.</summary>
    /// <param name="root">The active superproject root.</param>
    /// <param name="repository">The direct reference path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for the inspection.</returns>
    /// <exception cref="WorkspaceMappingException">Nested ownership is invalid or cannot be inspected.</exception>
    internal static async Task CheckNestedAsync(string root, string repository, CancellationToken cancellationToken)
    {
        Stack<string> pending = new();
        pending.Push(repository);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string current = pending.Pop();
            string[] children;
            try
            {
                children = [.. Directory.EnumerateDirectories(current)];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw InvalidNestedOwnership(repository, current);
            }

            foreach (string child in children)
            {
                if (Path.GetFileName(child) == ".git")
                {
                    continue;
                }

                try
                {
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    {
                        continue;
                    }

                    if (IsInitialized(child))
                    {
                        bool owned;
                        try
                        {
                            owned = await IsOwnedSubmoduleAsync(root, repository, child, cancellationToken).ConfigureAwait(false);
                        }
                        catch (WorkspaceMappingException)
                        {
                            throw InvalidNestedOwnership(repository, child);
                        }

                        if (owned)
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
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    throw InvalidNestedOwnership(repository, child);
                }
            }
        }
    }

    private static bool IsInitialized(string path) => Directory.Exists(path)
        && (File.Exists(Path.Combine(path, ".git")) || Directory.Exists(Path.Combine(path, ".git"))
            || new FileInfo(Path.Combine(path, ".git")).LinkTarget is not null);

    private static async Task<bool> IsOwnedSubmoduleAsync(string root, string repository, string child, CancellationToken cancellationToken)
    {
        string marker = Path.Combine(child, ".git");
        if (!File.Exists(marker) && !Directory.Exists(marker) && new FileInfo(marker).LinkTarget is not null)
        {
            throw InvalidNestedOwnership(child);
        }

        CompositionProcessResult parent = await GitWorkspaceProcess.RunAsync(child, cancellationToken, "rev-parse", "--show-superproject-working-tree").ConfigureAwait(false);
        if (!parent.Started || parent.TimedOut || parent.OutputTruncated || parent.ExitCode != 0)
        {
            throw InvalidNestedOwnership(child);
        }

        if (!string.IsNullOrWhiteSpace(parent.Output)
            && RepositoryPathResolver.TryResolvePathWithinRoot(repository, Path.GetRelativePath(repository, TrimGitLineEnding(parent.Output)), out _))
        {
            return true;
        }

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
#pragma warning disable IDE0046 // Keep an explicit failure before inspecting bounded Git history.
            if (!history.Started || history.TimedOut || history.OutputTruncated || history.ExitCode != 0)
            {
                throw InvalidNestedOwnership(child);
            }
#pragma warning restore IDE0046

            return history.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Any(line => line.StartsWith(":160000 ", StringComparison.Ordinal)
                    || line.Contains(" 160000 ", StringComparison.Ordinal));
        }

        string markerText = await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false);
        if (!markerText.StartsWith("gitdir: ", StringComparison.Ordinal))
        {
            return false;
        }

        string gitDirectory = Path.GetFullPath(Path.Combine(child, TrimGitLineEnding(markerText["gitdir: ".Length..])));
        return await IsWithinGitModulesAsync(root, gitDirectory, cancellationToken).ConfigureAwait(false)
            || await IsWithinGitModulesAsync(repository, gitDirectory, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> IsWithinGitModulesAsync(string repository, string gitDirectory, CancellationToken cancellationToken)
    {
        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(repository, cancellationToken, "rev-parse", "--absolute-git-dir").ConfigureAwait(false);
        if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0
            || string.IsNullOrWhiteSpace(result.Output))
        {
            throw InvalidNestedOwnership(gitDirectory);
        }

        string modules = Path.Combine(TrimGitLineEnding(result.Output), "modules");
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

    private static WorkspaceMappingException InvalidNestedOwnership(string path) => new(new ToolDiagnostic(
        "HXW003",
        ToolPhase.Topology,
        ToolFailureCategory.TopologyOrLifecycle,
        $"Cannot inspect nested Git ownership of '{path}'.",
        path));

    private static WorkspaceMappingException InvalidNestedOwnership(string repository, string nestedPath) => new(new ToolDiagnostic(
        "HXW003",
        ToolPhase.Topology,
        ToolFailureCategory.TopologyOrLifecycle,
        $"Cannot inspect nested Git ownership of '{nestedPath}' below direct reference '{Path.GetFileName(repository)}' at '{repository}'.",
        nestedPath));

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

    private static WorkspaceMappingException InvalidGitlink(string identity, string reference, string root, string reason) => new(new ToolDiagnostic(
        "HXW002",
        ToolPhase.Topology,
        ToolFailureCategory.TopologyOrLifecycle,
        $"Direct reference '{identity}' at '{Path.Combine(root, reference)}' has no valid recorded gitlink: {reason}.",
        Path.Combine(root, reference),
        "Stage one direct submodule gitlink matching the .gitmodules declaration."));
}
