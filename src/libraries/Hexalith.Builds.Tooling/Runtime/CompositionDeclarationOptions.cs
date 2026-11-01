// <copyright file="CompositionDeclarationOptions.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Frozen;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Workspace;

/// <summary>The authoritative inputs shared by lifecycle coordination and composition.</summary>
/// <param name="Mapping">The command-selected workspace mapping.</param>
/// <param name="DeclarationPaths">The supplied owner declarations.</param>
public sealed record CompositionDeclarationOptions(SourceMapping Mapping, IReadOnlyList<string> DeclarationPaths)
{
    /// <summary>Gets the unchanged owner release commits shared by lifecycle startup and the Platform AppHost.</summary>
    public static IReadOnlyDictionary<string, string> DefaultReleaseCommits { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eventstore/3.120.0"] = "9b26956f9e825fd6771b62a79c7b81323ba856cf",
        ["tenants/5.8.0"] = "032573384d3df4bc7a5bc0e69945ecca4e00967f",
        ["memories/2.27.1"] = "d54dba5be30a3626e871b4fc56dd78e76a7acf71",
        ["eventstore/3.129.0"] = "c02afa0fce310ccdb696232f18829df96cba088e",
        ["memories/3.0.12"] = "ef5c134bbee7ec708f354ce1bc3e508ee6105ab7",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the shared released declaration hashes; absent entries have no released v2 declaration.</summary>
    public static IReadOnlyDictionary<string, string> DefaultReleaseSnapshotHashes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eventstore/3.129.0"] = "a56951dcc4a8c53f22e449a2399516e51c4d2569bec1357f8197593a8d9e44a2",
        ["memories/3.0.12"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the selected Platform checkout.</summary>
    public string PlatformRoot { get; init; } = Mapping.PlatformIdentity?.PlatformPath
        ?? Mapping.Entries.SingleOrDefault(entry => entry.Identity == "Hexalith.Platform")?.Path
        ?? Mapping.Root;

    /// <summary>Gets the uniquely selected active module, or null for the reference composition.</summary>
    public string? ActiveModule { get; init; }

    /// <summary>Gets the explicitly selected module class, when present.</summary>
    public bool? DomainModule { get; init; }

    /// <summary>Gets a value indicating whether Timesheets is explicitly selected.</summary>
    public bool TimesheetsEnabled { get; init; }

    /// <summary>Gets the run workspace containing the pinned catalog snapshot, when materialized.</summary>
    public string? Workspace { get; init; }

    /// <summary>Gets the owner release tag commits selected by the Platform release.</summary>
    public IReadOnlyDictionary<string, string> ReleaseCommits { get; init; } = DefaultReleaseCommits;

    /// <summary>Gets the released owner declaration hashes; absent entries have no released v2 declaration.</summary>
    public IReadOnlyDictionary<string, string> ReleaseSnapshotHashes { get; init; } = DefaultReleaseSnapshotHashes;

    /// <summary>Resolves the sole catalog version authority while preserving snapshot identity checks.</summary>
    /// <param name="module">The owner identity.</param>
    /// <param name="source">The declaration source for diagnostics.</param>
    /// <returns>The catalog-selected owner version.</returns>
    /// <exception cref="InvalidDataException">The catalog or selected snapshot is unavailable or inconsistent.</exception>
    public string CatalogVersion(string module, string source)
    {
        try
        {
            return ReadCatalogVersion(module, source);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or XmlException)
        {
            throw new InvalidDataException($"{source}: {module}: Builds catalog metadata is malformed: {exception.Message}", exception);
        }
    }

    private string ReadCatalogVersion(string module, string source)
    {
        string package = module switch { "tenants" => "Hexalith.Tenants.Server", "memories" => "Hexalith.Memories.Aspire", _ => CompositionDeclarationClosure.Repository(module) + ".Aspire" };
        string? snapshotPath = Workspace is null ? null : Path.Combine(Workspace, "source-mapping", "catalog-versions.json");
        if (snapshotPath is not null && File.Exists(snapshotPath))
        {
            CatalogVersionSnapshot snapshot = JsonSerializer.Deserialize<CatalogVersionSnapshot>(File.ReadAllText(snapshotPath))
                ?? throw new InvalidDataException($"{source}: {module}: {snapshotPath}: catalog snapshot is null.");
            ValidateSnapshot(snapshot, source, module, snapshotPath);
            return snapshot.Versions.TryGetValue(package, out string? selected)
                ? selected
                : throw new InvalidDataException($"{source}: {module}: {package}: image version is missing from the Builds catalog.");
        }

        string catalogPath = Path.Combine(PlatformRoot, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        if (!File.Exists(catalogPath))
        {
            throw new InvalidDataException($"{source}: {module}: {catalogPath}: Builds catalog is unavailable.");
        }

        XDocument catalog = XDocument.Load(catalogPath);
        string suffix = module switch
        {
            "tenants" => "Tenants",
            "memories" => "Memories",
            _ => CompositionDeclarationClosure.Repository(module)["Hexalith.".Length..],
        };
        string property = "Hexalith" + suffix + "Version";
        return catalog.Descendants(property).FirstOrDefault()?.Value ?? throw new InvalidDataException($"{source}: {module}: {property}: image version is missing from the Builds catalog.");
    }

    private void ValidateSnapshot(CatalogVersionSnapshot snapshot, string source, string module, string snapshotPath)
    {
        if (snapshot.PinnedFiles is null || snapshot.Versions is null || snapshot.ProjectVersions?.Any(pair => pair.Value is null) != false
            || snapshot.MappingHash != Mapping.ContentHash || snapshot.Hash != CatalogVersionSnapshot.ComputeHash(snapshot.MappingHash, snapshot.CatalogCommit, snapshot.CatalogFileHash, snapshot.PinnedFiles, snapshot.Versions, snapshot.ProjectVersions))
        {
            throw new InvalidDataException($"{source}: {module}: {snapshotPath}: catalog snapshot differs from the selected mapping.");
        }
    }
}