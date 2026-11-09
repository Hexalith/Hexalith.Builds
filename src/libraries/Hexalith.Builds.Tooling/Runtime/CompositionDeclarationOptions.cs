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
        ["eventstore/4.0.4"] = "326bfef884543cdbcaf96de7f4c4ad378dbad6d7",
        ["eventstore/4.1.0"] = "dbc6e7e08d605ba6c6c790e3e08a15dc8bbdec8a",
        ["tenants/6.0.0"] = "9e5a991d0ba304bac7b1a5809cd71797b47d78ed",
        ["tenants/7.0.0"] = "1b2c71b0e3cc5b16745f87aa7f1bc2c0ac8ffc27",
        ["tenants/7.0.2"] = "143cec60a3f5bb1831fa57f35c510880ceb788d8",
        ["tenants/7.0.4"] = "8e20524574901d71ad5f4876804c64c3c6c87b88",
        ["tenants/7.0.5"] = "d88aef6b52d7e8a2e2d81132b1d76b7e1d1f0f35",
        ["memories/3.0.18"] = "66822bc5bc4858c824f17b7d093f5f0f676b03ea",
        ["memories/3.0.23"] = "82c0aa0a7046d7bd91569ac1c9a38a3ed0386d0f",
        ["memories/3.0.24"] = "859680b38a77f04cc8f81ab7a5fe7f40a617cc2a",
        ["memories/3.0.25"] = "246157a7ff8e2dcd8d356c833a77a02514490d2f",
        ["parties/3.1.0"] = "50904cc4ba53c9b0b2c082d5872a0d276be8f5c3",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the shared released declaration hashes; absent entries have no released v2 declaration.</summary>
    public static IReadOnlyDictionary<string, string> DefaultReleaseSnapshotHashes { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["eventstore/3.129.0"] = "a56951dcc4a8c53f22e449a2399516e51c4d2569bec1357f8197593a8d9e44a2",
        ["memories/3.0.12"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
        ["eventstore/4.0.4"] = "21eda6da824d5ae6135ff342dd8a5aae950b02420319b953a836dddeb00ce495",
        ["eventstore/4.1.0"] = "21eda6da824d5ae6135ff342dd8a5aae950b02420319b953a836dddeb00ce495",
        ["tenants/6.0.0"] = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522",
        ["tenants/7.0.0"] = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522",
        ["tenants/7.0.2"] = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522",
        ["tenants/7.0.4"] = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522",
        ["tenants/7.0.5"] = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522",
        ["memories/3.0.18"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
        ["memories/3.0.23"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
        ["memories/3.0.24"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
        ["memories/3.0.25"] = "56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840",
        ["parties/3.1.0"] = "d9c193b0e7b61601b8fcceb906b5dafe4769a1b9d7538a6ce164b4aa0840b348",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>Gets the selected Platform checkout.</summary>
    public string PlatformRoot { get; init; } = Mapping.PlatformIdentity?.PlatformPath
        ?? Mapping.Entries.SingleOrDefault(entry => entry.Identity == "Hexalith.Platform")?.Path
        ?? Mapping.Root;

    /// <summary>Gets the uniquely selected active module, or null for the reference composition.</summary>
    public string? ActiveModule { get; init; }

    /// <summary>Gets the explicit additive reference owners requested for a fresh qualification run.</summary>
    public IReadOnlyList<string> QualificationOwners { get; init; } = [];

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

    /// <summary>Resolves one package's pinned version from the same catalog authority.</summary>
    /// <param name="packageId">The package identity.</param>
    /// <param name="source">The declaration source for diagnostics.</param>
    /// <returns>The catalog-pinned version, or null when the catalog does not pin the package.</returns>
    /// <exception cref="InvalidDataException">The catalog or selected snapshot is unavailable or inconsistent.</exception>
    public string? CatalogPackageVersion(string packageId, string source)
        => CatalogPackageVersion(packageId, source, null);

    /// <summary>Resolves a package from an already recorded consumer-specific pinned snapshot view.</summary>
    /// <param name="packageId">The package identity.</param>
    /// <param name="source">The declaration source for diagnostics.</param>
    /// <param name="projectName">The actual consumer project name, or null for the default view.</param>
    /// <returns>The selected package version, or null when the selected view does not pin it.</returns>
    /// <exception cref="InvalidDataException">The snapshot is invalid or the requested consumer view is absent.</exception>
    public string? CatalogPackageVersion(string packageId, string source, string? projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        try
        {
            return ReadCatalogPackageVersion(packageId, source, projectName);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or XmlException)
        {
            throw new InvalidDataException($"{source}: {packageId}: Builds catalog metadata is malformed: {exception.Message}", exception);
        }
    }

    private string? ReadCatalogPackageVersion(string packageId, string source, string? projectName)
    {
        string? snapshotPath = Workspace is null ? null : Path.Combine(Workspace, "source-mapping", "catalog-versions.json");
        if (snapshotPath is not null && File.Exists(snapshotPath))
        {
            CatalogVersionSnapshot snapshot = JsonSerializer.Deserialize<CatalogVersionSnapshot>(File.ReadAllText(snapshotPath))
                ?? throw new InvalidDataException($"{source}: {packageId}: {snapshotPath}: catalog snapshot is null.");
            ValidateSnapshot(snapshot, source, packageId, snapshotPath);
            IReadOnlyDictionary<string, string> selected = snapshot.Versions;
            if (!string.IsNullOrWhiteSpace(projectName))
            {
                selected = snapshot.ProjectVersions.FirstOrDefault(pair => string.Equals(pair.Key, projectName, StringComparison.OrdinalIgnoreCase)).Value
                    ?? throw new InvalidDataException($"{source}: {packageId}: {snapshotPath}: catalog snapshot has no recorded view for consumer '{projectName}'.");
            }

            return selected.FirstOrDefault(pair => string.Equals(pair.Key, packageId, StringComparison.OrdinalIgnoreCase)).Value;
        }

        if (!string.IsNullOrWhiteSpace(projectName))
        {
            throw new InvalidDataException($"{source}: {packageId}: consumer '{projectName}' requires a materialized pinned catalog snapshot.");
        }

        XDocument catalog = XDocument.Load(CatalogPath(packageId, source));
        string? version = catalog.Descendants("PackageVersion")
            .FirstOrDefault(element => string.Equals(element.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase))
            ?.Attribute("Version")?.Value.Trim();
        if (version?.StartsWith("$(", StringComparison.Ordinal) != true || !version.EndsWith(')'))
        {
            return version;
        }

        string property = version[2..^1];
        return catalog.Descendants(property).FirstOrDefault()?.Value.Trim();
    }

    private string CatalogPath(string module, string source)
    {
        // A module's resolved identity already verifies its direct Builds checkout against Platform's pin.
        // Platform's own nested references remain uninitialized in a direct-only module workspace.
        string catalogRoot = Mapping.PlatformIdentity is { IsPlatformRepository: false } ? Mapping.Root : PlatformRoot;
        string catalogPath = Path.Combine(catalogRoot, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        return File.Exists(catalogPath)
            ? catalogPath
            : throw new InvalidDataException($"{source}: {module}: {catalogPath}: Builds catalog is unavailable.");
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

        XDocument catalog = XDocument.Load(CatalogPath(module, source));
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