// <copyright file="CatalogVersionValidationTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

using Microsoft.Build.Framework;

using NuGet.Versioning;

/// <summary>Rejects package versions that differ from the pinned catalog.</summary>
public sealed class CatalogVersionValidationTask : ITask
{
    /// <inheritdoc />
    public IBuildEngine BuildEngine { get; set; } = null!;

    /// <inheritdoc />
    public ITaskHost HostObject { get; set; } = null!;

    /// <summary>Gets or sets the source mapping path.</summary>
    [Required]
    public string MappingFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the command-selected mapping hash.</summary>
    [Required]
    public string MappingHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the pinned catalog snapshot path.</summary>
    [Required]
    public string CatalogFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the resolved NuGet assets path, when available.</summary>
    public string? AssetsFile { get; set; }

    /// <summary>Gets or sets a value indicating whether restored assets are checked.</summary>
    public bool CheckAssets { get; set; }

    /// <summary>Gets or sets the actual consumer project name.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Gets or sets the framework being built.</summary>
    public string TargetFramework { get; set; } = string.Empty;

    /// <summary>Gets or sets the runtime identifier being built.</summary>
    public string RuntimeIdentifier { get; set; } = string.Empty;

    /// <summary>Gets or sets the effective package references.</summary>
#pragma warning disable CA1819 // MSBuild task parameters require array properties.
    public ITaskItem[] Packages { get; set; } = [];

    /// <summary>Gets or sets the consumer's central package versions.</summary>
    public ITaskItem[] PackageVersions { get; set; } = [];
#pragma warning restore CA1819

