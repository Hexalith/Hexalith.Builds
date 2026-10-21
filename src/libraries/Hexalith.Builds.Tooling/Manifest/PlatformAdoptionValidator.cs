// <copyright file="PlatformAdoptionValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Inspects enrollment in the active module Git workspace without initialization, resolution or writes.</summary>
public static class PlatformAdoptionValidator
{
    /// <summary>Gets the scope of this report and the required human verification.</summary>
    public const string Scope = "Bounded static checks only. Human source review is required for unsupported constructs. SM-2 remains separately unverified.";

    private static readonly string[] _minimumDependencies = ["eventstore", "tenants", "memories"];

    /// <summary>Validates every independent static requirement without changing the workspace.</summary>
    /// <param name="manifestPaths">Declarations inside the current module workspace.</param>
    /// <param name="workingDirectory">The active directory, defaulting to the process directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>All requirement rows, including failures independent of malformed inputs.</returns>
    public static async Task<PlatformAdoptionValidationResult> ValidateAsync(
        IEnumerable<string> manifestPaths,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        List<PlatformAdoptionRow> rows = [];
        List<ToolDiagnostic> diagnostics = [];
        string[] paths = [.. manifestPaths];
        string root = Path.GetFullPath(workingDirectory ?? ".");
        string[] tracked = [];
        IReadOnlyList<string> references = [];
        HashSet<string> requiredReferences = new(StringComparer.Ordinal);
        PlatformManifestValidationResult? declaration = null;
        HashSet<string> daprConfigurationKeys = new(StringComparer.OrdinalIgnoreCase);
        await CheckAsync("workspace", ".git", async () =>
        {
            CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(root, cancellationToken, "rev-parse", "--show-toplevel").ConfigureAwait(false);
            Require(Succeeded(result), "Run adoption validation in the current module Git workspace.");
            root = result.Output.TrimEnd('\r', '\n');
            result = await GitWorkspaceProcess.RunAsync(root, 64 * 1_048_576, cancellationToken, "ls-files", "-z").ConfigureAwait(false);
            Require(Succeeded(result), "Cannot enumerate tracked module-owned files.");
            tracked = [.. result.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }).ConfigureAwait(false);
        await CheckAsync("declaration", string.Join(", ", paths.Select(path => Path.GetRelativePath(root, Path.GetFullPath(path, Path.GetFullPath(workingDirectory ?? "."))).Replace('\\', '/'))), () =>
        {
            Require(paths.Length > 0, "Supply at least one module declaration.");
            string[] resolvedPaths = [.. paths.Select(path => Path.GetFullPath(path, Path.GetFullPath(workingDirectory ?? ".")))];
            foreach (string path in resolvedPaths)
            {
                Require(IsLocalFile(root, path), "Every manifest must be an existing file physically inside the active workspace.");
            }

            declaration = PlatformManifestValidator.Validate(resolvedPaths, cancellationToken);
            diagnostics.AddRange(declaration.Diagnostics);
            Require(declaration.IsValid, "Correct the declaration failures reported below.");
            foreach (JsonElement module in declaration.Declarations!.Select(item => item.Declaration))
            {
                JsonElement identity = module.GetProperty("identity");
                foreach (JsonElement role in module.GetProperty("runtime").GetProperty("dapr").EnumerateArray())
                {
                    _ = daprConfigurationKeys.Add(role.GetProperty("configurationKey").GetString()!);
                }

                string self = identity.GetProperty("moduleId").GetString()!;
                HashSet<string> dependencies = new(identity.GetProperty("dependencies").EnumerateArray().Select(value => value.GetString()!), StringComparer.Ordinal);
                Require(_minimumDependencies.Where(id => id != self).All(dependencies.Contains), "Declare the distinct minimum server dependencies eventstore, tenants and memories, excluding self.");
                Require(!dependencies.Contains(self), "A module must not depend on itself.");
            }

            return Task.CompletedTask;
        }).ConfigureAwait(false);
        await CheckAsync("references", ".gitmodules", async () =>
        {
            references = await WorkspaceRootResolver.ReadDirectReferencesAsync(root, cancellationToken).ConfigureAwait(false);
            HashSet<string> required = new(StringComparer.OrdinalIgnoreCase) { "Platform", "Builds", "McpCli", "EventStore", "Tenants", "Memories" };
            HashSet<string> self = new(StringComparer.OrdinalIgnoreCase);
            if (declaration?.Declarations is not null)
            {
                foreach (PlatformModuleDeclaration module in declaration.Declarations)
                {
                    JsonElement identity = module.Declaration.GetProperty("identity");
                    _ = self.Add(identity.GetProperty("moduleId").GetString()!);
                    foreach (JsonElement dependency in identity.GetProperty("dependencies").EnumerateArray())
                    {
                        string id = dependency.GetString()!;
                        _ = required.Add(id);
                    }
                }
            }

            string[] missing = [.. required.Where(id => !self.Contains(id) && !references.Any(reference => string.Equals(Path.GetFileName(reference), "Hexalith." + id, StringComparison.OrdinalIgnoreCase))).Order(StringComparer.Ordinal)];
            requiredReferences = new HashSet<string>(references.Where(reference => required.Contains(Path.GetFileName(reference).Replace("Hexalith.", string.Empty, StringComparison.OrdinalIgnoreCase)) && !self.Contains(Path.GetFileName(reference).Replace("Hexalith.", string.Empty, StringComparison.OrdinalIgnoreCase))), StringComparer.Ordinal);
            Require(!references.Any(reference => self.Contains(Path.GetFileName(reference).Replace("Hexalith.", string.Empty, StringComparison.OrdinalIgnoreCase))), "Remove the declared direct self reference; the active module must not reference itself.");
            Require(missing.Length == 0, "Declare distinct direct references for: " + string.Join(", ", missing) + ". Self references are excluded.");
        }).ConfigureAwait(false);
        await CheckAsync("gitlinks", ".gitmodules", async () =>
        {
            Require(references.Count > 0, "No direct references were available for recorded-gitlink inspection.");
            foreach (string reference in references)
            {
                IReadOnlyDictionary<string, string> recorded = await SubmoduleInitializer.ValidateRecordedGitlinksAsync(root, [reference], cancellationToken).ConfigureAwait(false);
                Require(!requiredReferences.Contains(reference) || SubmoduleInitializer.IsInitialized(Path.Combine(root, reference)), "Initialize only the recorded required direct reference: " + reference + ".");
                await SubmoduleInitializer.ValidateInitializedPackageRootsAsync(root, [reference], recorded, cancellationToken).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
        await CheckAsync("nested-references", ".gitmodules", async () =>
        {
            // Read independently, so malformed declarations never suppress nested-checkout inspection.
            IReadOnlyList<string> direct = await WorkspaceRootResolver.ReadDirectReferencesAsync(root, cancellationToken).ConfigureAwait(false);
            await SubmoduleInitializer.CheckNestedReferencesAsync(root, direct, cancellationToken).ConfigureAwait(false);
        }).ConfigureAwait(false);
        await CheckAsync("tool-pin", ".config/dotnet-tools.json", async () =>
        {
            string? platformReference = references.FirstOrDefault(reference => string.Equals(Path.GetFileName(reference), "Hexalith.Platform", StringComparison.OrdinalIgnoreCase));
            Require(platformReference is not null, "Declare the direct Platform reference before checking its accepted tool pin.");
            string platform = Path.Combine(root, platformReference!);
            string localPin = ReadPin(root, root);
            string platformPin = ReadPin(platform, root);
            Require(localPin == platformPin, "The tool pin must exactly match the direct Platform checkout's accepted tool pin.");
            CompositionProcessResult record = await GitWorkspaceProcess.RunAsync(platform, cancellationToken, "show", "HEAD:.config/dotnet-tools.json").ConfigureAwait(false);
            Require(Succeeded(record), "Cannot read the direct Platform checkout's committed tool pin.");
            using JsonDocument committed = JsonDocument.Parse(record.Output);
            Require(localPin == Pin(committed.RootElement), "The direct Platform tool manifest differs from its committed identity.");
        }).ConfigureAwait(false);
        HashSet<string> frozen = new(StringComparer.Ordinal);
        await CheckAsync("hosting-inventory", "module/hosting-inventory.json", () =>
        {
            frozen = PlatformHostingInventory.Validate(root, tracked);
            return Task.CompletedTask;
        }).ConfigureAwait(false);
        foreach (string requirement in new[] { "dependency-selectors", "dapr-subscriptions" })
        {
            int before = rows.Count;
            try
            {
                Require(tracked.Length > 0, "No tracked module files were available for bounded static inspection.");
                rows.AddRange(PlatformAdoptionSourceAudit.Scan(root, tracked, frozen, daprConfigurationKeys, requirement, cancellationToken));
                if (rows.Count == before)
                {
                    rows.Add(new PlatformAdoptionRow(requirement, true, ".", "No known forbidden patterns in tracked module-owned files. " + Scope));
                }
            }
            catch (Exception exception) when (InspectionFailure(exception))
            {
                rows.Add(new PlatformAdoptionRow(requirement, false, ".", exception.Message));
            }
        }

        foreach (PlatformAdoptionRow row in rows.Where(row => !row.Passed))
        {
            diagnostics.Add(new ToolDiagnostic("HXA001", ToolPhase.Manifest, ToolFailureCategory.Manifest, row.Reason, row.Requirement, Source: row.File));
        }

        return new PlatformAdoptionValidationResult(rows.AsReadOnly(), diagnostics.AsReadOnly());

        async Task CheckAsync(string requirement, string file, Func<Task> action)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await action().ConfigureAwait(false);
                rows.Add(new PlatformAdoptionRow(requirement, true, file, "Static requirement passed. " + Scope));
            }
            catch (WorkspaceMappingException exception)
            {
                foreach (ToolDiagnostic diagnostic in exception.Diagnostics)
                {
                    string source = diagnostic.Source ?? diagnostic.Field ?? file;
                    if (Path.IsPathFullyQualified(source))
                    {
                        source = Path.GetRelativePath(root, source).Replace('\\', '/');
                    }

                    string reason = string.IsNullOrWhiteSpace(diagnostic.Hint) ? diagnostic.Message : diagnostic.Message + " " + diagnostic.Hint;
                    rows.Add(new PlatformAdoptionRow(requirement, false, source, reason));
                }
            }
            catch (Exception exception) when (InspectionFailure(exception))
            {
                rows.Add(new PlatformAdoptionRow(requirement, false, file, exception.Message));
            }
        }
    }

    /// <summary>Checks physical confinement and existence without opening an external target.</summary>
    /// <param name="root">The owning root.</param>
    /// <param name="path">The candidate path.</param>
    /// <returns>Whether the candidate is a local existing file.</returns>
    internal static bool IsLocalFile(string root, string path)
    {
        string relative = Path.GetRelativePath(root, Path.GetFullPath(path, root));
        return RepositoryPathResolver.TryResolveExistingFile(root, relative, out _);
    }

    /// <summary>Raises a structured inspection failure for an unmet requirement.</summary>
    /// <param name="condition">The checked condition.</param>
    /// <param name="reason">The metadata-only reason.</param>
    /// <exception cref="InvalidDataException">The requirement is not met.</exception>
    internal static void Require(bool condition, string reason)
    {
        if (!condition)
        {
            throw new InvalidDataException(reason);
        }
    }

    private static bool InspectionFailure(Exception exception) => exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException or System.Xml.XmlException or InvalidOperationException or KeyNotFoundException;

    private static bool Succeeded(CompositionProcessResult result) => result.Started && !result.TimedOut && !result.OutputTruncated && result.ExitCode == 0;

    private static string ReadPin(string root, string workspaceRoot)
    {
        const string path = ".config/dotnet-tools.json";
        if (!IsLocalFile(root, path))
        {
            string relative = Path.GetRelativePath(workspaceRoot, Path.Combine(root, path)).Replace('\\', '/');
            throw new WorkspaceMappingException(new ToolDiagnostic("HXA001", ToolPhase.Manifest, ToolFailureCategory.Manifest, "Supply a readable tool manifest inside the workspace at " + relative + ".", Source: relative));
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, path)));
        return Pin(document.RootElement);
    }

