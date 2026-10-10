// <copyright file="SourceMappingValidationTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Text.Json;

using Hexalith.Builds.Tooling.Filesystem;

using Microsoft.Build.Framework;

/// <summary>Rejects package copies of source identities and project paths outside mapped roots.</summary>
public sealed class SourceMappingValidationTask : ITask
{
    /// <inheritdoc />
    public IBuildEngine BuildEngine { get; set; } = null!;

    /// <inheritdoc />
    public ITaskHost HostObject { get; set; } = null!;

    /// <summary>Gets or sets the persisted mapping path.</summary>
    [Required]
    public string MappingFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the command-resolved mapping hash.</summary>
    [Required]
    public string MappingHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the NuGet project assets path.</summary>
    [Required]
    public string AssetsFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the project references to validate.</summary>
#pragma warning disable CA1819 // MSBuild task parameters require an array property.
    public ITaskItem[] Projects { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>Gets or sets direct package references in the project.</summary>
#pragma warning disable CA1819 // MSBuild task parameters require an array property.
    public ITaskItem[] Packages { get; set; } = [];

    /// <summary>Gets or sets centrally managed package versions.</summary>
    public ITaskItem[] PackageVersions { get; set; } = [];
#pragma warning restore CA1819

    /// <summary>Gets or sets a value indicating whether this invocation validates paths before restore only.</summary>
    public bool ProjectsOnly { get; set; }

    /// <inheritdoc />
    public bool Execute()
    {
        try
        {
            SourceMapping mapping = SourceMapping.LoadVerified(MappingFile, MappingHash);
            string root = mapping.Root;
            string references = Path.Combine(root, "references");
            List<(string Identity, string Path)> sources = [];
            foreach (SourceMappingEntry entry in mapping.Entries.Where(entry => entry.Origin == "source"))
            {
                sources.Add((entry.Identity, entry.Path!));
            }

            foreach (ITaskItem project in Projects)
            {
                string requested = project.GetMetadata("FullPath");
                if (!File.Exists(requested))
                {
                    return Error("HXW006", $"Unmapped ProjectReference '{requested}' does not exist.");
                }

                bool mapped = sources.Any(source => RepositoryPathResolver.TryResolvePathWithinRoot(source.Path, Path.GetRelativePath(source.Path, requested), out _)
                    && (!SamePath(source.Path, root) || !RepositoryPathResolver.TryResolvePathWithinRoot(references, Path.GetRelativePath(references, requested), out _)));
                if (!mapped)
                {
                    return Error("HXW006", $"Unmapped ProjectReference '{requested}' is outside the active module and mapped source roots.");
                }
            }

            return CheckDirectPackages(sources) && (ProjectsOnly || CheckPackages(sources));
        }
        catch (IOException exception)
        {
            return Error("HXW006", $"Source mapping validation failed: {exception.Message}");
        }
        catch (InvalidDataException exception)
        {
            return Error("HXW006", $"Source mapping validation failed: {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            return Error("HXW006", $"Source mapping validation failed: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            return Error("HXW006", $"Source mapping validation failed: {exception.Message}");
        }
        catch (JsonException exception)
        {
            return Error("HXW006", $"Source mapping validation failed: {exception.Message}");
        }
    }

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), FilesystemPathRules.Comparison(right));

    private bool CheckDirectPackages(IReadOnlyList<(string Identity, string Path)> sources)
    {
        foreach (ITaskItem package in Packages)
        {
            foreach ((string identity, string path) in sources)
            {
                if (!package.ItemSpec.Equals(identity, StringComparison.OrdinalIgnoreCase)
                    && !package.ItemSpec.StartsWith(identity + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string version = package.GetMetadata("VersionOverride");
                if (string.IsNullOrWhiteSpace(version))
                {
                    version = package.GetMetadata("Version");
                }

                if (string.IsNullOrWhiteSpace(version))
                {
                    version = PackageVersions.FirstOrDefault(candidate => candidate.ItemSpec.Equals(package.ItemSpec, StringComparison.OrdinalIgnoreCase))?.GetMetadata("Version") ?? "unknown";
                }

                return Error("HXW005", $"Source '{identity}' at '{path}' duplicates direct package '{package.ItemSpec}/{version}'.");
            }
        }

        return true;
    }

    private bool CheckPackages(IReadOnlyList<(string Identity, string Path)> sources)
    {
        if (!File.Exists(AssetsFile))
        {
            return Error("HXW005", $"NuGet assets file '{AssetsFile}' is missing; source/package duplicates cannot be checked.");
        }

        using JsonDocument assets = JsonDocument.Parse(File.ReadAllText(AssetsFile));
        if (!assets.RootElement.TryGetProperty("libraries", out JsonElement libraries))
        {
            return Error("HXW005", $"NuGet assets file '{AssetsFile}' has no libraries; source/package duplicates cannot be checked.");
        }

        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("type", out JsonElement type) || type.GetString() != "package")
            {
                continue;
            }

            int separator = library.Name.LastIndexOf('/');
            if (separator <= 0)
            {
                continue;
            }

            string packageId = library.Name[..separator];
            foreach ((string identity, string path) in sources)
            {
                if (packageId.Equals(identity, StringComparison.OrdinalIgnoreCase)
                    || packageId.StartsWith(identity + ".", StringComparison.OrdinalIgnoreCase))
                {
                    return Error("HXW005", $"Source '{identity}' at '{path}' duplicates restored package '{library.Name}'.");
                }
            }
        }

        return true;
    }

    private bool Error(string ruleId, string message)
    {
        BuildEngine.LogErrorEvent(new BuildErrorEventArgs(null, ruleId, MappingFile, 0, 0, 0, 0, message, null, nameof(SourceMappingValidationTask)));
        return false;
    }
}
