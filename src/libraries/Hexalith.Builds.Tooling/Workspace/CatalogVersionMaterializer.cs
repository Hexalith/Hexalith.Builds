// <copyright file="CatalogVersionMaterializer.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Evaluates the pinned catalog away from consumer MSBuild overrides.</summary>
internal static partial class CatalogVersionMaterializer
{
    /// <summary>Writes the checked catalog snapshot beside the mapping.</summary>
    /// <param name="mapping">The resolved package mapping.</param>
    /// <param name="workspace">The run workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for evaluation and writing.</returns>
#pragma warning disable RCS1140 // Fail-closed parsing and process failures share WorkspaceMappingException.
    internal static async Task WriteAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken)
    {
        if (mapping.Mode != WorkspaceMode.Package || mapping.PlatformIdentity is null)
        {
            return;
        }

        try
        {
            await WriteCoreAsync(mapping, workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            string path = Path.Combine(workspace, "source-mapping");
            throw Failure($"I/O failure while materializing the pinned catalog under '{path}': {exception.Message}", path);
        }
    }

#pragma warning restore RCS1140

    /// <summary>Evaluates one actual consumer name from the already materialized pinned tree.</summary>
    /// <param name="snapshotPath">The catalog snapshot beside the pinned tree.</param>
    /// <param name="projectName">The consumer's MSBuild project name.</param>
    /// <param name="pin">The pinned Builds commit.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The isolated package versions for that consumer.</returns>
    /// <exception cref="WorkspaceMappingException">The consumer name or pinned catalog cannot be evaluated.</exception>
    internal static async Task<Dictionary<string, string>> EvaluateForProjectAsync(string snapshotPath, string projectName, string pin, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectName) || projectName is "." or ".."
            || projectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw Failure($"Consumer project name '{projectName}' cannot be used for pinned catalog evaluation.", snapshotPath);
        }

        string root = Path.GetDirectoryName(snapshotPath) ?? throw Failure("Pinned catalog snapshot has no directory.", snapshotPath);
        string directory = Path.Combine(root, "catalog-evaluations", Guid.NewGuid().ToString("N"));
        Dictionary<string, string>? versions = null;
        Exception? failure = null;
        try
        {
            EnsureDirectory(directory);
            string catalogPath = Path.Combine(root, "pinned-tree", "Props", "Directory.Packages.props");
            versions = await EvaluateAsync(directory, catalogPath, projectName, pin, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failure = Failure($"I/O failure while evaluating the pinned catalog for '{projectName}' under '{directory}': {exception.Message}", directory);
        }
        catch (WorkspaceMappingException exception)
        {
            failure = exception;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            failure = exception;
        }

        if (!await CompositionWorkspace.TryDeleteAsync(directory, CancellationToken.None).ConfigureAwait(false))
        {
            WorkspaceMappingException cleanup = Failure($"Pinned catalog evaluation workspace '{directory}' could not be removed.", directory);
            throw failure is WorkspaceMappingException mappingFailure
                ? new WorkspaceMappingException([.. mappingFailure.Diagnostics, .. cleanup.Diagnostics])
                : cleanup;
        }

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return versions!;
    }

    private static async Task WriteCoreAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken)
    {
        string pin = mapping.PlatformIdentity?.CatalogCommit
            ?? throw Failure("Platform identity has no Builds catalog pin.", mapping.Root);
        string builds = Path.Combine(mapping.Root, "references", "Hexalith.Builds");
        string directory = Path.Combine(workspace, "source-mapping");
        EnsureDirectory(directory);
        string treeDirectory = Path.Combine(directory, "pinned-tree");
        Dictionary<string, string> pinnedFiles = new(StringComparer.Ordinal);
        _ = await MaterializeFileAsync(builds, pin, treeDirectory, "Props/Directory.Packages.props", pinnedFiles, cancellationToken).ConfigureAwait(false);
        string catalogPath = Path.Combine(treeDirectory, "Props", "Directory.Packages.props");
        IReadOnlyList<string> projectNames = ConditionalProjectNames(pinnedFiles.Keys.Select(path => (
            Path: Path.Combine(treeDirectory, path),
            Text: ReadPinnedText(Path.Combine(treeDirectory, path)))));
        Dictionary<string, string> versions = await EvaluateAsync(directory, catalogPath, "__HXW_DefaultCatalog__", pin, cancellationToken).ConfigureAwait(false);
        Dictionary<string, IReadOnlyDictionary<string, string>> projectVersions = new(StringComparer.OrdinalIgnoreCase);
        foreach (string projectName in projectNames)
        {
            projectVersions.Add(projectName, await EvaluateAsync(directory, catalogPath, projectName, pin, cancellationToken).ConfigureAwait(false));
        }

        string catalogFileHash = pinnedFiles["Props/Directory.Packages.props"];
        CatalogVersionSnapshot snapshot = new(
            mapping.ContentHash,
            pin,
            catalogFileHash,
            pinnedFiles,
            versions,
            projectVersions,
            CatalogVersionSnapshot.ComputeHash(mapping.ContentHash, pin, catalogFileHash, pinnedFiles, versions, projectVersions));
        await WritePinnedBytesAsync(Path.Combine(directory, "catalog-versions.json"), JsonSerializer.SerializeToUtf8Bytes(snapshot), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> MaterializeFileAsync(
        string builds,
        string pin,
        string treeDirectory,
        string relativePath,
        Dictionary<string, string> pinnedFiles,
        CancellationToken cancellationToken,
        bool optional = false)
    {
        string fullPath = Path.GetFullPath(Path.Combine(treeDirectory, relativePath));
        string treeRoot = Path.GetFullPath(treeDirectory) + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(treeRoot, StringComparison.Ordinal) || relativePath.Contains(':', StringComparison.Ordinal))
        {
            throw Failure($"Pinned catalog import '{relativePath}' escapes the pinned Builds tree.", treeDirectory);
        }

        string gitPath = Path.GetRelativePath(treeDirectory, fullPath).Replace('\\', '/');
        if (pinnedFiles.ContainsKey(gitPath))
        {
            return ReadPinnedText(fullPath);
        }

        if (optional)
        {
            CompositionProcessResult entry = await GitWorkspaceProcess.RunAsync(builds, cancellationToken, "ls-tree", "-z", pin, "--", gitPath).ConfigureAwait(false);
            if (!entry.Started || entry.TimedOut || entry.OutputTruncated || entry.ExitCode != 0)
            {
                throw Failure($"Pinned Builds import '{gitPath}' cannot be inspected at {pin}.", builds);
            }

            if (entry.Output.Length == 0)
            {
                return string.Empty;
            }

            if (!PlatformIdentityResolver.IsRegularBlob(entry.Output, gitPath))
            {
                throw Failure($"Pinned Builds import '{gitPath}' is not a regular blob at {pin}.", builds);
            }
        }

        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(builds, cancellationToken, "show", pin + ":" + gitPath).ConfigureAwait(false);
        if (!result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0)
        {
            throw Failure($"Pinned Builds file '{gitPath}' at {pin} cannot be read from '{builds}'.", builds);
        }

        string contents = result.Output;
        pinnedFiles.Add(gitPath, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contents))));
        EnsureDirectory(Path.GetDirectoryName(fullPath)!);
        await WritePinnedTextAsync(fullPath, contents, cancellationToken).ConfigureAwait(false);
        try
        {
            XDocument document = XDocument.Parse(contents);
            foreach (XElement element in document.Descendants().Where(element => element.Name.LocalName == "Import"))
            {
                string? imports = element.Attribute("Project")?.Value;
                if (string.IsNullOrWhiteSpace(imports))
                {
                    throw Failure($"Pinned Builds import in '{gitPath}' has no project path.", fullPath);
                }

                foreach (string import in imports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string path = import.Replace("$(MSBuildThisFileDirectory)", string.Empty, StringComparison.OrdinalIgnoreCase);
                    if (path.Contains("$(", StringComparison.Ordinal) || path.Contains('*', StringComparison.Ordinal))
                    {
                        throw Failure($"Pinned catalog import '{import}' cannot be resolved solely from the pinned tree.", fullPath);
                    }

                    string child = Path.GetRelativePath(treeDirectory, Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fullPath)!, path)));
                    bool conditional = !string.IsNullOrWhiteSpace(element.Attribute("Condition")?.Value)
                        || element.Ancestors().Any(parent => parent.Name.LocalName == "ImportGroup"
                            && !string.IsNullOrWhiteSpace(parent.Attribute("Condition")?.Value));
                    _ = await MaterializeFileAsync(builds, pin, treeDirectory, child, pinnedFiles, cancellationToken, conditional).ConfigureAwait(false);
                }
            }
        }
        catch (System.Xml.XmlException exception)
        {
            throw Failure($"Pinned Builds file '{gitPath}' is invalid XML: {exception.Message}", fullPath);
        }

        return contents;
    }

    private static IReadOnlyList<string> ConditionalProjectNames(IEnumerable<(string Path, string Text)> files)
    {
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string catalog) in files)
        {
            try
            {
                XDocument document = XDocument.Parse(catalog);
                foreach (string condition in document.Root!.DescendantsAndSelf().Attributes("Condition")
                    .Select(attribute => attribute.Value)
                    .Where(value => value.Contains("MSBuildProjectName", StringComparison.OrdinalIgnoreCase)))
                {
                    MatchCollection matches = ProjectNameCondition().Matches(condition);
                    if (matches.Count == 0)
                    {
                        // The actual consumer is evaluated on demand by the build task.
                        continue;
                    }

                    foreach (string name in matches.Select(match => match.Groups["name"].Value))
                    {
                        if (name == "__HXW_DefaultCatalog__")
                        {
                            throw new InvalidDataException($"Reserved MSBuildProjectName in condition '{condition}'.");
                        }

                        _ = names.Add(name);
                    }
                }
            }
            catch (Exception exception) when (exception is System.Xml.XmlException or InvalidDataException)
            {
                throw Failure($"Pinned catalog has an invalid project-name condition: {exception.Message}", path);
            }
        }

        return [.. names.Order(StringComparer.Ordinal)];
    }

    private static async Task<Dictionary<string, string>> EvaluateAsync(
        string directory,
        string catalogPath,
        string projectName,
        string pin,
        CancellationToken cancellationToken)
    {
        string wrapperPath = Path.Combine(directory, projectName + ".proj");
        await WritePinnedTextAsync(wrapperPath, "<Project><Import Project=\"" + XmlEscape(catalogPath) + "\" /></Project>", cancellationToken).ConfigureAwait(false);
        CompositionProcessResult evaluated = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", wrapperPath, "-nologo", "-noAutoResponse", "-getItem:PackageVersion"], directory, null),
            TimeSpan.FromMinutes(2),
            1_048_576,
            cancellationToken).ConfigureAwait(false);
        if (!evaluated.Started || evaluated.TimedOut || evaluated.OutputTruncated || evaluated.ExitCode != 0)
        {
            string detail = (evaluated.ErrorOutput + " " + evaluated.Output).Trim();
            throw Failure($"Pinned Builds catalog {pin} could not be evaluated in isolation for project '{projectName}': {detail[..Math.Min(detail.Length, 500)]}.", catalogPath);
        }

        try
        {
            using JsonDocument result = JsonDocument.Parse(evaluated.Output);
            JsonElement packages = result.RootElement.GetProperty("Items").GetProperty("PackageVersion");
            Dictionary<string, string> versions = new(StringComparer.OrdinalIgnoreCase);
            foreach (JsonElement package in packages.EnumerateArray())
            {
                string name = package.GetProperty("Identity").GetString() ?? throw new JsonException("Empty package ID.");
                string version = package.GetProperty("Version").GetString() ?? throw new JsonException("Empty catalog version.");
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version))
                {
                    throw new JsonException($"Empty package ID or catalog version for '{name}'.");
                }

                if (!versions.TryAdd(name, version))
                {
                    throw new JsonException($"Duplicate catalog package '{name}'.");
                }
            }

            return versions;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw Failure($"Pinned Builds catalog {pin} has an invalid evaluation result for project '{projectName}': {exception.Message}", catalogPath);
        }
    }

    private static void EnsureDirectory(string path)
    {
        try
        {
            _ = Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure($"I/O failure creating pinned catalog directory '{path}': {exception.Message}", path);
        }
    }

    private static string ReadPinnedText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure($"I/O failure reading pinned catalog file '{path}': {exception.Message}", path);
        }
    }

    private static async Task WritePinnedTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(path, contents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure($"I/O failure writing pinned catalog file '{path}': {exception.Message}", path);
        }
    }

    private static async Task WritePinnedBytesAsync(string path, byte[] contents, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllBytesAsync(path, contents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw Failure($"I/O failure writing pinned catalog snapshot '{path}': {exception.Message}", path);
        }
    }

    [GeneratedRegex("[\\\"']\\$\\(MSBuildProjectName\\)[\\\"']\\s*(?:==|!=)\\s*[\\\"'](?<name>[A-Za-z0-9_.-]+)[\\\"']|[\\\"'](?<name>[A-Za-z0-9_.-]+)[\\\"']\\s*(?:==|!=)\\s*[\\\"']\\$\\(MSBuildProjectName\\)[\\\"']", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProjectNameCondition();

    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static WorkspaceMappingException Failure(string message, string path) => new(new ToolDiagnostic(
        "HXW009", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, message, path));
}
