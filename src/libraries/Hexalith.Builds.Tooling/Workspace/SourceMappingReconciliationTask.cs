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

    /// <summary>Gets or sets a value indicating whether the consumer uses centrally managed package versions.</summary>
    public bool ManagePackageVersionsCentrally { get; set; }

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
                    string packagePath = Path.Combine(root, "references", identity);
                    if (Directory.Exists(packagePath)
                        && !RepositoryPathResolver.TryResolvePathWithinRoot(root, Path.GetRelativePath(root, packagePath), out _))
                    {
                        return Error("HXW006", $"Package-origin '{identity}' at '{packagePath}' physically escapes active root '{root}'.");
                    }

                    packageRoots.Add((identity, packagePath));
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
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidProjectFileException or ArgumentException or InvalidDataException)
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
                    .Where(path => IsInPackageRoot(packagePath, path) && (!File.Exists(path) || !HasGitMarker(packagePath)))
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

                string? unsupportedProjectMetadata = UnsupportedMetadata(project, ["Version", "VersionOverride", "OutputItemType", "Targets", "SetTargetFramework", "SetConfiguration", "SetPlatform", "GlobalPropertiesToRemove", "AdditionalProperties", "SkipGetTargetFrameworkProperties", "BuildReference", "Private", "ExternallyResolved", "IncludeAssets", "ExcludeAssets", "GeneratePathProperty", "TreatAsUsed", "PrunePackageReference"]);
                if (unsupportedProjectMetadata is not null || string.Equals(project.GetMetadata("ReferenceOutputAssembly"), "false", StringComparison.OrdinalIgnoreCase))
                {
                    return Error("HXW006", $"ProjectReference '{path}' in package-origin '{packageRoot.Identity}' cannot become a package because metadata '{unsupportedProjectMetadata ?? "ReferenceOutputAssembly"}' has no equivalent package behavior.");
                }

                if (!ManagePackageVersionsCentrally)
                {
                    return Error("HXW006", $"ProjectReference '{path}' in package-origin '{packageRoot.Identity}' requires ManagePackageVersionsCentrally=true before it can become a versionless PackageReference.");
                }

                // Package mode chooses only from the central catalog. A working-tree
                // project may be stale, and its PackageId cannot select a package.
                string stem = Path.GetFileNameWithoutExtension(path);
                string[] candidates = [.. PackageVersions.Select(version => version.ItemSpec)
                    .Where(id => MatchesIdentity(id, packageRoot.Identity))
                    .Distinct(StringComparer.OrdinalIgnoreCase)];
                string packageId = candidates.FirstOrDefault(id => id.Equals(stem, StringComparison.OrdinalIgnoreCase))
                    ?? (candidates.Length == 1 ? candidates[0] : string.Empty);
                if (packageId.Length == 0)
                {
                    return Error("HXW006", $"Package-origin '{packageRoot.Identity}' at '{packageRoot.Path}' cannot identify project '{path}' from the central catalog; candidates are {(candidates.Length == 0 ? "missing" : "ambiguous")}. Declare one matching PackageVersion or use a project filename matching a catalog package ID.");
                }

                if (!MatchesIdentity(packageId, packageRoot.Identity))
                {
                    return Error("HXW006", $"ProjectReference '{path}' resolves inside package-origin '{packageRoot.Path}', but package identity '{packageId}' does not match '{packageRoot.Identity}'.");
                }

                if (Packages.Any(package => string.Equals(package.ItemSpec, packageId, StringComparison.OrdinalIgnoreCase))
                    || selectedPackages.Any(package => string.Equals(package.ItemSpec, packageId, StringComparison.OrdinalIgnoreCase)))
                {
                    return Error("HXW006", $"ProjectReference '{path}' would duplicate package '{packageId}' already declared by this project.");
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
        string projectDirectory;
        string projectFile;
        if (parts.Length == 2)
        {
            projectDirectory = parts[0];
            projectFile = parts[1];
        }
        else if (parts.Length == 3 && string.Equals(parts[0], "libraries", FilesystemPathRules.Comparison(sourceTree)))
        {
            projectDirectory = parts[1];
            projectFile = parts[2];
        }
        else
        {
            return false;
        }

        string[] utilityDirectories = ["tools", "samples", "examples", "evidence", "archive", "archives", "archived", "test", "tests", "hosts", "_bmad-output"];
        return !utilityDirectories.Contains(projectDirectory, StringComparer.OrdinalIgnoreCase)
            && string.Equals(projectDirectory, Path.GetFileNameWithoutExtension(projectFile), FilesystemPathRules.Comparison(sourceTree));
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

    private static bool HasGitMarker(string root)
    {
        string marker = Path.Combine(root, ".git");
        return File.Exists(marker) || Directory.Exists(marker) || new FileInfo(marker).LinkTarget is not null;
    }

    private (string PackageId, bool Packable) EvaluatePackageIdentity(string project)
    {
        (string packageId, bool packable, string frameworks) = EvaluateProject(project, TargetFramework);
        if (string.IsNullOrWhiteSpace(TargetFramework))
        {
            string? selectedId = null;
            bool? selectedPackable = null;
            foreach (string framework in frameworks.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                (string innerId, bool innerPackable, _) = EvaluateProject(project, framework);
                if (selectedId is not null && !string.Equals(selectedId, innerId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Project '{project}' has framework-dependent PackageId values '{selectedId}' and '{innerId}' without a selected TargetFramework.");
                }

                if (selectedPackable is not null && selectedPackable != innerPackable)
                {
                    throw new InvalidDataException(
                        $"Project '{project}' has framework-dependent IsPackable values without a selected TargetFramework.");
                }

                selectedId = innerId;
                selectedPackable = innerPackable;
            }

            packageId = selectedId ?? packageId;
            packable = selectedPackable ?? packable;
        }

        return (packageId, packable);
    }

    private (string PackageId, bool Packable, string Frameworks) EvaluateProject(string project, string framework)
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

        if (!string.IsNullOrWhiteSpace(framework))
        {
            projects.SetGlobalProperty("TargetFramework", framework);
        }

        Project evaluated = projects.LoadProject(project);
        string packageId = evaluated.GetPropertyValue("PackageId");
        return (string.IsNullOrWhiteSpace(packageId) ? Path.GetFileNameWithoutExtension(project) : packageId.Trim(),
            !string.Equals(evaluated.GetPropertyValue("IsPackable"), "false", StringComparison.OrdinalIgnoreCase),
            evaluated.GetPropertyValue("TargetFrameworks"));
    }

    private bool Error(string ruleId, string message)
    {
        BuildEngine.LogErrorEvent(new BuildErrorEventArgs(null, ruleId, MappingFile, 0, 0, 0, 0, message, null, nameof(SourceMappingReconciliationTask)));
        return false;
    }
}
