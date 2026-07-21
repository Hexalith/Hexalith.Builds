// <copyright file="ModuleRunEvidenceFactory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using System.Diagnostics;
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
    private const int _toolTimeoutMilliseconds = 30_000;

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
        if (normalizedManifestPath is not null
            && (ManifestPathValidator.ContainsPlaceholder(normalizedManifestPath)
                || ManifestSecretDetector.ContainsSecret(normalizedManifestPath)))
        {
            normalizedManifestPath = null;
        }

        string? manifestHash = HashFile(fullManifestPath);
        ModuleProfile? selectedProfile = GetProfile(manifest, profile);
        string? profileIdentity = selectedProfile is null ? null : profile;
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
                CreateCommand(command, normalizedManifestPath, profileIdentity, filterHash),
                normalizedManifestPath,
                manifestHash,
                profileIdentity,
                fixturePath,
                fixtureHash,
                filterHash),
            CreateTopology(manifest),
            [new ModuleRunPhaseOutcome(result.Outcome.Phase, result.Outcome.Category, result.Outcome.RuleId)],
            new ModuleRunTestCounts(false, 0, 0, 0, 0),
            new SortedDictionary<string, string>(StringComparer.Ordinal),
            result.Status,
            result.Outcome,
            ["runId", "timestamps.completedUtc", "timestamps.startedUtc"],
            [],
            []);
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
        SourceControlMetadata sourceControl = ReadSourceControlMetadata(repositoryRoot);
        return new ModuleRunEnvironment(
            sourceControl.Revision,
            sourceControl.DirtyMarker,
            ReadSdkVersion(repositoryRoot),
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

    private static string ReadSdkVersion(string repositoryRoot)
    {
        string? sdkVersion = RunTool(repositoryRoot, "dotnet", "--version");
        return !string.IsNullOrWhiteSpace(sdkVersion) && SdkVersionRegex().IsMatch(sdkVersion)
            ? sdkVersion
            : Environment.Version.ToString();
    }

    private static SourceControlMetadata ReadSourceControlMetadata(string repositoryRoot)
    {
        string? revision = RunTool(repositoryRoot, "git", "rev-parse", "--verify", "HEAD");
        string? status = RunTool(repositoryRoot, "git", "status", "--porcelain", "--untracked-files=all");
        string resolvedRevision = !string.IsNullOrWhiteSpace(revision) && RevisionRegex().IsMatch(revision)
            ? revision
            : "unavailable";
        return new SourceControlMetadata(resolvedRevision, ToDirtyMarker(status));
    }

    private static string? RunTool(string workingDirectory, string fileName, params string[] arguments)
    {
        if (!Directory.Exists(workingDirectory))
        {
            return null;
        }

        try
        {
            ProcessStartInfo startInfo = new(fileName)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                WorkingDirectory = workingDirectory,
            };
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return null;
            }

            // Drain both pipes via asynchronous callbacks to avoid the classic full-buffer
            // deadlock, and bound the wait so a hung or slow child cannot block evidence
            // generation. Standard error is drained but discarded to prevent its buffer filling.
            List<string> standardOutputLines = [];
            process.OutputDataReceived += (_, eventArgs) =>
            {
                if (eventArgs.Data is not null)
                {
                    standardOutputLines.Add(eventArgs.Data);
                }
            };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(_toolTimeoutMilliseconds))
            {
                TryKill(process);
                return null;
            }

            process.WaitForExit();
            return process.ExitCode == 0 ? string.Join('\n', standardOutputLines).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process already exited between the timeout and the kill attempt.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // The operating system refused the kill; treat the tool output as unavailable.
        }
        catch (NotSupportedException)
        {
            // The platform cannot kill the process tree; treat the tool output as unavailable.
        }
    }

    private static string ToDirtyMarker(string? status) => status switch
    {
        null => "unknown",
        _ when string.IsNullOrWhiteSpace(status) => "clean",
        _ => "dirty",
    };

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

    [GeneratedRegex("^\\d+\\.\\d+\\.\\d+(?:[-+][0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SdkVersionRegex();

    private sealed record SourceControlMetadata(string Revision, string DirtyMarker);
}