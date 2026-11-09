// <copyright file="CompositionCatalogPackageViewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Composition selects only a validated, explicitly recorded consumer view.</summary>
public sealed class CompositionCatalogPackageViewTests
{
    /// <summary>Selects the recorded default or named view without losing the original overload.</summary>
    /// <param name="project">The requested consumer, or null for the default view.</param>
    /// <param name="expected">The selected pinned version.</param>
    [Theory]
    [InlineData(null, "1.0.0")]
    [InlineData("hexalith.platform.eventstorehost", "2.0.0")]
    public void RecordedPackageViewIsSelected(string? project, string expected)
    {
        string root = Directory.CreateTempSubdirectory("catalog-package-view-").FullName;
        try
        {
            CompositionDeclarationOptions options = Options(root);
            options.CatalogPackageVersion("Package.Probe", "probe", project).ShouldBe(expected);
            options.CatalogPackageVersion("Package.Missing", "probe", project).ShouldBeNull();
            options.CatalogPackageVersion("Package.Probe", "probe").ShouldBe("1.0.0");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Refuses a missing consumer view without consulting the default or live catalog.</summary>
    /// <param name="missingSnapshot">Whether the whole snapshot is absent.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingConsumerViewNeverFallsBackToDefaultOrLiveCatalog(bool missingSnapshot)
    {
        string root = Directory.CreateTempSubdirectory("catalog-package-view-").FullName;
        try
        {
            CompositionDeclarationOptions options = Options(root);
            if (missingSnapshot)
            {
                File.Delete(Path.Combine(root, "source-mapping", "catalog-versions.json"));
            }

            Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion("Package.Probe", "probe", "Unrecorded.Host"))
                .Message.ShouldContain("Unrecorded.Host");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Validates the snapshot before reading the requested consumer.</summary>
    [Fact]
    public void TamperedConsumerViewIsRefusedBeforeSelection()
    {
        string root = Directory.CreateTempSubdirectory("catalog-package-view-").FullName;
        try
        {
            CompositionDeclarationOptions options = Options(root);
            string path = Path.Combine(root, "source-mapping", "catalog-versions.json");
            File.WriteAllText(path, File.ReadAllText(path).Replace("2.0.0", "3.0.0", StringComparison.Ordinal));
            Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion("Package.Probe", "probe", "Hexalith.Platform.EventStoreHost"))
                .Message.ShouldContain("catalog snapshot differs");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Reads consumer versions evaluated by Builds from its committed pinned tree.</summary>
    /// <returns>The materialized consumer-view assertions.</returns>
    [Fact]
    public async Task MaterializedConsumerViewUsesPinnedCatalogDespiteLiveDriftAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "consumer-view-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace };
        const string package = "CommunityToolkit.Aspire.Hosting.Dapr";
        options.CatalogPackageVersion(package, "probe").ShouldBe("13.6.0-preview.1");
        options.CatalogPackageVersion(package, "probe", "hexalith.folders.aspire").ShouldBe("13.0.0");

        string liveCatalog = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        await File.WriteAllTextAsync(liveCatalog, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        options.CatalogPackageVersion(package, "probe", "Hexalith.Folders.Aspire").ShouldBe("13.0.0");
    }

    private static CompositionDeclarationOptions Options(string root)
    {
        SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Platform", [], false);
        Dictionary<string, string> versions = new() { ["Package.Probe"] = "1.0.0" };
        Dictionary<string, string> pinned = new() { ["Props/Directory.Packages.props"] = new string('a', 64) };
        Dictionary<string, IReadOnlyDictionary<string, string>> projects = new()
        {
            ["Hexalith.Platform.EventStoreHost"] = new Dictionary<string, string> { ["Package.Probe"] = "2.0.0" },
        };
        string commit = new('b', 40);
        string hash = new('a', 64);
        CatalogVersionSnapshot snapshot = new(
            mapping.ContentHash,
            commit,
            hash,
            pinned,
            versions,
            projects,
            CatalogVersionSnapshot.ComputeHash(mapping.ContentHash, commit, hash, pinned, versions, projects));
        _ = Directory.CreateDirectory(Path.Combine(root, "source-mapping"));
        File.WriteAllText(Path.Combine(root, "source-mapping", "catalog-versions.json"), JsonSerializer.Serialize(snapshot));
        return new CompositionDeclarationOptions(mapping, []) { Workspace = root };
    }
}
