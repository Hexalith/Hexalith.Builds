// <copyright file="PersistedProfileLoader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;
using System.Text.Json.Serialization;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Loads the data-only persisted profile with strict containment and schema checks.
/// </summary>
public static class PersistedProfileLoader
{
    private static readonly JsonSerializerOptions _strictOptions = new(CompositionDocumentStore.SerializerOptions)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Loads a supported full profile, returning null for unsupported or malformed fixtures.
    /// </summary>
    /// <param name="manifest">The validated manifest.</param>
    /// <param name="manifestPath">The manifest path.</param>
    /// <param name="profile">The selected profile.</param>
    /// <param name="filter">The optional test filter.</param>
    /// <returns>The supported definition, or null.</returns>
    public static PersistedProfileDefinition? TryLoad(ModuleManifest manifest, string manifestPath, string? profile, string? filter)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (filter is not null || profile is null || !manifest.Profiles.TryGetValue(profile, out ModuleProfile? declared)
            || !declared.Classes.Contains("persisted-boundary", StringComparer.Ordinal)
            || !declared.Classes.Contains("restart", StringComparer.Ordinal)
            || !declared.Classes.Contains("two-instance", StringComparer.Ordinal))
        {
            return null;
        }

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        List<Diagnostics.ToolDiagnostic> diagnostics = [];
        string? path = ManifestPathValidator.ValidateExistingFile(declared.Fixture, root, "profile.fixture", diagnostics);
        if (path is null || new FileInfo(path).Length > 1024 * 1024)
        {
            return null;
        }

        try
        {
            PersistedProfileDefinition? definition = JsonSerializer.Deserialize<PersistedProfileDefinition>(File.ReadAllText(path), _strictOptions);
            bool valid = definition is not null && string.Equals(definition.Schema, PersistedProfileDefinition.SupportedSchema, StringComparison.Ordinal)
                && !string.IsNullOrWhiteSpace(definition.Id) && definition.Modules.Count == 2
                && manifest.Modules.Count == 2 && !definition.Modules.Any(module =>
                    string.IsNullOrWhiteSpace(module.ModuleId)
                    || string.IsNullOrWhiteSpace(module.CommandType)
                    || string.IsNullOrWhiteSpace(module.EventType)
                    || string.IsNullOrWhiteSpace(module.ProjectionType)
                    || module.InitialQuantity <= 0 || module.RetryQuantity <= 0)
                && definition.Modules.Select(module => module.ModuleId).Distinct(StringComparer.Ordinal).Count() == 2
                && definition.Modules.Select(module => module.ModuleId).Order(StringComparer.Ordinal)
                    .SequenceEqual(manifest.Modules.Select(module => module.Id).Order(StringComparer.Ordinal), StringComparer.Ordinal)
                && (definition.NativeTests is null || IsValidNativeTests(definition.NativeTests, root));

            return valid ? definition : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool IsValidNativeTests(PersistedProfileNativeTests tests, string root)
    {
        List<Diagnostics.ToolDiagnostic> diagnostics = [];
        return tests.Platform is PersistedProfileNativeTests.VsTest or PersistedProfileNativeTests.MicrosoftTestingPlatform
            && tests.Project?.EndsWith(".csproj", StringComparison.Ordinal) == true
            && ManifestPathValidator.ValidateExistingFile(tests.Project, root, "profile.nativeTests.project", diagnostics) is not null;
    }
}
