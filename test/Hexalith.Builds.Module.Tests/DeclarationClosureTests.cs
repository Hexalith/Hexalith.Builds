// <copyright file="DeclarationClosureTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Aspire.Hosting;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Proves source dependency selection and the shared release provenance refusals.</summary>
public sealed class DeclarationClosureTests
{
    /// <summary>Selects each owner once and finds Conversations through Projects using the canonical Platform declaration.</summary>
    [Fact]
    public void VerifiedSelectorSelectsEveryOwnerOnce()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = CompleteEnvironmentFixture.CreateSourceOptions(workspace.Root);
        IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(options);

        closure.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal)
            .ShouldBe(CompleteEnvironmentFixture.SelectedOwners);
        Path.GetFullPath(closure.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "platform").Source).ShouldBe(options.DeclarationPaths.Single());
        closure.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "mcpcli")
            .Declaration.GetProperty("identity").GetProperty("servers").GetArrayLength().ShouldBe(0);
    }

    /// <summary>Refuses a changed transitive declaration at the real AppHost plan-validation boundary.</summary>
    [Fact]
    public void CompleteAppHostRequiresTheCoordinatedDeclarationClosure()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = CompleteEnvironmentFixture.CreateSourceOptions(workspace.Root);
        string selector = options.DeclarationPaths.Single();
        IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(options);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with
        {
            PlatformDeclarationPaths = [selector],
            PlatformActiveModule = "platform",
            PlatformModelAssemblyPath = Path.Combine(workspace.Root, "missing-model.dll"),
            PlatformDeclarationHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(selector))),
            SourceMapping = options.Mapping,
            CompositionContentIdentity = TestEnvironmentDescriptorWriter.CompositionIdentity(closure, null),
        };

        IReadOnlyList<PlatformModuleDeclaration> verified = RunTopology.VerifyPlatformDeclaration(plan).ShouldNotBeNull();
        string transitiveOwner = closure.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "conversations").Source;
        File.AppendAllText(transitiveOwner, " ");
        CompositionDeclarationOptions modelOptions = options with { DeclarationPaths = plan.PlatformDeclarationPaths, Workspace = plan.Workspace };
        CompositionDeclarationClosure.ComposeVerifiedModel(plan, verified, () =>
            CompositionDeclarationClosure.Load(modelOptions).ShouldBeSameAs(verified));
        CompositionDeclarationClosure.Load(modelOptions).ShouldNotBeSameAs(verified);

        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
        Should.Throw<InvalidDataException>(() => RunTopology.Compose(builder, plan, "fixture-signing-key")).Message.ShouldContain("closure changed");
    }

    /// <summary>Refuses a missing owner, substituted owner identity, dependency cycle or selector outside the verified root.</summary>
    /// <param name="fault">The authority or dependency fault to inject.</param>
    /// <param name="diagnostic">The expected owner or root diagnostic.</param>
    [Theory]
    [InlineData("missing", "Hexalith.Conversations")]
    [InlineData("projects-dependency", "Hexalith.Projects")]
    [InlineData("wrong-owner", "Hexalith.Projects")]
    [InlineData("cycle", "dependency cycle")]
    [InlineData("wrong-path", "verified Platform root")]
    [InlineData("foreign-root", "verified Platform root")]
    [InlineData("unverified", "verified Platform root")]
    public void CompleteSelectorRefusesInvalidOwnerGraph(string fault, string diagnostic)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = CompleteEnvironmentFixture.CreateSourceOptions(workspace.Root);
        string projects = Path.Combine(workspace.Root, "references", "Hexalith.Projects");
        string conversations = Path.Combine(workspace.Root, "references", "Hexalith.Conversations");

        switch (fault)
        {
            case "missing":
                Directory.Delete(conversations, recursive: true);
                break;
            case "projects-dependency":
                _ = CompleteEnvironmentFixture.WriteManifest(projects, "projects", ["folders"]);
                break;
            case "wrong-owner":
                _ = CompleteEnvironmentFixture.WriteManifest(projects, "mcpcli", ["conversations"]);
                break;
            case "cycle":
                _ = CompleteEnvironmentFixture.WriteManifest(conversations, "conversations", ["projects"]);
                break;
            case "wrong-path":
                string foreign = Path.Combine(workspace.Root, "fake.json");
                File.Copy(options.DeclarationPaths.Single(), foreign);
                options = options with { DeclarationPaths = [foreign] };
                break;
            case "foreign-root":
                options = options with { Mapping = options.Mapping with { PlatformIdentity = options.Mapping.PlatformIdentity! with { IsPlatformRepository = false } } };
                break;
            default:
                options = options with { Mapping = options.Mapping with { IdentityFindings = [new PlatformIdentityFinding("HXW008", "Fixture identity drift.", workspace.Root)] } };
                break;
        }

        Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options)).Message.ShouldContain(diagnostic);
    }

    /// <summary>Refuses a matching owner identity whose Projects release metadata differs from the pinned catalog.</summary>
    /// <returns>The package refusal assertions.</returns>
    [Fact]
    public async Task CompletePackageSelectorRejectsMismatchedProjectsSnapshotAsync()
    {
        using WorkspaceGitFixture fixture = await CompleteEnvironmentFixture.CreateGitAsync().ConfigureAwait(true);
        (string manifest, Dictionary<string, string> commits, Dictionary<string, string> hashes) = await CompleteEnvironmentFixture.PopulateGitAsync(fixture, package: false).ConfigureAwait(true);
        CancellationToken token = TestContext.Current.CancellationToken;
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, token).ConfigureAwait(true);
        CompositionDeclarationOptions options = new(mapping, [manifest])
        {
            ReleaseCommits = commits,
            ReleaseSnapshotHashes = hashes,
        };
        IReadOnlyList<PlatformModuleDeclaration> closure = await CompositionDeclarationClosure.LoadForConsumerAsync(options, token).ConfigureAwait(true);
        closure.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal)
            .ShouldBe(CompleteEnvironmentFixture.SelectedOwners);

        string provenance = Path.Combine(mapping.Root, "src", "Hexalith.Platform.Composition", "Declarations", "provenance.json");
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(provenance, token).ConfigureAwait(true))!;
        document["projects"]!["version"] = "9.3.5";
        await File.WriteAllTextAsync(provenance, document.ToJsonString(), token).ConfigureAwait(true);

        InvalidDataException failure = await Should.ThrowAsync<InvalidDataException>(() =>
            CompositionDeclarationClosure.LoadForConsumerAsync(options, token)).ConfigureAwait(true);
        failure.Message.ShouldContain("Hexalith.Projects", customMessage: failure.Message);
        failure.Message.ShouldContain("Builds catalog version '9.3.6'");
    }

    /// <summary>Accepts a supplied snapshot with the same effective declaration defaults as the authenticated release.</summary>
    [Fact]
    public void PackageClosureAcceptsEqualSnapshotWithImplicitDefaults()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = PackageOptions(workspace.Root);
        string snapshot = Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations", "memories.json");
        string supplied = Path.Combine(workspace.Root, "supplied-memories.json");
        File.Copy(snapshot, supplied);
        IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(options with { DeclarationPaths = [supplied] });

        PlatformModuleDeclaration owner = closure.ShouldHaveSingleItem();
        CompositionDeclarationClosure.Id(owner.Declaration).ShouldBe("memories");
        Path.GetFullPath(owner.Source).ShouldBe(snapshot);
    }

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
        CompositionDeclarationClosure.Repository("mcpcli").ShouldBe("Hexalith.McpCli");
        Path.GetFullPath(declarations.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "memories").Source).ShouldBe(Path.Combine(workspace.Root, "references", "Hexalith.Memories", "module", "hexalith.module-manifest.v2.json"));
    }

    /// <summary>Explicit reference owners add their dependencies without changing the active owner's closure.</summary>
    /// <param name="package">Whether authenticated released declarations supply the closure.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitQualificationOwnerIsAdditive(bool package)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = package
            ? PackageOptions(workspace.Root) with { ActiveModule = "eventstore", DomainModule = false }
            : new CompositionDeclarationOptions(CompositionTestFiles.CreatePlatformMapping(workspace.Root), [workspace.Save()]) { ActiveModule = "eventstore" };
        IReadOnlyList<PlatformModuleDeclaration> original = CompositionDeclarationClosure.Load(options);
        original.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).ShouldBe(["eventstore"]);
        IReadOnlyList<PlatformModuleDeclaration> expanded = CompositionDeclarationClosure.Load(options with { QualificationOwners = ["tenants"] });
        expanded.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["eventstore", "tenants"]);
        CompositionDeclarationClosure.Load(options).Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).ShouldBe(["eventstore"]);
        IReadOnlyList<PlatformModuleDeclaration> all = CompositionDeclarationClosure.Load(options with { ActiveModule = null, QualificationOwners = ["tenants"] });
        all.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["eventstore", "memories", "tenants"]);
    }

    /// <summary>An explicit owner preserves every declared transitive dependency of both selected owners.</summary>
    [Fact]
    public void ExplicitQualificationOwnerPreservesTransitiveDependencies()
    {
        using PlatformManifestTestWorkspace workspace = new();
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        workspace.Change("modules.0.identity.moduleId", "\"mcpcli\"");
        workspace.Change("modules.0.identity.dependencies", "[\"memories\"]");
        string path = Path.Combine(workspace.Root, "references", "Hexalith.Tenants", "module", "hexalith.module-manifest.v2.json");
        JsonNode tenant = JsonNode.Parse(File.ReadAllText(path))!;
        tenant["modules"]![0]!["identity"]!["dependencies"] = new JsonArray("eventstore");
        File.WriteAllText(path, tenant.ToJsonString());
        IReadOnlyList<PlatformModuleDeclaration> expanded = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [workspace.Save()]) { ActiveModule = "mcpcli", QualificationOwners = ["tenants"] });
        expanded.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["eventstore", "mcpcli", "memories", "tenants"]);
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

    /// <summary>Qualifies the exact released Tenants declaration against the production release authorities.</summary>
    /// <param name="fault">The released provenance or snapshot fault, or none for the valid release.</param>
    /// <param name="refusalText">The expected refusal diagnostic, or empty for a valid closure.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fixture fault is unknown.</exception>
    [Theory]
    [InlineData("none", "")]
    [InlineData("wrong-commit", "does not match Builds catalog version '7.0.2'")]
    [InlineData("missing-commit", "commit: source provenance is unavailable")]
    [InlineData("wrong-hash", "differs from its source provenance")]
    [InlineData("missing-hash", "sha256: source provenance is unavailable")]
    [InlineData("unreleased-snapshot", "does not match Builds catalog version '7.0.2'")]
    public void ReleasedTenantsPackageClosureUsesProductionAuthorities(string fault, string refusalText)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = PackageOptions(workspace.Root);
        Dictionary<string, string> commits = new(CompositionDeclarationOptions.DefaultReleaseCommits, StringComparer.Ordinal);
        Dictionary<string, string> hashes = new(CompositionDeclarationOptions.DefaultReleaseSnapshotHashes, StringComparer.Ordinal);
        foreach (string owner in new[] { "eventstore", "memories" })
        {
            // Only the unrelated owners use fixture authorities; Tenants retains the production defaults.
            commits[owner + "/1.0.0"] = options.ReleaseCommits[owner + "/1.0.0"];
            hashes[owner + "/1.0.0"] = options.ReleaseSnapshotHashes[owner + "/1.0.0"];
        }

        options = options with { ActiveModule = "tenants", ReleaseCommits = commits, ReleaseSnapshotHashes = hashes };
        string catalog = Path.Combine(workspace.Root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        File.WriteAllText(catalog, File.ReadAllText(catalog).Replace("<HexalithTenantsVersion>1.0.0", "<HexalithTenantsVersion>7.0.2", StringComparison.Ordinal));
        string directory = Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations");
        string snapshot = Path.Combine(directory, "tenants.json");
        byte[] released = File.ReadAllBytes(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "platform", "tenants-7.0.2.json"));
        const string releasedHash = "1588f3428ec29296c452fdd35bf95146142315aad3ad8ceb934e7a791f1fb522";
        Convert.ToHexStringLower(SHA256.HashData(released)).ShouldBe(releasedHash);
        File.WriteAllBytes(snapshot, released);
        string provenancePath = Path.Combine(directory, "provenance.json");
        JsonNode provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;

        // Exact owner v7.0.2 tag; the offline fixture preserves its manifest bytes.
        JsonObject tenantProvenance = new()
        {
            ["repository"] = "Hexalith.Tenants",
            ["commit"] = "143cec60a3f5bb1831fa57f35c510880ceb788d8",
            ["version"] = "7.0.2",
            ["source"] = "module/hexalith.module-manifest.v2.json",
            ["sha256"] = releasedHash,
        };
        provenance["tenants"] = tenantProvenance;
        switch (fault)
        {
            case "none":
                break;
            case "wrong-commit":
                tenantProvenance["commit"] = new string('b', 40);
                break;
            case "missing-commit":
                _ = tenantProvenance.Remove("commit");
                break;
            case "wrong-hash":
                tenantProvenance["sha256"] = new string('0', 64);
                break;
            case "missing-hash":
                _ = tenantProvenance.Remove("sha256");
                break;
            case "unreleased-snapshot":
                File.AppendAllText(snapshot, "\n");
                tenantProvenance["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(snapshot)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        File.WriteAllText(provenancePath, provenance.ToJsonString());
        if (fault == "none")
        {
            IReadOnlyList<PlatformModuleDeclaration> declarations = CompositionDeclarationClosure.Load(options);
            declarations.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["eventstore", "memories", "tenants"]);
            Path.GetFullPath(declarations.Single(declaration => CompositionDeclarationClosure.Id(declaration.Declaration) == "tenants").Source).ShouldBe(snapshot);
        }
        else
        {
            InvalidDataException refusal = Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options));
            refusal.Message.ShouldContain("Hexalith.Tenants");
            refusal.Message.ShouldContain(refusalText);
        }
    }

    /// <summary>Verifies malformed release inputs become diagnostics-compatible declaration refusals.</summary>
    /// <param name="fault">The malformed input.</param>
    [Theory]
    [InlineData("provenance-json")]
    [InlineData("provenance-owner")]
    [InlineData("provenance-shape")]
    [InlineData("snapshot-json")]
    [InlineData("snapshot-modules")]
    [InlineData("catalog-xml")]
    [InlineData("catalog-json")]
    [InlineData("catalog-null")]
    [InlineData("catalog-shape")]
    public void MalformedReleaseMetadataBecomesInvalidData(string fault)
    {
        ArgumentNullException.ThrowIfNull(fault);
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = PackageOptions(workspace.Root);
        string directory = Path.Combine(workspace.Root, "src", "Hexalith.Platform.Composition", "Declarations");
        if (fault.StartsWith("provenance", StringComparison.Ordinal))
        {
            string malformed = fault switch
            {
                "provenance-json" => "{",
                "provenance-owner" => "{}",
                _ => "[]",
            };
            File.WriteAllText(Path.Combine(directory, "provenance.json"), malformed);
        }
        else if (fault.StartsWith("snapshot", StringComparison.Ordinal))
        {
            string snapshot = Path.Combine(directory, "eventstore.json");
            File.WriteAllText(snapshot, fault == "snapshot-json" ? "{" : "{\"modules\":[]}");
            string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(snapshot)));
            string provenancePath = Path.Combine(directory, "provenance.json");
            JsonNode provenance = JsonNode.Parse(File.ReadAllText(provenancePath))!;
            provenance["eventstore"]!["sha256"] = hash;
            File.WriteAllText(provenancePath, provenance.ToJsonString());
            options = options with { ReleaseSnapshotHashes = new Dictionary<string, string>(options.ReleaseSnapshotHashes, StringComparer.Ordinal) { ["eventstore/1.0.0"] = hash } };
        }
        else if (fault == "catalog-xml")
        {
            File.WriteAllText(Path.Combine(workspace.Root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props"), "<Project>");
        }
        else
        {
            options = options with { Workspace = workspace.Root };
            string mapping = Path.Combine(workspace.Root, "source-mapping");
            _ = Directory.CreateDirectory(mapping);
            string malformed = fault switch
            {
                "catalog-json" => "{",
                "catalog-null" => "null",
                _ => "{}",
            };
            File.WriteAllText(Path.Combine(mapping, "catalog-versions.json"), malformed);
        }

        InvalidDataException refusal = Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options));
        refusal.Message.ShouldContain(workspace.Root);
        if (fault.StartsWith("catalog", StringComparison.Ordinal))
        {
            Should.Throw<InvalidDataException>(() => options.CatalogVersion("eventstore", directory)).Message.ShouldContain("eventstore");
        }
    }

    /// <summary>Creates explicit offline package snapshots with fixture-only release authorities.</summary>
    /// <param name="root">The isolated repository root.</param>
    /// <returns>The fixture declaration authorities.</returns>
    internal static CompositionDeclarationOptions PackageOptions(string root)
    {
        SourceMapping source = CompositionTestFiles.CreatePlatformMapping(root);
        string directory = Path.Combine(root, "src", "Hexalith.Platform.Composition", "Declarations");
        _ = Directory.CreateDirectory(directory);
        JsonObject provenance = [];
        Dictionary<string, string> commits = [with(StringComparer.Ordinal)];
        Dictionary<string, string> hashes = [with(StringComparer.Ordinal)];
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
