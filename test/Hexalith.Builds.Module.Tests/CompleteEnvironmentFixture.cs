// <copyright file="CompleteEnvironmentFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Provides source owners and fixture-only package release authority for the complete Platform command.</summary>
internal static class CompleteEnvironmentFixture
{
    private const string _contractVersion = "9.3.6";
    private const string _contractPackage = "Hexalith.Parties.Contracts";
    private const string _unpinnedContract = "Hexalith.Unpinned.Contracts";
    private const string _module = """
        {"identity":{"moduleId":"fixture","servers":[],"dependencies":[]},
        "runtime":{"dapr":[],"extensions":[],"resources":[]},
        "surfaces":{"interfaces":[]},
        "integration":{"topics":[],"secrets":[],"dynamicSecretNamespaces":[],"identityNeeds":[],"egress":[],"providers":[],"workers":[]},
        "lifecycle":{"readiness":[],"tasks":[],"recoveryHooks":[],"fenceHooks":[],"replay":{"reopenBeforeReplay":false},"startup":{},
        "criticalFlows":[],"smokeSurfaces":[],"classification":{"changeClass":"additive","reason":"Complete environment fixture."},"recoveryInventory":[]}}
        """;

    private static readonly Dictionary<string, (string Package, string[] Dependencies)> _owners = new(StringComparer.Ordinal)
    {
        ["eventstore"] = ("Hexalith.EventStore.Aspire", []),
        ["tenants"] = ("Hexalith.Tenants.Server", ["eventstore"]),
        ["parties"] = (_contractPackage, ["eventstore", "tenants"]),
        ["folders"] = ("Hexalith.Folders.Contracts", ["eventstore"]),
        ["projects"] = ("Hexalith.Projects.EventStoreExtensions", ["conversations", "folders", "parties"]),
        ["mcpcli"] = ("Hexalith.McpCli.Abstractions", ["eventstore"]),
        ["memories"] = ("Hexalith.Memories.Aspire", ["eventstore"]),
        ["conversations"] = ("Hexalith.Conversations.Contracts", ["tenants", "memories"]),
    };

    /// <summary>Gets the complete closure in sorted owner order, including its selector.</summary>
    internal static IEnumerable<string> SelectedOwners => _owners.Keys.Append("platform").Order(StringComparer.Ordinal);

    /// <summary>Gets the version used by both the source Contracts and their fixture package.</summary>
    internal static string ContractLibrary => _contractPackage + "/" + _contractVersion;

    /// <summary>Writes the complete serverless selector to its canonical location.</summary>
    /// <param name="root">The Platform root.</param>
    /// <returns>The manifest path.</returns>
    internal static string WriteSelector(string root) => WriteManifest(root, "platform", _owners.Keys.Where(owner => owner != "conversations"));

