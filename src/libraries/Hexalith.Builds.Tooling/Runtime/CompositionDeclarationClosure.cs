// <copyright file="CompositionDeclarationClosure.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Validates authoritative source or release declarations and selects the composed dependency closure.</summary>
public static class CompositionDeclarationClosure
{
    /// <summary>Loads the same owner declarations for startup coordination and the Platform model.</summary>
    /// <param name="options">The selected declaration inputs and release provenance.</param>
    /// <returns>The validated dependency closure.</returns>
    /// <exception cref="InvalidDataException">An owner declaration or its release provenance is unavailable or inconsistent.</exception>
    public static IReadOnlyList<PlatformModuleDeclaration> Load(CompositionDeclarationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            return LoadDeclarations(options);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or XmlException)
        {
            throw new InvalidDataException($"{options.PlatformRoot}: declaration or release provenance is malformed: {exception.Message}", exception);
        }
    }

    /// <summary>Returns the declared module identity.</summary>
    /// <param name="declaration">The validated module.</param>
    /// <returns>The module identity.</returns>
    public static string Id(JsonElement declaration) => declaration.GetProperty("identity").GetProperty("moduleId").GetString()!;

    /// <summary>Resolves task paths against their mapped owner repository or released declaration artifacts.</summary>
    /// <param name="mapping">The command-selected source/package mapping.</param>
    /// <param name="declaration">The authoritative declaring module.</param>
    /// <returns>The declaring repository or release-artifact root.</returns>
    public static string TaskRepositoryRoot(SourceMapping mapping, PlatformModuleDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(declaration);
        SourceMappingEntry? owner = mapping.Entries.SingleOrDefault(entry => entry.Identity.Equals(Repository(Id(declaration.Declaration)), StringComparison.OrdinalIgnoreCase));
        return owner?.Path ?? (mapping.Mode == WorkspaceMode.Source
            ? ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(declaration.Source))
            : Path.GetDirectoryName(Path.GetFullPath(declaration.Source))!);
    }

    /// <summary>Returns the source repository identity for an owner.</summary>
    /// <param name="module">The module identity.</param>
    /// <returns>The repository identity.</returns>
    public static string Repository(string module) => module switch
    {
        "eventstore" => "Hexalith.EventStore",
        "tenants" => "Hexalith.Tenants",
        "memories" => "Hexalith.Memories",
        "mcpcli" => "Hexalith.McpCli",
        _ => "Hexalith." + char.ToUpperInvariant(module[0]) + module[1..],
    };

    private static IReadOnlyList<PlatformModuleDeclaration> LoadDeclarations(CompositionDeclarationOptions options)
    {
        if (options.DeclarationPaths.Count > 0)
        {
            PlatformManifestValidationResult supplied = PlatformManifestValidator.Validate(options.DeclarationPaths);
            if (!supplied.IsValid)
            {
                throw new InvalidDataException(string.Join(Environment.NewLine, supplied.Diagnostics.Select(d => $"{d.Source}: {d.Field}: {d.Message}")));
            }
        }

        List<string> paths = [];
        Dictionary<string, (string Path, JsonElement Declaration)> snapshots = new(StringComparer.Ordinal);
        foreach (string owner in new[] { "eventstore", "tenants", "memories" })
        {
            string repository = Repository(owner);
            if (options.Mapping.Mode == WorkspaceMode.Source)
            {
                SourceMappingEntry? entry = options.Mapping.Entries.SingleOrDefault(e => e.Identity.Equals(repository, StringComparison.OrdinalIgnoreCase));
                if (entry is null || entry.Origin != "source" || entry.Path is null)
                {
                    throw new InvalidDataException($"{repository}: identity.dependencies: mapped source declaration is unavailable.");
                }

                paths.Add(Path.Combine(entry.Path, "module", "hexalith.module-manifest.v2.json"));
            }
            else
            {
                string directory = Path.Combine(options.PlatformRoot, "src", "Hexalith.Platform.Composition", "Declarations");
                string snapshot = Path.Combine(directory, owner + ".json");
                string provenance = Path.Combine(directory, "provenance.json");
                if (!File.Exists(snapshot) || !File.Exists(provenance))
                {
                    throw new InvalidDataException($"{repository}: release snapshot '{snapshot}' or provenance '{provenance}' is unavailable.");
                }

                using JsonDocument sources = JsonDocument.Parse(File.ReadAllText(provenance));
                JsonElement source = sources.RootElement.GetProperty(owner);
                foreach (string field in new[] { "repository", "commit", "source", "sha256", "version" })
                {
                    if (!source.TryGetProperty(field, out JsonElement value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                    {
                        throw new InvalidDataException($"{repository}: release snapshot provenance '{provenance}': {field}: source provenance is unavailable.");
                    }
                }

                string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(snapshot)));
                if (source.GetProperty("repository").GetString() != repository || source.GetProperty("sha256").GetString() != hash)
                {
                    throw new InvalidDataException($"{repository}: release snapshot '{snapshot}' differs from its source provenance.");
                }

                string catalogVersion = options.CatalogVersion(owner, snapshot);
                string provenanceVersion = source.GetProperty("version").GetString()!;
                string provenanceCommit = source.GetProperty("commit").GetString()!;
                if (!options.ReleaseCommits.TryGetValue(owner + "/" + catalogVersion, out string? releaseCommit)
                    || !options.ReleaseSnapshotHashes.TryGetValue(owner + "/" + catalogVersion, out string? releasedHash)
                    || !string.Equals(provenanceVersion, catalogVersion, StringComparison.Ordinal)
                    || !string.Equals(provenanceCommit, releaseCommit, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(hash, releasedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"{repository}: release snapshot '{snapshot}': provenance version '{provenanceVersion}' at commit '{provenanceCommit}' does not match Builds catalog version '{catalogVersion}', release commit '{releaseCommit ?? "unavailable"}', or its released v2 declaration (absent until the owner releases one).");
                }

                using JsonDocument released = JsonDocument.Parse(File.ReadAllText(snapshot));
                snapshots[owner] = (snapshot, released.RootElement.GetProperty("modules").EnumerateArray().Single().Clone());
                paths.Add(snapshot);
            }
        }

        // A supplied reference-owner file is already part of the source set or release snapshot.
        foreach (string path in options.DeclarationPaths)
        {
            if (paths.Contains(Path.GetFullPath(path), StringComparer.Ordinal))
            {
                continue;
            }

            if (options.Mapping.Mode == WorkspaceMode.Package)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                JsonElement[] supplied = [.. document.RootElement.GetProperty("modules").EnumerateArray()];
                for (int index = 0; index < supplied.Length; index++)
                {
                    JsonElement module = supplied[index];
                    if (snapshots.TryGetValue(Id(module), out (string Path, JsonElement Declaration) snapshot) && !JsonElement.DeepEquals(module, snapshot.Declaration))
                    {
                        throw new InvalidDataException($"{Path.GetFullPath(path)}: {Id(module)}: modules[{index}]: supplied owner declaration conflicts with authoritative release snapshot '{snapshot.Path}'.");
                    }
                }

                if (supplied.All(module => snapshots.ContainsKey(Id(module))))
                {
                    continue;
                }

                // A mixed document can replace an equal snapshot, but never change its release content.
                foreach (JsonElement module in supplied)
                {
                    if (snapshots.TryGetValue(Id(module), out (string Path, JsonElement Declaration) snapshot))
                    {
                        _ = paths.Remove(snapshot.Path);
                    }
                }
            }

            paths.Add(Path.GetFullPath(path));
        }

        if (options.TimesheetsEnabled)
        {
            string candidate = Path.GetFullPath(Path.Combine(options.PlatformRoot, "eng", "timesheets-deployment", "hexalith.module-manifest.v2.json"));
            if (!paths.Contains(candidate, StringComparer.Ordinal))
            {
                paths.Add(candidate);
            }
        }

        PlatformManifestValidationResult result = PlatformManifestValidator.Validate(paths);
        if (!result.IsValid)
        {
            throw new InvalidDataException(string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Source}: {d.Field}: {d.Message}")));
        }

        Dictionary<string, PlatformModuleDeclaration> byId = result.Declarations!.ToDictionary(d => Id(d.Declaration), StringComparer.Ordinal);
        HashSet<string> selected = new(StringComparer.Ordinal);
        HashSet<string> visiting = new(StringComparer.Ordinal);
        void Select(string identity)
        {
            if (!byId.TryGetValue(identity, out PlatformModuleDeclaration? declaration))
            {
                throw new InvalidDataException($"{identity}: identity.dependencies: owner declaration is unavailable.");
            }

            if (visiting.Contains(identity))
            {
                throw new InvalidDataException($"{declaration.Source}: {identity}: identity.dependencies: dependency cycle is not supported.");
            }

            if (selected.Contains(identity))
            {
                return;
            }

            _ = visiting.Add(identity);
            foreach (JsonElement dependency in declaration.Declaration.GetProperty("identity").GetProperty("dependencies").EnumerateArray())
            {
                Select(dependency.GetString()!);
            }

            _ = visiting.Remove(identity);
            _ = selected.Add(identity);
        }

        if (options.ActiveModule is not null)
        {
            Select(options.ActiveModule);
        }

        if (options.TimesheetsEnabled)
        {
            Select("timesheets");
        }

        bool domain = options.DomainModule ?? (options.ActiveModule is not null && options.ActiveModule is not ("eventstore" or "memories" or "mcpcli" or "platform" or "builds"));
        if (options.ActiveModule is null || domain)
        {
            foreach (string owner in new[] { "eventstore", "tenants", "memories" })
            {
                Select(owner);
            }
        }

        return [.. result.Declarations!.Where(d => selected.Contains(Id(d.Declaration)))];
    }
}