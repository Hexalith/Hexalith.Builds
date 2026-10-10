// <copyright file="SourceMappingReconciliationTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Filesystem;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

/// <summary>Chooses mapped project or centrally versioned package items before restore.</summary>
public sealed class SourceMappingReconciliationTask : ITask
{
    /// <inheritdoc />
    public IBuildEngine BuildEngine { get; set; } = null!;

    /// <inheritdoc />
    public ITaskHost HostObject { get; set; } = null!;

    /// <summary>Gets or sets the persisted mapping file.</summary>
    [Required]
    public string MappingFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the command-resolved mapping hash.</summary>
    [Required]
    public string MappingHash { get; set; } = string.Empty;

    /// <summary>Gets or sets the evaluated project references.</summary>
#pragma warning disable CA1819 // MSBuild task parameters require array properties.
    public ITaskItem[] Projects { get; set; } = [];

    /// <summary>Gets or sets the evaluated package references.</summary>
    public ITaskItem[] Packages { get; set; } = [];

    /// <summary>Gets or sets the evaluated central catalog versions.</summary>
    public ITaskItem[] PackageVersions { get; set; } = [];

    /// <summary>Gets or sets the selected build configuration.</summary>
    public string Configuration { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected platform.</summary>
    public string Platform { get; set; } = string.Empty;

    /// <summary>Gets or sets the selected target framework.</summary>
    public string TargetFramework { get; set; } = string.Empty;

    /// <summary>Gets the selected project references, with the original applicable metadata.</summary>
    [Output]
    public ITaskItem[] SelectedProjects { get; private set; } = [];

    /// <summary>Gets the selected package references, with the original applicable metadata.</summary>
    [Output]
    public ITaskItem[] SelectedPackages { get; private set; } = [];
#pragma warning restore CA1819

    /// <inheritdoc />
    public bool Execute()
    {
        try
        {
            SourceMapping mapping = SourceMapping.LoadVerified(MappingFile, MappingHash);
            string root = mapping.Root;
            Dictionary<string, string> sourceProjects = new(StringComparer.OrdinalIgnoreCase);
            List<(string Identity, string Path)> packageRoots = [];
            foreach (SourceMappingEntry entry in mapping.Entries)
            {
                string identity = entry.Identity;
                if (entry.Origin == "package")
                {
                    packageRoots.Add((identity, Path.Combine(root, "references", identity)));
                    continue;
                }

                string? path = entry.Path;
                if (path is null || SamePath(path, root) || !Directory.Exists(path))
                {
                    continue;
                }

                string sourceTree = Path.Combine(path, "src");
                if (!Directory.Exists(sourceTree))
                {
                    continue;
                }

                HashSet<string> requested = Packages.Select(package => package.ItemSpec)
                    .Where(packageId => MatchesIdentity(packageId, identity))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (requested.Count == 0)
                {
                    continue;
                }

                List<string> skippedCandidates = [];
                EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (string project in Directory.EnumerateFiles(sourceTree, "*.csproj", options)
                    .Where(candidate => IsProductionCandidate(sourceTree, candidate)))
                {
                    string packageId;
                    bool packable;
                    try
                    {
                        (packageId, packable) = EvaluatePackageIdentity(project);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidProjectFileException or ArgumentException)
                    {
                        skippedCandidates.Add($"'{project}': {exception.Message}");
                        continue;
                    }

                    if (!packable || !MatchesIdentity(packageId, identity) || !requested.Contains(packageId))
                    {
                        continue;
                    }

                    if (!sourceProjects.TryAdd(packageId, project))
                    {
                        return Error("HXW002", $"Source package identity '{packageId}' is declared by both '{sourceProjects[packageId]}' and '{project}'.");
                    }
                }

                string[] missing = [.. requested.Where(packageId => !sourceProjects.ContainsKey(packageId))];
                if (missing.Length > 0 && skippedCandidates.Count > 0)
                {
                    return Error("HXW006", $"No valid source candidate for identity '{identity}' and package(s) {string.Join(", ", missing)}; skipped project evaluation errors: {string.Join("; ", skippedCandidates)}");
                }
            }

            List<ITaskItem> selectedProjects = [];
            List<ITaskItem> selectedPackages = [];
            foreach ((string identity, string packagePath) in packageRoots)
            {
                string[] absent = [.. Projects.Select(project => project.GetMetadata("FullPath"))
                    .Where(path => IsInPackageRoot(packagePath, path) && !File.Exists(path))
                    .Distinct(FilesystemPathRules.Comparison(packagePath) == StringComparison.OrdinalIgnoreCase
                        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
                if (absent.Length > 1)
                {
                    return Error("HXW006", $"Package-origin '{identity}' at '{packagePath}' has multiple absent project paths ({string.Join(", ", absent)}); the catalog cannot identify each path unambiguously.");
                }
            }

            foreach (ITaskItem project in Projects)
            {
                string path = project.GetMetadata("FullPath");
                (string Identity, string Path) packageRoot = packageRoots.FirstOrDefault(candidate => IsInPackageRoot(candidate.Path, path));
                if (packageRoot.Path is null)
                {
                    string? unsupportedRetainedMetadata = UnsupportedMetadata(project, ["SetConfiguration", "SetPlatform", "AdditionalProperties", "GlobalPropertiesToRemove"]);
                    if (unsupportedRetainedMetadata is not null)
                    {
                        return Error("HXW006", $"Retained ProjectReference '{path}' cannot override the mapped configuration because metadata '{unsupportedRetainedMetadata}' is set.");
                    }

                    selectedProjects.Add(new TaskItem(project));
                    continue;
                }

                string packageId;
                if (File.Exists(path))
                {
                    (packageId, bool packable) = EvaluatePackageIdentity(path);
                    if (!packable)
                    {
                        return Error("HXW006", $"ProjectReference '{path}' is not a packable source project and cannot become a catalog package.");
                    }
                }
                else
                {
                    string stem = Path.GetFileNameWithoutExtension(path);
                    string[] candidates = [.. PackageVersions.Select(version => version.ItemSpec)
                        .Where(id => MatchesIdentity(id, packageRoot.Identity))
                        .Distinct(StringComparer.OrdinalIgnoreCase)];
                    packageId = candidates.FirstOrDefault(id => id.Equals(stem, StringComparison.OrdinalIgnoreCase))
                        ?? (candidates.Length == 1 ? candidates[0] : string.Empty);
                    if (packageId.Length == 0)
                    {
                        return Error("HXW006", $"Uninitialized package root '{packageRoot.Path}' has no project '{path}'. Catalog candidates for '{packageRoot.Identity}' are {(candidates.Length == 0 ? "missing" : "ambiguous")}; declare one matching central PackageVersion or initialize the source to identify it.");
                    }
                }

                if (!MatchesIdentity(packageId, packageRoot.Identity))
                {
                    return Error("HXW006", $"ProjectReference '{path}' resolves inside package-origin '{packageRoot.Path}', but package identity '{packageId}' does not match '{packageRoot.Identity}'.");
                }

                string? unsupportedProjectMetadata = UnsupportedMetadata(project, ["Version", "VersionOverride", "OutputItemType", "Targets", "SetTargetFramework", "SetConfiguration", "SetPlatform", "GlobalPropertiesToRemove", "AdditionalProperties", "SkipGetTargetFrameworkProperties", "BuildReference", "Private", "ExternallyResolved", "IncludeAssets", "ExcludeAssets"]);
                if (unsupportedProjectMetadata is not null || string.Equals(project.GetMetadata("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase))
                {
                    return Error("HXW006", $"ProjectReference '{path}' cannot become package '{packageId}' because metadata '{unsupportedProjectMetadata ?? "ReferenceOutputAssembly"}' has no equivalent package behavior.");
                }

                TaskItem replacement = new(project) { ItemSpec = packageId };
                selectedPackages.Add(replacement);
            }

            foreach (ITaskItem package in Packages)
            {
                if (sourceProjects.TryGetValue(package.ItemSpec, out string? project)
                    && !selectedProjects.Any(selected => SamePath(selected.GetMetadata("FullPath"), project)))
                {
                    string? unsupportedPackageMetadata = UnsupportedMetadata(package, ["Version", "VersionOverride", "GeneratePathProperty", "IncludeAssets", "ExcludeAssets", "NoWarn", "TreatAsUsed", "PrunePackageReference", "ExcludeRestorePackageImports", "SetConfiguration", "SetPlatform", "AdditionalProperties", "GlobalPropertiesToRemove", "ReferenceOutputAssembly", "Private"]);
                    if (unsupportedPackageMetadata is not null)
                    {
                        return Error("HXW006", $"PackageReference '{package.ItemSpec}' cannot become source project '{project}' because metadata '{unsupportedPackageMetadata}' has no equivalent project behavior.");
                    }

                    TaskItem replacement = new(package) { ItemSpec = EscapeItemSpec(project) };
                    selectedProjects.Add(replacement);
                }
                else
                {
                    selectedPackages.Add(new TaskItem(package));
                }
            }

            SelectedProjects = [.. selectedProjects];
            SelectedPackages = [.. selectedPackages];
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or System.Text.Json.JsonException or InvalidProjectFileException)
        {
            return Error("HXW006", $"Source mapping item reconciliation failed: {exception.Message}");
        }
    }

    private static string EscapeItemSpec(string path) => path
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("*", "%2A", StringComparison.Ordinal)
        .Replace("?", "%3F", StringComparison.Ordinal)
        .Replace(";", "%3B", StringComparison.Ordinal);

    private static bool IsProductionCandidate(string sourceTree, string project)
    {
        string[] parts = Path.GetRelativePath(sourceTree, project).Split(Path.DirectorySeparatorChar);
        if (parts.Length < 2)
        {
            return false;
        }

        // Production layouts include src/<project> and src/libraries/<project>.
        // A matching PackageId in a utility or archived subtree must not win.
        return !parts.SelectMany((part, index) => (index == parts.Length - 1 ? Path.GetFileNameWithoutExtension(part) : part)
                .Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries))
            .Any(token => token.Equals("tool", StringComparison.OrdinalIgnoreCase)
                || token.Equals("tools", StringComparison.OrdinalIgnoreCase)
                || token.Equals("sample", StringComparison.OrdinalIgnoreCase)
                || token.Equals("samples", StringComparison.OrdinalIgnoreCase)
                || token.Equals("evidence", StringComparison.OrdinalIgnoreCase)
                || token.Equals("archive", StringComparison.OrdinalIgnoreCase)
                || token.Equals("archived", StringComparison.OrdinalIgnoreCase)
                || token.Equals("test", StringComparison.OrdinalIgnoreCase)
                || token.Equals("tests", StringComparison.OrdinalIgnoreCase)
                || token.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || token.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || token.Equals("references", StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesIdentity(string packageId, string identity) => packageId.Equals(identity, StringComparison.OrdinalIgnoreCase)
        || packageId.StartsWith(identity + ".", StringComparison.OrdinalIgnoreCase);

    private static bool SamePath(string left, string right) => string.Equals(
        Path.GetFullPath(left),
        Path.GetFullPath(right),
        FilesystemPathRules.Comparison(right));

    private static string? UnsupportedMetadata(ITaskItem item, IReadOnlyList<string> names) => names.FirstOrDefault(name => !string.IsNullOrWhiteSpace(item.GetMetadata(name)));

    private static bool IsLexicallyInside(string root, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, FilesystemPathRules.Comparison(root));
    }

    private static bool IsInPackageRoot(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        return (File.Exists(path), Directory.Exists(root)) switch
        {
            (true, _) => RepositoryPathResolver.TryResolveExistingFile(root, relative, out _),
            (false, true) => RepositoryPathResolver.TryResolvePathWithinRoot(root, relative, out _),
            _ => IsLexicallyInside(root, path),
        };
    }

    private (string PackageId, bool Packable) EvaluatePackageIdentity(string project)
    {
        using ProjectCollection projects = new();
        if (!string.IsNullOrWhiteSpace(Configuration))
        {
            projects.SetGlobalProperty("Configuration", Configuration);
        }

        if (!string.IsNullOrWhiteSpace(Platform))
        {
            projects.SetGlobalProperty("Platform", Platform);
        }

        if (!string.IsNullOrWhiteSpace(TargetFramework))
        {
            projects.SetGlobalProperty("TargetFramework", TargetFramework);
        }

        Project evaluated = projects.LoadProject(project);
        string packageId = evaluated.GetPropertyValue("PackageId");
        return (string.IsNullOrWhiteSpace(packageId) ? Path.GetFileNameWithoutExtension(project) : packageId.Trim(),
            !string.Equals(evaluated.GetPropertyValue("IsPackable"), "false", StringComparison.OrdinalIgnoreCase));
    }

    private bool Error(string ruleId, string message)
    {
        BuildEngine.LogErrorEvent(new BuildErrorEventArgs(null, ruleId, MappingFile, 0, 0, 0, 0, message, null, nameof(SourceMappingReconciliationTask)));
        return false;
    }
}