    /// <inheritdoc />
    public bool Execute()
    {
        try
        {
            SourceMapping mapping = SourceMapping.LoadVerified(MappingFile, MappingHash);
            if (mapping.Mode != WorkspaceMode.Package)
            {
                return true;
            }

            CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, MappingHash, CatalogFile);

            IReadOnlyDictionary<string, string> selected = snapshot.ProjectVersions
                .FirstOrDefault(pair => string.Equals(pair.Key, ProjectName, StringComparison.OrdinalIgnoreCase)).Value
                ?? (string.IsNullOrWhiteSpace(ProjectName) ? snapshot.Versions : EvaluateAndRecordProject(snapshot));
            Dictionary<string, string> pinnedVersions = selected.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

            List<string> mismatches = [];
            foreach (ITaskItem package in Packages)
            {
                if (!pinnedVersions.TryGetValue(package.ItemSpec, out string? pinned))
                {
                    continue;
                }

                string resolved = package.GetMetadata("VersionOverride");
                if (string.IsNullOrWhiteSpace(resolved))
                {
                    resolved = package.GetMetadata("Version");
                }

                if (string.IsNullOrWhiteSpace(resolved))
                {
                    resolved = PackageVersions.FirstOrDefault(item => string.Equals(item.ItemSpec, package.ItemSpec, StringComparison.OrdinalIgnoreCase))?.GetMetadata("Version") ?? "unavailable";
                }

                if (DefinitelyDifferent(resolved, pinned))
                {
                    mismatches.Add($"Package '{package.ItemSpec}' resolves to {resolved}; pinned Builds catalog {snapshot.CatalogCommit} requires {pinned}.");
                }
            }

            if (CheckAssets)
            {
                if (string.IsNullOrWhiteSpace(AssetsFile) || !File.Exists(AssetsFile))
                {
                    return Error($"Restored assets are unavailable at '{AssetsFile ?? "unavailable"}' for target framework '{TargetFramework}'.");
                }

                using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(AssetsFile));
                JsonElement targets = assets.RootElement.GetProperty("targets");
                string normalizedFramework = TargetFramework;
                if (assets.RootElement.TryGetProperty("project", out JsonElement project)
                    && project.TryGetProperty("frameworks", out JsonElement frameworks))
                {
                    string? framework = frameworks.EnumerateObject().Where(item =>
                        string.Equals(item.Name, TargetFramework, StringComparison.OrdinalIgnoreCase)
                        || (item.Value.TryGetProperty("targetAlias", out JsonElement alias)
                            && string.Equals(alias.GetString(), TargetFramework, StringComparison.OrdinalIgnoreCase)))
                        .Select(item => item.Name)
                        .FirstOrDefault();
                    if (framework is not null)
                    {
                        normalizedFramework = framework;
                    }
                }

                string targetName = string.IsNullOrWhiteSpace(RuntimeIdentifier) ? normalizedFramework : normalizedFramework + "/" + RuntimeIdentifier;
                if (string.IsNullOrWhiteSpace(TargetFramework)
                    || !targets.EnumerateObject().Any(target => string.Equals(target.Name, targetName, StringComparison.OrdinalIgnoreCase)))
                {
                    return Error($"Restored assets have no active target '{targetName}'.");
                }

                JsonElement libraries = targets.EnumerateObject()
                    .First(target => string.Equals(target.Name, targetName, StringComparison.OrdinalIgnoreCase)).Value;
                HashSet<string> referenced = Packages.Select(package => package.ItemSpec).ToHashSet(StringComparer.OrdinalIgnoreCase);
                HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty library in libraries.EnumerateObject())
                {
                    int separator = library.Name.LastIndexOf('/');
                    if (separator <= 0 || !string.Equals(library.Value.GetProperty("type").GetString(), "package", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string name = library.Name[..separator];
                    string resolved = library.Name[(separator + 1)..];
                    _ = found.Add(name);
                    if (referenced.Contains(name) && pinnedVersions.TryGetValue(name, out string? pinned)
                        && !EquivalentVersion(resolved, pinned))
                    {
                        mismatches.Add($"Package '{name}' resolves to {resolved}; pinned Builds catalog {snapshot.CatalogCommit} requires {pinned}.");
                    }
                }

                foreach (string missing in referenced.Where(name => pinnedVersions.ContainsKey(name) && !found.Contains(name)))
                {
                    mismatches.Add($"Package '{missing}' is absent from active restored target '{targetName}'; pinned Builds catalog {snapshot.CatalogCommit} requires {pinnedVersions[missing]}.");
                }
            }

            foreach (string mismatch in mismatches.Distinct(StringComparer.Ordinal))
            {
                _ = Error(mismatch);
            }

            return mismatches.Count == 0;
        }
        catch (WorkspaceMappingException exception)
        {
            foreach (ToolDiagnostic diagnostic in exception.Diagnostics)
            {
                BuildEngine.LogErrorEvent(new BuildErrorEventArgs(null, diagnostic.RuleId, diagnostic.Field ?? CatalogFile, 0, 0, 0, 0, diagnostic.Message, null, nameof(CatalogVersionValidationTask)));
            }

            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException or NotSupportedException or KeyNotFoundException or InvalidOperationException)
        {
            return Error($"Pinned catalog validation failed: {exception.Message}");
        }
    }

    private static bool DefinitelyDifferent(string requested, string pinned)
    {
        if (EquivalentVersion(requested, pinned))
        {
            return false;
        }

        if (NuGetVersion.TryParse(requested, out _))
        {
            return true;
        }

        if (VersionRange.TryParse(requested, out VersionRange? range))
        {
            // Restore chooses the concrete version for a non-exact range.
            return range.MinVersion is not null && range.MaxVersion is not null
                && range.IsMinInclusive && range.IsMaxInclusive
                && VersionComparer.VersionRelease.Compare(range.MinVersion, range.MaxVersion) == 0;
        }

        return !string.IsNullOrWhiteSpace(requested) && requested != "unavailable";
    }

    private static FileStream AcquireSnapshotLock(string path)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(100);
            }
        }
    }

    private static bool EquivalentVersion(string candidate, string pinned)
    {
        NuGetVersion pin = NuGetVersion.TryParse(pinned, out NuGetVersion? parsedPin) ? parsedPin : throw new InvalidDataException($"Pinned catalog version '{pinned}' is invalid.");

        return NuGetVersion.TryParse(candidate, out NuGetVersion? version)
            ? VersionComparer.VersionRelease.Compare(version, pin) == 0
            : VersionRange.TryParse(candidate, out VersionRange? range)
                && range.MinVersion is not null && range.MaxVersion is not null
                && range.IsMinInclusive && range.IsMaxInclusive
                && VersionComparer.VersionRelease.Compare(range.MinVersion, range.MaxVersion) == 0
                && VersionComparer.VersionRelease.Compare(range.MinVersion, pin) == 0;
    }

    private IReadOnlyDictionary<string, string> EvaluateAndRecordProject(CatalogVersionSnapshot snapshot)
    {
#pragma warning disable VSTHRD002 // ITask.Execute is synchronous; the isolated process evaluator never captures this context.
        Dictionary<string, string> evaluated = CatalogVersionMaterializer.EvaluateForProjectAsync(
            CatalogFile, ProjectName, snapshot.CatalogCommit, CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        using FileStream lease = AcquireSnapshotLock(CatalogFile + ".lock");
        CatalogVersionSnapshot latest = CatalogVersionMaterializer.ReadSnapshot(CatalogFile);
        if (latest.PinnedFiles is null || latest.Versions is null || latest.ProjectVersions is null
            || latest.MappingHash != snapshot.MappingHash || latest.CatalogCommit != snapshot.CatalogCommit
            || latest.CatalogFileHash != snapshot.CatalogFileHash
            || latest.PinnedFiles.Count != snapshot.PinnedFiles.Count
            || latest.PinnedFiles.Any(pair => !snapshot.PinnedFiles.TryGetValue(pair.Key, out string? hash)
                || !string.Equals(hash, pair.Value, StringComparison.OrdinalIgnoreCase)
                || !CatalogVersionMaterializer.PinnedFileMatches(Path.Combine(Path.GetDirectoryName(CatalogFile)!, "pinned-tree"), pair))
            || latest.Versions.Count != snapshot.Versions.Count
            || latest.Versions.Any(pair => !snapshot.Versions.TryGetValue(pair.Key, out string? version) || version != pair.Value)
            || !string.Equals(latest.Hash, CatalogVersionSnapshot.ComputeHash(latest.MappingHash, latest.CatalogCommit, latest.CatalogFileHash, latest.PinnedFiles, latest.Versions, latest.ProjectVersions), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Pinned catalog snapshot changed during project evaluation.");
        }

        IReadOnlyDictionary<string, string>? present = latest.ProjectVersions
            .FirstOrDefault(pair => string.Equals(pair.Key, ProjectName, StringComparison.OrdinalIgnoreCase)).Value;
        if (present is not null)
        {
            return present.Count != evaluated.Count || present.Any(pair => !evaluated.TryGetValue(pair.Key, out string? version) || version != pair.Value)
                ? throw new InvalidDataException($"Pinned catalog evaluation changed for consumer '{ProjectName}'.")
                : present;
        }

        Dictionary<string, IReadOnlyDictionary<string, string>> projectVersions = latest.ProjectVersions
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        projectVersions.Add(ProjectName, evaluated);
        CatalogVersionSnapshot updated = latest with
        {
            ProjectVersions = projectVersions,
            Hash = CatalogVersionSnapshot.ComputeHash(latest.MappingHash, latest.CatalogCommit, latest.CatalogFileHash, latest.PinnedFiles, latest.Versions, projectVersions),
        };
        string temporary = CatalogFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(updated));
            File.Move(temporary, CatalogFile, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return evaluated;
    }

    private bool Error(string message)
    {
        BuildEngine.LogErrorEvent(new BuildErrorEventArgs(null, "HXW012", CatalogFile, 0, 0, 0, 0, message, null, nameof(CatalogVersionValidationTask)));
        return false;
    }
}