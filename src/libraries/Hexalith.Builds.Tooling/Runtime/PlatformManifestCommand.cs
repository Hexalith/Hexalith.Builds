// <copyright file="PlatformManifestCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Identifies declaration commands and builds their pinned Platform model.</summary>
public static class PlatformManifestCommand
{
    /// <summary>The v2 module identity whose source runs build the run-scoped McpCli tool.</summary>
    public const string McpCliModule = "mcpcli";

    private const int _maximumManifestBytes = 1_048_576;

    private const string _mcpCliRepository = "Hexalith.McpCli";

    /// <summary>Tests the document schema without converting v2 into a legacy manifest.</summary>
    /// <param name="path">The declaration path.</param>
    /// <returns>Whether the document declares v2.</returns>
    public static bool IsV2(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!stream.CanSeek || stream.Length > _maximumManifestBytes)
            {
                return false;
            }

            byte[] buffer = new byte[_maximumManifestBytes + 1];
            int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count > _maximumManifestBytes)
            {
                return false;
            }

            int offset = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(offset, count - offset));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.String
                && schema.GetString() == "hexalith.module-manifest.v2";
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Builds the pinned model without importing nested owner source trees.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The built model assembly path.</returns>
    /// <exception cref="InvalidOperationException">The model cannot be built.</exception>
    public static Task<string> BuildModelAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken) =>
        BuildModelAsync(mapping, workspace, null, cancellationToken);

    /// <summary>Builds the pinned model within the remaining startup deadline.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <param name="startupDeadlineUtc">The absolute startup deadline, or null for the default bound.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The built model assembly path.</returns>
    /// <exception cref="InvalidOperationException">The model cannot be built.</exception>
    public static async Task<string> BuildModelAsync(SourceMapping mapping, string workspace, DateTimeOffset? startupDeadlineUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        string root = mapping.PlatformIdentity?.PlatformPath
            ?? mapping.Entries.SingleOrDefault(entry => entry.Identity == "Hexalith.Platform")?.Path
            ?? throw new InvalidOperationException("Platform identity has no composition checkout path.");
        string project = Path.Combine(root, "src", "Hexalith.Platform.Composition", "Hexalith.Platform.Composition.csproj");
        if (!File.Exists(project))
        {
            throw new InvalidOperationException($"Platform model project '{project}' is unavailable.");
        }

        string pinnedCatalog = VerifiedPinnedCatalog(mapping, workspace, $"Platform model '{project}'");
        string output = Path.Combine(workspace, "hosts", "platform-model");
        List<string> arguments =
        [
            "build", project, "--configuration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release", "--output", output,
            "-p:BuildsToolingAssemblyPath=" + typeof(PlatformManifestCommand).Assembly.Location,
            "-p:NuGetAudit=false", "-p:MinVerVersionOverride=1.0.0", "-nodeReuse:false", "-p:UseSharedCompilation=false",
            .. PinnedCatalogArguments(pinnedCatalog),
        ];

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", arguments, root, null),
            CompositionStartupClock.Remaining(startupDeadlineUtc, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        string assembly = Path.Combine(output, "Hexalith.Platform.Composition.dll");
        return result.ExitCode == 0 && File.Exists(assembly)
            ? assembly
            : throw new InvalidOperationException($"Platform model '{project}' could not build: {BoundedOutput(result.Output, result.ErrorOutput)}");
    }

    /// <summary>Tests whether a v2 run builds the run-scoped McpCli tool.</summary>
    /// <param name="activeModule">The selected v2 module identity.</param>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <returns>True for an McpCli run in source mode; package-mode McpCli is not built here.</returns>
    public static bool BuildsMcpCli(string? activeModule, SourceMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return string.Equals(activeModule, McpCliModule, StringComparison.Ordinal) && mapping.Mode == WorkspaceMode.Source;
    }

    /// <summary>Gets the McpCli tool project selected by the mapping's <c>HexalithMcpCliRoot</c>, whether or not it exists.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <returns>The mapped project path, or its expected direct-reference path when McpCli is not mapped.</returns>
    public static string McpCliProjectPath(SourceMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        string root = McpCliEntry(mapping)?.Path ?? Path.Combine(mapping.Root, "references", _mcpCliRepository);
        return Path.Combine(root, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");
    }

    /// <summary>Gets the run-scoped McpCli tool assembly built into a run workspace.</summary>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <returns>The Debug tool assembly path.</returns>
    public static string McpCliToolPath(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        return Path.Combine(McpCliArtifactsPath(workspace), "bin", "Hexalith.McpCli", "debug", "Hexalith.McpCli.dll");
    }

    /// <summary>Builds the mapped McpCli checkout in Debug into the run workspace; it is never packed or published.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <param name="startupDeadlineUtc">The absolute startup deadline, or null for the default bound.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run-scoped tool assembly path.</returns>
    /// <exception cref="WorkspaceMappingException">The mapped McpCli checkout or tool project is unavailable.</exception>
    /// <exception cref="InvalidOperationException">The pinned catalog is unusable or the tool cannot build.</exception>
    public static async Task<string> BuildMcpCliAsync(SourceMapping mapping, string workspace, DateTimeOffset? startupDeadlineUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        (string root, string project) = ResolveMcpCliProject(mapping);
        string pinnedCatalog = VerifiedPinnedCatalog(mapping, workspace, $"McpCli '{project}'");
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", McpCliBuildArguments(project, workspace, pinnedCatalog), root, null),
            CompositionStartupClock.Remaining(startupDeadlineUtc, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        string tool = McpCliToolPath(workspace);
        return result.ExitCode == 0 && File.Exists(tool)
            ? tool
            : throw new InvalidOperationException($"McpCli '{project}' could not build: {BoundedOutput(result.Output, result.ErrorOutput)}");
    }

    /// <summary>Formats process diagnostics without passing log dumps to command output.</summary>
    /// <param name="output">The process standard output.</param>
    /// <param name="errorOutput">The process standard error.</param>
    /// <returns>Bounded process diagnostics.</returns>
    internal static string BoundedOutput(string output, string errorOutput)
    {
        string combined = string.Join("; ", new[] { output, errorOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                string safe = new([.. value.Select(character => char.IsControl(character) ? ' ' : character)]);
                return (safe.Length > 512 ? safe[^512..] : safe).Trim();
            }));
        return combined.Length > 1024 ? combined[..1024] : combined;
    }

    /// <summary>Uses reference composition when a valid document has no unique active declaration.</summary>
    /// <param name="declarations">The validated module set.</param>
    /// <returns>The unique active identity, or the reference default.</returns>
    internal static string? ActiveModule(IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformModuleDeclaration>? declarations) =>
        declarations is { Count: 1 } ? declarations[0].Declaration.GetProperty("identity").GetProperty("moduleId").GetString() : null;

    /// <summary>Gets the McpCli build arguments: Debug, run-workspace artifacts, the pinned catalog, no pack or publish.</summary>
    /// <remarks>
    /// The pinned-catalog arguments skip McpCli's own Directory.Packages.props, so its transitive pinning is restored explicitly:
    /// the catalog's security pins for transitive packages apply only with it.
    /// </remarks>
    /// <param name="project">The mapped McpCli tool project.</param>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <param name="pinnedCatalog">The verified pinned catalog.</param>
    /// <returns>The <c>dotnet</c> arguments.</returns>
    internal static IReadOnlyList<string> McpCliBuildArguments(string project, string workspace, string pinnedCatalog) =>
    [
        "build", project, "--configuration", "Debug", "--artifacts-path", McpCliArtifactsPath(workspace),
        "-p:GeneratePackageOnBuild=false", "-p:NuGetAudit=false", "-p:MinVerVersionOverride=1.0.0", "-nodeReuse:false", "-p:UseSharedCompilation=false",
        "-p:CentralPackageTransitivePinningEnabled=true",
        .. PinnedCatalogArguments(pinnedCatalog),
    ];

    /// <summary>Resolves the McpCli tool project only through the mapping, never through a sibling or nested copy.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <returns>The mapped checkout root and its tool project.</returns>
    /// <exception cref="WorkspaceMappingException">The mapping selects no initialized McpCli checkout with a tool project.</exception>
    internal static (string Root, string Project) ResolveMcpCliProject(SourceMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        string project = McpCliProjectPath(mapping);
        SourceMappingEntry? entry = McpCliEntry(mapping);
        string reason;
        if (entry is not { Origin: "source", Path: not null })
        {
            reason = "the source mapping selects no direct 'references/Hexalith.McpCli' checkout";
        }
        else if (!RepositoryPathResolver.TryResolveExistingFile(entry.Path, Path.GetRelativePath(entry.Path, project), out _))
        {
            reason = "the mapped checkout is uninitialized or has no McpCli tool project";
        }
        else
        {
            return (entry.Path, project);
        }

        throw new WorkspaceMappingException(new ToolDiagnostic(
            "HXW004",
            ToolPhase.Prerequisite,
            ToolFailureCategory.PrerequisiteUnavailable,
            $"Required source '{_mcpCliRepository}' at '{project}' is unavailable: {reason}. No other McpCli checkout is used.",
            project,
            "Initialize the direct references/Hexalith.McpCli checkout without --recursive, then retry; no resources were started."));
    }

    private static string McpCliArtifactsPath(string workspace) => Path.Combine(workspace, "tools", McpCliModule);

    private static SourceMappingEntry? McpCliEntry(SourceMapping mapping)
    {
        // HexalithMcpCliRoot is generated from the identity's property suffix; the resolver rejects suffix collisions.
        string suffix = SourceMapping.PropertySuffix(_mcpCliRepository);
        SourceMappingEntry[] entries = [.. mapping.Entries.Where(entry => string.Equals(SourceMapping.PropertySuffix(entry.Identity), suffix, StringComparison.OrdinalIgnoreCase))];
        return entries.Length == 1 ? entries[0] : null;
    }

    /// <summary>Imports the pinned catalog first, because owner early props need its properties, then prevents a second SDK import while retaining NuGet central-version resolution.</summary>
    /// <param name="pinnedCatalog">The verified pinned catalog.</param>
    /// <returns>The MSBuild catalog arguments.</returns>
    private static IEnumerable<string> PinnedCatalogArguments(string pinnedCatalog) =>
    [
        "-p:CustomBeforeDirectoryBuildProps=" + pinnedCatalog,
        "-p:ImportDirectoryPackagesProps=false",
        "-p:CentralPackageVersionsFileImported=true",
        "-p:DirectoryPackagesPropsPath=" + pinnedCatalog,
    ];

    private static string VerifiedPinnedCatalog(SourceMapping mapping, string workspace, string consumer)
    {
        string pinnedCatalog = Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props");
        if (!File.Exists(pinnedCatalog))
        {
            throw new InvalidOperationException($"{consumer} cannot use the selected Builds catalog because '{pinnedCatalog}' was not materialized from the Platform identity.");
        }

        try
        {
            _ = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(workspace, "source-mapping", "catalog-versions.json"));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            throw new InvalidOperationException($"{consumer} cannot use its pinned catalog: {exception.Message}", exception);
        }

        return pinnedCatalog;
    }
}
