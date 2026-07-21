// <copyright file="ModuleManifestLoader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Loads and fail-closed validates <c>hexalith.module-manifest.v1</c> documents.
/// </summary>
public static partial class ModuleManifestLoader
{
    private const string _manifestSchema = "hexalith.module-manifest.v1";
    private const int _maximumManifestBytes = 1_048_576;
    private const int _maximumIdentifierLength = 63;

    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Loads a manifest and returns deterministic, metadata-only diagnostics.
    /// </summary>
    /// <param name="manifestPath">The manifest file path.</param>
    /// <returns>The manifest loading result.</returns>
    public static ManifestLoadResult Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        List<ToolDiagnostic> diagnostics = [];
        string fullManifestPath;

        try
        {
            fullManifestPath = Path.GetFullPath(manifestPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            diagnostics.Add(CreateDiagnostic("HXM004", "manifest", "Supply a valid manifest file path."));
            return CreateResult(null, diagnostics);
        }

        if (!File.Exists(fullManifestPath))
        {
            diagnostics.Add(CreateDiagnostic("HXM005", "manifest", "Supply an existing manifest file."));
            return CreateResult(null, diagnostics);
        }

        string content;
        try
        {
            using FileStream stream = File.Open(fullManifestPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > _maximumManifestBytes)
            {
                diagnostics.Add(CreateDiagnostic("HXM013", "manifest", "Keep the UTF-8 manifest at or below 1 MiB."));
                return CreateResult(null, diagnostics);
            }

            using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            char[] buffer = new char[_maximumManifestBytes + 1];
            int charactersRead = 0;
            while (charactersRead < buffer.Length)
            {
                int read = reader.Read(buffer, charactersRead, buffer.Length - charactersRead);
                if (read == 0)
                {
                    break;
                }

                charactersRead += read;
            }

            if (charactersRead > _maximumManifestBytes || reader.Peek() >= 0)
            {
                diagnostics.Add(CreateDiagnostic("HXM013", "manifest", "Keep the UTF-8 manifest at or below 1 MiB."));
                return CreateResult(null, diagnostics);
            }

            content = new string(buffer, 0, charactersRead);
        }
        catch (IOException)
        {
            diagnostics.Add(CreateDiagnostic("HXM005", "manifest", "Ensure the manifest is readable."));
            return CreateResult(null, diagnostics);
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add(CreateDiagnostic("HXM005", "manifest", "Ensure the manifest is readable."));
            return CreateResult(null, diagnostics);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
            });
            string? duplicateProperty = JsonDuplicatePropertyValidator.FindDuplicateProperty(document.RootElement);
            if (duplicateProperty is not null)
            {
                diagnostics.Add(CreateDiagnostic("HXM012", "json", "Remove duplicate JSON object properties."));
                return CreateResult(null, diagnostics);
            }
        }
        catch (JsonException)
        {
            diagnostics.Add(CreateDiagnostic("HXM011", "json", "Supply syntactically valid JSON without comments or trailing commas."));
            return CreateResult(null, diagnostics);
        }

        ModuleManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<ModuleManifest>(content, _serializerOptions);
        }
        catch (JsonException)
        {
            diagnostics.Add(CreateDiagnostic("HXM002", "schema", "Remove unknown or invalid manifest fields."));
            return CreateResult(null, diagnostics);
        }

        if (manifest is null)
        {
            diagnostics.Add(CreateDiagnostic("HXM015", "manifest", "Supply a non-empty manifest object."));
            return CreateResult(null, diagnostics);
        }

        ValidateManifest(manifest, fullManifestPath, diagnostics);
        return CreateResult(manifest, diagnostics);
    }

    private static ToolDiagnostic CreateDiagnostic(string ruleId, string field, string hint) =>
        new(
            ruleId,
            ToolPhase.Manifest,
            ToolFailureCategory.Manifest,
            MessageFor(ruleId),
            field,
            hint);

    private static ManifestLoadResult CreateResult(ModuleManifest? manifest, IEnumerable<ToolDiagnostic> diagnostics) =>
        new(
            manifest,
            [.. diagnostics
                .OrderBy(diagnostic => diagnostic.RuleId, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Field, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Hint, StringComparer.Ordinal)]);

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXM001" => "The manifest schema is unsupported.",
        "HXM002" => "The manifest contains an unknown or invalid field.",
        "HXM003" => "The manifest contains a duplicate deterministic identifier.",
        "HXM004" => "A manifest path is not canonical and repository-relative.",
        "HXM005" => "A manifest path does not resolve to a readable repository file.",
        "HXM006" => "The manifest contains an unresolved placeholder.",
        "HXM007" => "The manifest contains a prohibited secret-bearing value.",
        "HXM008" => "The manifest contains malformed module dependencies.",
        "HXM009" => "The manifest contains an invalid qualification profile.",
        "HXM010" => "The manifest contains a non-deterministic identifier.",
        "HXM011" => "The manifest JSON is invalid.",
        "HXM012" => "The manifest JSON contains a duplicate property.",
        "HXM013" => "The manifest exceeds the supported size or complexity limit.",
        "HXM014" => "The manifest contains an unsupported profile class.",
        "HXM015" => "A required manifest value is missing.",
        "HXM016" => "The manifest platform pins are not supported by this runner.",
        _ => "The manifest is invalid.",
    };

    private static void ValidateDependencies(ModuleManifest manifest, List<ToolDiagnostic> diagnostics)
    {
        HashSet<string> moduleIds = manifest.Modules
            .Where(module => module is not null && !string.IsNullOrWhiteSpace(module.Id))
            .Select(module => module.Id)
            .ToHashSet(StringComparer.Ordinal);

        for (int moduleIndex = 0; moduleIndex < manifest.Modules.Count; moduleIndex++)
        {
            ModuleDescriptor? module = manifest.Modules[moduleIndex];
            if (module is null)
            {
                continue;
            }

            string fieldPrefix = $"modules[{moduleIndex}]";
            if (module.Dependencies is null)
            {
                diagnostics.Add(CreateDiagnostic("HXM015", $"{fieldPrefix}.dependencies", "Supply a dependency array, including an empty array when none are required."));
                continue;
            }

            HashSet<string> dependencies = new(StringComparer.Ordinal);
            if (module.Dependencies.Any(dependency =>
                string.IsNullOrWhiteSpace(dependency) ||
                string.Equals(dependency, module.Id, StringComparison.Ordinal) ||
                !moduleIds.Contains(dependency) ||
                !dependencies.Add(dependency)))
            {
                diagnostics.Add(CreateDiagnostic("HXM008", $"{fieldPrefix}.dependencies", "Declare unique dependencies on other listed modules."));
            }
        }

        if (HasDependencyCycle([.. manifest.Modules.Where(module => module is not null)]))
        {
            diagnostics.Add(CreateDiagnostic("HXM008", "modules.dependencies", "Remove cyclic module dependencies."));
        }
    }

    private static void ValidateIdentifier(
        string? value,
        string field,
        List<ToolDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM015", field, "Supply a required deterministic identifier."));
        }
        else if (ManifestPathValidator.ContainsPlaceholder(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM006", field, "Resolve placeholders before invoking the runner."));
        }
        else if (ManifestSecretDetector.ContainsSecret(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM007", field, "Remove secret-bearing values from the manifest."));
        }
        else if (value.Length > _maximumIdentifierLength || !DeterministicIdentifierRegex().IsMatch(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM010", field, "Use lowercase kebab-case deterministic identifiers."));
        }
    }

    private static void ValidateManifest(
        ModuleManifest manifest,
        string fullManifestPath,
        List<ToolDiagnostic> diagnostics)
    {
        if (!string.Equals(manifest.Schema, _manifestSchema, StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic("HXM001", "schema", "Use hexalith.module-manifest.v1."));
        }

        ValidateIdentifier(manifest.Id, "id", diagnostics);
        ValidateModules(manifest, fullManifestPath, diagnostics);
        ValidatePlatform(manifest.Platform, diagnostics);
        ValidateProfiles(manifest.Profiles, fullManifestPath, diagnostics);

        if (manifest.Ui is not null)
        {
            _ = ManifestPathValidator.ValidateExistingFile(
                manifest.Ui.DescriptorAssembly,
                ManifestPathValidator.FindRepositoryRoot(fullManifestPath),
                "ui.descriptorAssembly",
                diagnostics);
        }
    }

    private static void ValidateModules(
        ModuleManifest manifest,
        string fullManifestPath,
        List<ToolDiagnostic> diagnostics)
    {
        if (manifest.Modules is null || manifest.Modules.Count == 0)
        {
            diagnostics.Add(CreateDiagnostic("HXM015", "modules", "Declare one or more module descriptors."));
            return;
        }

        string repositoryRoot = ManifestPathValidator.FindRepositoryRoot(fullManifestPath);
        HashSet<string> moduleIds = new(StringComparer.Ordinal);
        HashSet<string> domains = new(StringComparer.Ordinal);
        HashSet<string> applicationIds = new(StringComparer.Ordinal);
        HashSet<string> resourceIds = new(StringComparer.Ordinal);

        for (int moduleIndex = 0; moduleIndex < manifest.Modules.Count; moduleIndex++)
        {
            ModuleDescriptor? module = manifest.Modules[moduleIndex];
            if (module is null)
            {
                diagnostics.Add(CreateDiagnostic("HXM015", "modules", "Remove null module descriptors."));
                continue;
            }

            string fieldPrefix = $"modules[{moduleIndex}]";
            ValidateIdentifier(module.Id, $"{fieldPrefix}.id", diagnostics);
            ValidateIdentifier(module.Domain, $"{fieldPrefix}.domain", diagnostics);
            ValidateIdentifier(module.ApplicationId, $"{fieldPrefix}.applicationId", diagnostics);
            ValidateIdentifier(module.ResourceId, $"{fieldPrefix}.resourceId", diagnostics);
            _ = ManifestPathValidator.ValidateExistingFile(
                module.DescriptorAssembly,
                repositoryRoot,
                $"{fieldPrefix}.descriptorAssembly",
                diagnostics);

            AddDuplicateDiagnostic(module.Id, moduleIds, "modules.id", diagnostics);
            AddDuplicateDiagnostic(module.Domain, domains, "modules.domain", diagnostics);
            AddDuplicateDiagnostic(module.ApplicationId, applicationIds, "modules.applicationId", diagnostics);
            AddDuplicateDiagnostic(module.ResourceId, resourceIds, "modules.resourceId", diagnostics);
        }

        ValidateDependencies(manifest, diagnostics);
    }

    private static void ValidatePlatform(PlatformPins? platform, List<ToolDiagnostic> diagnostics)
    {
        if (platform is null)
        {
            diagnostics.Add(CreateDiagnostic("HXM015", "platform", "Supply all supported platform pins."));
            return;
        }

        ValidatePlatformPin(platform.EventStoreVersion, SupportedPlatformPins.EventStoreVersion, "platform.eventStoreVersion", diagnostics);
        ValidatePlatformPin(platform.DaprRuntimeVersion, SupportedPlatformPins.DaprRuntimeVersion, "platform.daprRuntimeVersion", diagnostics);
        ValidatePlatformPin(platform.DaprSdkVersion, SupportedPlatformPins.DaprSdkVersion, "platform.daprSdkVersion", diagnostics);
        ValidatePlatformPin(platform.FrontComposerVersion, SupportedPlatformPins.FrontComposerVersion, "platform.frontComposerVersion", diagnostics);
    }

    private static void ValidatePlatformPin(
        string? value,
        string expected,
        string field,
        List<ToolDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM015", field, "Supply the required platform pin."));
        }
        else if (ManifestPathValidator.ContainsPlaceholder(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM006", field, "Resolve placeholders before invoking the runner."));
        }
        else if (ManifestSecretDetector.ContainsSecret(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM007", field, "Remove secret-bearing values from the manifest."));
        }
        else if (!string.Equals(value, expected, StringComparison.Ordinal))
        {
            diagnostics.Add(CreateDiagnostic("HXM016", field, "Use the supported package and runtime pins for this runner release."));
        }
    }

    private static void ValidateProfiles(
        IReadOnlyDictionary<string, ModuleProfile>? profiles,
        string fullManifestPath,
        List<ToolDiagnostic> diagnostics)
    {
        if (profiles is null || profiles.Count == 0)
        {
            diagnostics.Add(CreateDiagnostic("HXM009", "profiles", "Declare one or more named qualification profiles."));
            return;
        }

        string repositoryRoot = ManifestPathValidator.FindRepositoryRoot(fullManifestPath);
        int profileIndex = 0;
        foreach (KeyValuePair<string, ModuleProfile> entry in profiles.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            string name = entry.Key;
            ModuleProfile? profile = entry.Value;
            string fieldPrefix = $"profiles[{profileIndex}]";
            profileIndex++;
            ValidateIdentifier(name, $"{fieldPrefix}.key", diagnostics);
            if (profile is null)
            {
                diagnostics.Add(CreateDiagnostic("HXM009", fieldPrefix, "Supply a complete profile object."));
                continue;
            }

            _ = ManifestPathValidator.ValidateExistingFile(profile.Fixture, repositoryRoot, $"{fieldPrefix}.fixture", diagnostics);

            if (profile.Classes is null || profile.Classes.Count == 0)
            {
                diagnostics.Add(CreateDiagnostic("HXM009", $"{fieldPrefix}.classes", "Declare one or more supported profile classes."));
                continue;
            }

            HashSet<string> classes = new(StringComparer.Ordinal);
            foreach (string profileClass in profile.Classes)
            {
                if (!classes.Add(profileClass))
                {
                    diagnostics.Add(CreateDiagnostic("HXM009", $"{fieldPrefix}.classes", "Declare each profile class once."));
                    break;
                }

                if (!ModuleProfileClasses.All.Contains(profileClass))
                {
                    diagnostics.Add(CreateDiagnostic("HXM014", $"{fieldPrefix}.classes", "Use a supported AD-25 profile class."));
                    break;
                }
            }
        }
    }

    private static void AddDuplicateDiagnostic(
        string? value,
        HashSet<string> values,
        string field,
        List<ToolDiagnostic> diagnostics)
    {
        if (!string.IsNullOrWhiteSpace(value) && !values.Add(value))
        {
            diagnostics.Add(CreateDiagnostic("HXM003", field, "Use unique deterministic identifiers."));
        }
    }

    private static bool HasDependencyCycle(IReadOnlyList<ModuleDescriptor> modules)
    {
        Dictionary<string, ModuleDescriptor> modulesById = modules
            .Where(module => !string.IsNullOrWhiteSpace(module.Id))
            .GroupBy(module => module.Id, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);
        Dictionary<string, int> dependencyCounts = modulesById.Keys
            .ToDictionary(moduleId => moduleId, _ => 0, StringComparer.Ordinal);
        Dictionary<string, List<string>> dependents = modulesById.Keys
            .ToDictionary(moduleId => moduleId, _ => new List<string>(), StringComparer.Ordinal);

        foreach ((string moduleId, ModuleDescriptor module) in modulesById)
        {
            foreach (string dependency in (module.Dependencies ?? [])
                .Where(dependency => !string.IsNullOrWhiteSpace(dependency) && modulesById.ContainsKey(dependency))
                .Distinct(StringComparer.Ordinal))
            {
                dependencyCounts[moduleId]++;
                dependents[dependency].Add(moduleId);
            }
        }

        Queue<string> ready = new(dependencyCounts
            .Where(entry => entry.Value == 0)
            .Select(entry => entry.Key));
        int visitedCount = 0;
        while (ready.TryDequeue(out string? moduleId))
        {
            visitedCount++;
            foreach (string dependent in dependents[moduleId])
            {
                dependencyCounts[dependent]--;
                if (dependencyCounts[dependent] == 0)
                {
                    ready.Enqueue(dependent);
                }
            }
        }

        return visitedCount != modulesById.Count;
    }

    [GeneratedRegex("^[a-z][a-z0-9]*(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeterministicIdentifierRegex();
}