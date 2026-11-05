// <copyright file="DirectCatalogDeclarationTests.cs" company="ITANEO">
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

/// <summary>Loads real released declarations from a verified direct-only module before allocation.</summary>
public sealed class DirectCatalogDeclarationTests
{
    /// <summary>Uses default release authorities after the actual public Git workspace resolver.</summary>
    /// <param name="platformRoot">Whether the command starts at Platform rather than its consuming module.</param>
    /// <returns>The verified cold-start closure assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdStartUsesVerifiedCatalogWithoutNestedModuleDependenciesAsync(bool platformRoot)
    {
        using WorkspaceGitFixture fixture = await CreateReleasedWorkspaceAsync(false).ConfigureAwait(true);
        string manifest = platformRoot ? Path.Combine(fixture.Directory, "platform", "hexalith-platform.json") : fixture.Manifest;
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        mapping.IdentityFindings.ShouldBeEmpty();
        mapping.PlatformIdentity.ShouldNotBeNull().IsPlatformRepository.ShouldBe(platformRoot);
        string nested = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform", "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        File.Exists(nested).ShouldBeFalse();
        CompositionDeclarationOptions options = new(mapping, []);
        options.Workspace.ShouldBeNull();

        IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(options);

        closure.Select(item => CompositionDeclarationClosure.Id(item.Declaration)).Order(StringComparer.Ordinal).ShouldBe(["eventstore", "memories", "tenants"]);
        File.Exists(nested).ShouldBeFalse();
        Directory.Exists(Path.Combine(fixture.Checkout, "source-mapping")).ShouldBeFalse();
    }

    /// <summary>Rejects invalid direct authority at the real resolver before declaration loading.</summary>
    /// <param name="fault">The checkout or pin fault.</param>
    /// <returns>The unchanged fail-closed identity assertions.</returns>
    [Theory]
    [InlineData("dirty-catalog")]
    [InlineData("missing-catalog")]
    [InlineData("wrong-pin")]
    [InlineData("dirty-platform")]
    public async Task InvalidDirectAuthorityStillRefusesBeforeAllocationAsync(string fault)
    {
        using WorkspaceGitFixture fixture = await CreateReleasedWorkspaceAsync(false).ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        string catalog = Path.Combine(builds, "Props", "Directory.Packages.props");
        if (fault == "dirty-catalog")
        {
            await File.AppendAllTextAsync(catalog, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else if (fault == "missing-catalog")
        {
            File.Delete(catalog);
        }
        else if (fault == "wrong-pin")
        {
            _ = await WorkspaceGitFixture.RunGitAsync(builds, "checkout", "HEAD~1").ConfigureAwait(true);
        }
        else
        {
            await File.AppendAllTextAsync(Path.Combine(fixture.Checkout, "references", "Hexalith.Platform", "hexalith-platform.json"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Diagnostics.ShouldContain(item => item.RuleId == (fault == "dirty-platform" ? "HXW011" : "HXW009"));
    }

    /// <summary>Default release authorities remain strict after resolving a valid direct catalog.</summary>
    /// <param name="unknownVersion">Whether the committed catalog selects an unreleased owner version.</param>
    /// <returns>The real snapshot hash or unknown-version refusal.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedClosureStillRefusesChangedHashOrUnknownVersionAsync(bool unknownVersion)
    {
        using WorkspaceGitFixture fixture = await CreateReleasedWorkspaceAsync(unknownVersion).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (!unknownVersion)
        {
            string snapshot = Path.Combine(mapping.PlatformIdentity!.PlatformPath, "src", "Hexalith.Platform.Composition", "Declarations", "eventstore.json");
            await File.AppendAllTextAsync(snapshot, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        InvalidDataException refusal = Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [])));
        refusal.Message.ShouldContain(unknownVersion ? "does not match Builds catalog version '999.0.0'" : "differs from its source provenance");
    }

    private static async Task<WorkspaceGitFixture> CreateReleasedWorkspaceAsync(bool unknownVersion)
    {
        WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        try
        {
            string builds = Path.Combine(fixture.Directory, "builds");
            string catalog = await File.ReadAllTextAsync(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "Props", "Directory.Packages.props"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (unknownVersion)
            {
                catalog = catalog.Replace(">4.1.0</HexalithEventStoreVersion>", ">999.0.0</HexalithEventStoreVersion>", StringComparison.Ordinal);
            }

            await File.WriteAllTextAsync(Path.Combine(builds, "Props", "Directory.Packages.props"), catalog, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(builds, "commit", "-am", "test: bind catalog fixture").ConfigureAwait(true);
            string catalogCommit = (await WorkspaceGitFixture.RunGitAsync(builds, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
            string platform = Path.Combine(fixture.Directory, "platform");
            await SelectAsync(Path.Combine(platform, "references", "Hexalith.Builds"), catalogCommit).ConfigureAwait(true);
            await WriteReleasedDeclarationsAsync(platform).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "src", "references/Hexalith.Builds").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "test: bind catalog fixture").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "v1.0.1").ConfigureAwait(true);
            string platformCommit = (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
            string root = Path.Combine(fixture.Directory, "root");
            await SelectAsync(Path.Combine(root, "references", "Hexalith.Builds"), catalogCommit).ConfigureAwait(true);
            await SelectAsync(Path.Combine(root, "references", "Hexalith.Platform"), platformCommit).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(root, "commit", "-am", "test: bind catalog fixture").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "pull", "--ff-only").ConfigureAwait(true);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    private static async Task WriteReleasedDeclarationsAsync(string platform)
    {
        string declarations = Path.Combine(platform, "src", "Hexalith.Platform.Composition", "Declarations");
        _ = Directory.CreateDirectory(declarations);
        JsonObject provenance = [];
        foreach ((string owner, string version) in new[] { ("eventstore", "4.1.0"), ("tenants", "7.0.4"), ("memories", "3.0.24") })
        {
            byte[] bytes = await File.ReadAllBytesAsync(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "platform", owner + "-" + version + ".json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            string key = owner + "/" + version;
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            hash.ShouldBe(CompositionDeclarationOptions.DefaultReleaseSnapshotHashes[key]);
            if (owner == "memories")
            {
                hash.ShouldBe("56f6dc8134d3f7cea06910e923a4736a450e00241e8e4a565ac9226897881840");
                CompositionDeclarationOptions.DefaultReleaseCommits[key].ShouldBe("859680b38a77f04cc8f81ab7a5fe7f40a617cc2a");
            }

            await File.WriteAllBytesAsync(Path.Combine(declarations, owner + ".json"), bytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            provenance[owner] = new JsonObject
            {
                ["repository"] = CompositionDeclarationClosure.Repository(owner),
                ["version"] = version,
                ["commit"] = CompositionDeclarationOptions.DefaultReleaseCommits[key],
                ["source"] = "module/hexalith.module-manifest.v2.json",
                ["sha256"] = hash,
            };
        }

        await File.WriteAllTextAsync(Path.Combine(declarations, "provenance.json"), provenance.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    private static async Task SelectAsync(string repository, string commit)
    {
        _ = await WorkspaceGitFixture.RunGitAsync(repository, "fetch", "origin").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(repository, "checkout", commit).ConfigureAwait(true);
    }
}