    private static string Pin(JsonElement root)
    {
        Require(root.ValueKind == JsonValueKind.Object && root.TryGetProperty("tools", out JsonElement tools) && tools.ValueKind == JsonValueKind.Object && tools.TryGetProperty("hexalith.builds.module.cli", out _), "Pin hexalith.builds.module.cli in .config/dotnet-tools.json.");
        JsonElement tool = root.GetProperty("tools").GetProperty("hexalith.builds.module.cli");
        Require(tool.ValueKind == JsonValueKind.Object && tool.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(version.GetString()), "An exact tool version is required.");
        Require(!tool.TryGetProperty("rollForward", out JsonElement rollForward) || rollForward.ValueKind == JsonValueKind.False, "Disable tool roll-forward for exact accepted-tool restore.");
        Require(
            tool.TryGetProperty("commands", out JsonElement commands) && commands.ValueKind == JsonValueKind.Array
                && commands.GetArrayLength() == 1 && commands[0].ValueKind == JsonValueKind.String && commands[0].GetString() == "hexalith-module",
            "The tool pin must expose the runnable hexalith-module command.");
        string pin = tool.GetProperty("version").GetString()!;
        Require(NuGet.Versioning.NuGetVersion.TryParse(pin, out _), "Use one exact semantic tool version.");
        return pin;
    }
}