    /// <summary>Writes a named owner declaration and its dependencies.</summary>
    /// <param name="root">The declaring repository.</param>
    /// <param name="owner">The declaring identity.</param>
    /// <param name="dependencies">The dependencies of that owner.</param>
    /// <returns>The canonical manifest path.</returns>
    internal static string WriteManifest(string root, string owner, IEnumerable<string> dependencies)
    {
        JsonObject declaration = JsonNode.Parse(_module)!.AsObject();
        declaration["identity"]!["moduleId"] = owner;
        declaration["identity"]!["dependencies"] = new JsonArray([.. dependencies.Select(dependency => (JsonNode?)JsonValue.Create(dependency))]);
        if (owner == "folders")
        {
            declaration["lifecycle"]!["startup"] = JsonNode.Parse("""{"override":{"timeoutSeconds":80,"justification":"Folders fixture startup"}}""");
        }
        else if (owner == "conversations")
        {
            declaration["lifecycle"]!["startup"] = JsonNode.Parse("""{"override":{"timeoutSeconds":120,"justification":"Conversations fixture startup"}}""");
        }

        JsonObject document = new()
        {
            ["schema"] = "hexalith.module-manifest.v2",
            ["modules"] = new JsonArray(declaration),
        };
        string manifest = Path.Combine(root, "module", "hexalith.module-manifest.v2.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        File.WriteAllText(manifest, document.ToJsonString());
        return manifest;
    }

    /// <summary>Prepares a lightweight mapped source selection without running Git.</summary>
    /// <param name="root">The temporary Platform root.</param>
    /// <returns>The mapped selector.</returns>
    internal static CompositionDeclarationOptions CreateSourceOptions(string root)
    {
        List<SourceMappingEntry> entries = [new("Hexalith.Platform", "source", root)];
        foreach ((string owner, (_, string[] dependencies)) in _owners)
        {
            string repository = CompositionDeclarationClosure.Repository(owner);
            string checkout = Path.Combine(root, "references", repository);
            _ = WriteManifest(checkout, owner, dependencies);
            entries.Add(new SourceMappingEntry(repository, "source", checkout));
        }

        string commit = new('a', 40);
        SourceMapping mapping = new(WorkspaceMode.Source, root, "Hexalith.Platform", entries, true)
        {
            PlatformIdentity = new PlatformIdentity(commit, commit, commit, root, null, commit, "[0.0.0,)", Path.Combine(root, "hexalith-platform.json"), true),
        };
        return new CompositionDeclarationOptions(mapping, [WriteSelector(root)]) { ActiveModule = "platform" };
    }

    /// <summary>Creates a real Platform Git root, direct owner gitlinks and a catalog pinned by Platform HEAD.</summary>
    /// <returns>The disposable fixture containing the Platform root.</returns>
    internal static Task<WorkspaceGitFixture> CreateGitAsync() => WorkspaceGitFixture.CreateWithCatalogAsync(
        "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup>"
        + string.Concat(_owners.Values.Select(owner => $$"""<PackageVersion Include="{{owner.Package}}" Version="{{_contractVersion}}" />"""))
        + """<HexalithCatalogConsumer Include="Hexalith.Platform.Composition" /></ItemGroup></Project>""");

    /// <summary>Populates exact Git owner checkouts plus fixture release snapshots and their provenance.</summary>
    /// <param name="fixture">The Git fixture.</param>
    /// <param name="package">Whether to generate a local Contracts package.</param>
    /// <param name="unpinnedTransitively">Whether Parties depends on a second Contracts package outside the catalog.</param>
    /// <param name="mcpCliReference">The checkout directory of the tool owner on a case-insensitive identity match.</param>
    /// <returns>The selector path and fixture release authorities.</returns>
    internal static async Task<(string Manifest, Dictionary<string, string> Commits, Dictionary<string, string> Hashes)> PopulateGitAsync(
        WorkspaceGitFixture fixture, bool package, bool unpinnedTransitively = false, string mcpCliReference = "Hexalith.McpCli")
    {
        CancellationToken token = TestContext.Current.CancellationToken;
        string root = Path.Combine(fixture.Directory, "platform");
        string feed = Path.Combine(fixture.Directory, "feed");
        _ = Directory.CreateDirectory(feed);
        string snapshots = Path.Combine(root, "src", "Hexalith.Platform.Composition", "Declarations");
        _ = Directory.CreateDirectory(snapshots);
        JsonObject provenance = [];
        Dictionary<string, string> commits = [with(StringComparer.Ordinal)];
        Dictionary<string, string> hashes = [with(StringComparer.Ordinal)];
        foreach ((string owner, (_, string[] dependencies)) in _owners)
        {
            string repository = CompositionDeclarationClosure.Repository(owner);
            string reference = owner == "mcpcli" ? mcpCliReference : repository;
            string origin = Path.Combine(fixture.Directory, "owner-" + owner);
            _ = Directory.CreateDirectory(origin);
            _ = await WorkspaceGitFixture.RunGitAsync(origin, "init", "-b", "main").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(origin, "config", "user.name", "Workspace Test").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(origin, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
            string manifest = WriteManifest(origin, owner, dependencies);
            if (owner == "parties")
            {
                if (unpinnedTransitively)
                {
                    string transitive = Path.Combine(fixture.Directory, "transitive-contracts");
                    WriteContract(transitive, _unpinnedContract);
                    await PackContractAsync(transitive, _unpinnedContract, feed, Path.Combine(fixture.Directory, "pack-transitive"), token).ConfigureAwait(true);
                }

                WriteContract(origin, _contractPackage, unpinnedTransitively ? feed : null);
                if (package)
                {
                    await PackContractAsync(origin, _contractPackage, feed, Path.Combine(fixture.Directory, "pack-output"), token).ConfigureAwait(true);
                }
            }
            else if (owner == "mcpcli")
            {
                WriteTool(origin, feed, Path.Combine(fixture.Directory, "packages"));
            }

            _ = await WorkspaceGitFixture.RunGitAsync(origin, "add", ".").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(origin, "commit", "-m", "fixture owner declaration").ConfigureAwait(true);
            string commit = (await WorkspaceGitFixture.RunGitAsync(origin, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
            byte[] bytes = await File.ReadAllBytesAsync(manifest, token).ConfigureAwait(true);
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            commits.Add(owner + "/" + _contractVersion, commit);
            hashes.Add(owner + "/" + _contractVersion, hash);
            provenance[owner] = new JsonObject
            {
                ["repository"] = repository,
                ["version"] = _contractVersion,
                ["commit"] = commit,
                ["source"] = "module/hexalith.module-manifest.v2.json",
                ["sha256"] = hash,
            };
            await File.WriteAllBytesAsync(Path.Combine(snapshots, owner + ".json"), bytes, token).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(origin).AbsoluteUri, "references/" + reference).ConfigureAwait(true);
        }

        await File.WriteAllTextAsync(Path.Combine(snapshots, "provenance.json"), provenance.ToJsonString(), token).ConfigureAwait(true);
        string selector = WriteSelector(root);
        _ = await WorkspaceGitFixture.RunGitAsync(root, "add", ".").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(root, "commit", "-m", "select complete fixture environment").ConfigureAwait(true);
        return (selector, commits, hashes);
    }

    private static async Task PackContractAsync(string root, string package, string feed, string artifacts, CancellationToken token)
    {
        string project = Path.Combine(root, "src", package, package + ".csproj");
        CompositionProcessResult pack = await CompositionProcess.RunAsync(
            CompositionSdkProcess.CreateStartInfo(["pack", project, "--output", feed, "--artifacts-path", artifacts], root),
            TimeSpan.FromMinutes(2),
            token).ConfigureAwait(true);
        pack.ExitCode.ShouldBe(0, pack.Output + pack.ErrorOutput);
    }

    private static void WriteContract(string root, string package, string? transitiveFeed = null)
    {
        string directory = Path.Combine(root, "src", package);
        _ = Directory.CreateDirectory(directory);
        string transitive = transitiveFeed is null
            ? string.Empty
            : $$"""
                <PropertyGroup><RestoreSources>{{System.Security.SecurityElement.Escape(transitiveFeed)}}</RestoreSources></PropertyGroup>
                <ItemGroup><PackageReference Include="{{_unpinnedContract}}" Version="{{_contractVersion}}" /></ItemGroup>
                """;
        File.WriteAllText(Path.Combine(directory, package + ".csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net11.0</TargetFramework>
                <Version>{{_contractVersion}}</Version>
              </PropertyGroup>
              {{transitive}}
            </Project>
            """);
        string type = package == _contractPackage ? "PartyContract" : "UnpinnedContract";
        File.WriteAllText(Path.Combine(directory, type + ".cs"), $"public static class {type} {{ public const int Value = 1; }}");
    }

    private static void WriteTool(string root, string feed, string packages)
    {
        File.WriteAllText(Path.Combine(root, ".gitignore"), "Directory.Build.targets\n");
        File.WriteAllText(Path.Combine(root, "NuGet.Config"), $"""
            <configuration>
              <packageSources><clear /><add key="fixture" value="{System.Security.SecurityElement.Escape(feed)}" /></packageSources>
              <config><add key="globalPackagesFolder" value="{System.Security.SecurityElement.Escape(packages)}" /></config>
            </configuration>
            """);
        string directory = Path.Combine(root, "src", "Hexalith.McpCli");
        string helper = Path.Combine(root, "src", "Hexalith.McpCli.Core");
        _ = Directory.CreateDirectory(directory);
        _ = Directory.CreateDirectory(helper);
        File.WriteAllText(Path.Combine(directory, "Hexalith.McpCli.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net11.0</TargetFramework>
                <PackAsTool>true</PackAsTool>
                <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Hexalith.McpCli.Core/Hexalith.McpCli.Core.csproj" />
                <PackageReference Include="{{_contractPackage}}" HexalithContracts="true" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(directory, "Program.cs"), "return ToolCore.Value - PartyContract.Value;");
        File.WriteAllText(Path.Combine(helper, "Hexalith.McpCli.Core.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>
            """);
        File.WriteAllText(Path.Combine(helper, "ToolCore.cs"), "public static class ToolCore { public const int Value = 1; }");
    }
}
