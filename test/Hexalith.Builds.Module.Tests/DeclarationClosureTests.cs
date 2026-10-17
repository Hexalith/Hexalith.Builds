// <copyright file="DeclarationClosureTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Proves source dependency selection and the shared release provenance refusals.</summary>
public sealed class DeclarationClosureTests
{
    /// <summary>Verifies only the authoritative dependency closure contributes lifecycle metadata.</summary>
    [Fact]
    public void SourceClosureSelectsOnlyDeclaredTechnicalDependencies()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.identity.moduleId", "\"mcpcli\"");
        workspace.Change("modules.0.identity.dependencies", "[\"memories\"]");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        IReadOnlyList<PlatformModuleDeclaration> declarations = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [workspace.Save()]) { ActiveModule = "mcpcli" });
        declarations.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["mcpcli", "memories"]);
        Path.GetFullPath(declarations.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "memories").Source).ShouldBe(Path.Combine(workspace.Root, "references", "Hexalith.Memories", "module", "hexalith.module-manifest.v2.json"));
    }

    /// <summary>Verifies the shared package closure retains every source/catalog/release provenance check.</summary>
    /// <param name="fault">The provenance input to alter, or none for valid release metadata.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fixture fault is unknown.</exception>
    [Theory]
    [InlineData("none")]
    [InlineData("repository")]
    [InlineData("hash")]
    [InlineData("commit")]
    [InlineData("version")]
    [InlineData("released-hash")]
    [InlineData("supplied-conflict")]
    public void PackageClosurePreservesReleaseProvenance(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = PackageOptions(workspace.Root);
        string provenancePath = Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations", "provenance.json");
        JsonNode provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
        switch (fault)
        {
            case "none":
                break;
            case "repository":
                provenance["memories"]!["repository"] = "Hexalith.Foreign";
                break;
            case "hash":
                provenance["memories"]!["sha256"] = new string('0', 64);
                break;
            case "commit":
                provenance["memories"]!["commit"] = new string('b', 40);
                break;
            case "version":
                provenance["memories"]!["version"] = "2.0.0";
                break;
            case "released-hash":
                options = options with { ReleaseSnapshotHashes = new Dictionary<string, string>(options.ReleaseSnapshotHashes, StringComparer.Ordinal) { ["memories/1.0.0"] = new string('0', 64) } };
                break;
            case "supplied-conflict":
                string snapshotPath = Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations", "memories.json");
                JsonNode supplied = JsonNode.Parse(File.ReadAllText(snapshotPath))!;
                supplied["modules"]![0]!["lifecycle"]!["startup"] = JsonNode.Parse("{\"defaultTimeoutSeconds\":600,\"override\":{\"timeoutSeconds\":900,\"justification\":\"conflicting override\"}}");
                string suppliedPath = Path.Combine(workspace.Root, "supplied.json");
                File.WriteAllText(suppliedPath, supplied.ToJsonString());
                options = options with { DeclarationPaths = [suppliedPath] };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        File.WriteAllText(provenancePath, provenance.ToJsonString());
        if (fault == "none")
        {
            IReadOnlyList<PlatformModuleDeclaration> declarations = CompositionDeclarationClosure.Load(options);
            declarations.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).ShouldBe(["memories"]);
            CompositionDeclarationClosure.TaskRepositoryRoot(options.Mapping with { Entries = [] }, declarations.Single()).ShouldBe(Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations"));
        }
        else
        {
            Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options)).Message.ShouldContain("memories");
        }
    }

    private static CompositionDeclarationOptions PackageOptions(string root)
    {
        SourceMapping source = CompositionTestFiles.CreatePlatformMapping(root);
        string directory = Path.Combine(root, "src", "Hexalith.Platform.Composition", "Declarations");
        _ = Directory.CreateDirectory(directory);
        JsonObject provenance = [];
        Dictionary<string, string> commits = new(StringComparer.Ordinal);
        Dictionary<string, string> hashes = new(StringComparer.Ordinal);
        foreach (string owner in new[] { "eventstore", "tenants", "memories" })
        {
            string repository = CompositionDeclarationClosure.Repository(owner);
            string declaration = Path.Combine(source.Entries.Single(entry => entry.Identity == repository).Path!, "module", "hexalith.module-manifest.v2.json");
            byte[] bytes = File.ReadAllBytes(declaration);
            File.WriteAllBytes(Path.Combine(directory, owner + ".json"), bytes);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            commits[owner + "/1.0.0"] = new string('a', 40);
            hashes[owner + "/1.0.0"] = hash;
            provenance[owner] = new JsonObject { ["repository"] = repository, ["commit"] = new string('a', 40), ["version"] = "1.0.0", ["source"] = "module/hexalith.module-manifest.v2.json", ["sha256"] = hash };
        }

        File.WriteAllText(Path.Combine(directory, "provenance.json"), provenance.ToJsonString());
        string catalog = Path.Combine(root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
        File.WriteAllText(catalog, "<Project><PropertyGroup><HexalithEventStoreVersion>1.0.0</HexalithEventStoreVersion><HexalithTenantsVersion>1.0.0</HexalithTenantsVersion><HexalithMemoriesVersion>1.0.0</HexalithMemoriesVersion></PropertyGroup></Project>");
        return new CompositionDeclarationOptions(source with { Mode = WorkspaceMode.Package }, [])
        {
            PlatformRoot = root,
            ActiveModule = "memories",
            ReleaseCommits = commits,
            ReleaseSnapshotHashes = hashes,
        };
    }
}
