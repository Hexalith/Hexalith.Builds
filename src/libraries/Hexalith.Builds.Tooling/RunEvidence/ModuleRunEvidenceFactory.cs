// <copyright file="ModuleRunEvidenceFactory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Builds metadata-only module-run evidence from a completed command outcome.
/// </summary>
public static partial class ModuleRunEvidenceFactory
{
    private const string _schema = "hexalith.module-run-evidence.v1";

    /// <summary>
    /// Creates deterministic evidence content with explicitly identified volatile fields.
    /// </summary>
    /// <param name="command">The invoked module command.</param>
    /// <param name="manifestPath">The supplied manifest path.</param>
    /// <param name="manifest">The validated manifest when available.</param>
    /// <param name="profile">The optional named qualification profile.</param>
    /// <param name="filter">The optional test filter, which is hashed rather than retained.</param>
    /// <param name="result">The rendered command result.</param>
    /// <param name="startedUtc">The invocation start timestamp.</param>
    /// <param name="completedUtc">The invocation completion timestamp.</param>
    /// <param name="runId">The invocation-scoped run identity.</param>
    /// <returns>The complete module-run evidence document.</returns>
    public static ModuleRunEvidence Create(
        ModuleInvocationCommand command,
        string manifestPath,
        ModuleManifest? manifest,
        string? profile,
        string? filter,
        ToolCommandResult result,
        DateTimeOffset startedUtc,
        DateTimeOffset completedUtc,
        string runId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        string? fullManifestPath = TryGetFullPath(manifestPath);
        string repositoryRoot = fullManifestPath is null
            ? Directory.GetCurrentDirectory()
            : ManifestPathValidator.FindRepositoryRoot(fullManifestPath);
        string? normalizedManifestPath = ToRepositoryRelativePath(fullManifestPath, repositoryRoot);
        string? manifestHash = HashFile(fullManifestPath);
        ModuleProfile? selectedProfile = GetProfile(manifest, profile);
        string? fixturePath = selectedProfile is null
            ? null
            : ToRepositoryRelativePath(Path.Combine(repositoryRoot, selectedProfile.Fixture), repositoryRoot);
        string? fixtureHash = selectedProfile is null
            ? null
            : HashFile(Path.Combine(repositoryRoot, selectedProfile.Fixture));
        string? filterHash = string.IsNullOrWhiteSpace(filter) ? null : HashString(filter);

        return new ModuleRunEvidence(
            _schema,
            runId,
            new ModuleRunTimestamps(startedUtc, completedUtc),
            CreateEnvironment(repositoryRoot),
            new ModuleRunInvocation(
                CreateCommand(command, normalizedManifestPath, profile, filterHash),
                normalizedManifestPath,
                manifestHash,
                profile,
                fixturePath,
                fixtureHash,
                filterHash),
            CreateTopology(manifest),
            [new ModuleRunPhaseOutcome(result.Outcome.Phase, result.Outcome.Category, result.Outcome.RuleId)],
            new ModuleRunTestCounts(false, 0, 0, 0, 0),
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            result.Status,
            result.Outcome,
            ["runId", "timestamps.completedUtc", "timestamps.startedUtc"]);
    }

    private static string CreateCommand(
        ModuleInvocationCommand command,
        string? manifestPath,
        string? profile,
        string? filterHash)
    {
        List<string> segments = ["hexalith-module", CommandName(command)];

        if (!string.IsNullOrWhiteSpace(manifestPath))
        {
            segments.Add("--manifest");
            segments.Add(manifestPath);
        }

        if (!string.IsNullOrWhiteSpace(profile))
        {
            segments.Add("--profile");
            segments.Add(profile);
        }

        if (!string.IsNullOrWhiteSpace(filterHash))
        {
            segments.Add("--filter-sha256");
            segments.Add(filterHash);
        }

        return string.Join(' ', segments);
    }

    private static string CommandName(ModuleInvocationCommand command) => command switch
    {
        ModuleInvocationCommand.Run => "run",
        ModuleInvocationCommand.Down => "down",
        ModuleInvocationCommand.Test => "test",
        _ => throw new ArgumentOutOfRangeException(nameof(command), command, "Unsupported module command."),
    };

    private static ModuleRunEnvironment CreateEnvironment(string repositoryRoot)
    {
        Assembly assembly = typeof(ModuleRunEvidenceFactory).Assembly;
        string toolVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "unavailable";
        return new ModuleRunEnvironment(
            ReadRepositoryRevision(repositoryRoot),
            "unknown",
            Environment.Version.ToString(),
            RuntimeInformation.OSDescription,
            toolVersion);
    }

    private static ModuleProfile? GetProfile(ModuleManifest? manifest, string? profile) =>
        manifest is not null &&
        !string.IsNullOrWhiteSpace(profile) &&
        manifest.Profiles.TryGetValue(profile, out ModuleProfile? selectedProfile)
            ? selectedProfile
            : null;

    private static string HashFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return "unavailable";
        }

        try
        {
            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch (IOException)
        {
            return "unavailable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unavailable";
        }
    }

    private static string HashString(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string ReadRepositoryRevision(string repositoryRoot)
    {
        string gitPath = Path.Combine(repositoryRoot, ".git");
        if (!Directory.Exists(gitPath))
        {
            return "unavailable";
        }

        try
        {
            string head = File.ReadAllText(Path.Combine(gitPath, "HEAD")).Trim();
            string revision = head.StartsWith("ref: ", StringComparison.Ordinal)
                ? File.ReadAllText(Path.Combine(gitPath, head[5..])).Trim()
                : head;
            return RevisionRegex().IsMatch(revision) ? revision : "unavailable";
        }
        catch (IOException)
        {
            return "unavailable";
        }
        catch (UnauthorizedAccessException)
        {
            return "unavailable";
        }
    }

    private static ModuleRunTopology CreateTopology(ModuleManifest? manifest)
    {
        if (manifest is null)
        {
            return new ModuleRunTopology([], null);
        }

        IReadOnlyList<ModuleRunModule> modules = [.. manifest.Modules.Select(module => new ModuleRunModule(
            module.Id,
            module.Domain,
            module.ApplicationId,
            module.ResourceId,
            module.DescriptorAssembly))];
        return new ModuleRunTopology(modules, manifest.Platform);
    }

    private static string? ToRepositoryRelativePath(string? fullPath, string repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return null;
        }

        try
        {
            string relativePath = Path.GetRelativePath(repositoryRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
            return relativePath.StartsWith("../", StringComparison.Ordinal) || string.Equals(relativePath, "..", StringComparison.Ordinal)
                ? null
                : relativePath;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    [GeneratedRegex("^[0-9a-fA-F]{40,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex RevisionRegex();
}