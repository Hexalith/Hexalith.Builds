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

    /// <summary>Materializes every retained consumer view from the owning catalog while preserving its current default.</summary>
    /// <returns>The finite consumer-view and unknown-name refusal assertions.</returns>
    [Fact]
    public async Task OwningCatalogMaterializesTheEventStoreHostViewWithoutAPrematureFixturePinAsync()
    {
        string catalog = await File.ReadAllTextAsync(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateWithCatalogAsync(catalog).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "actual-host-consumer");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(workspace, "source-mapping", "catalog-versions.json"));
        string[] retainedConsumers =
        [
            "Hexalith.Parties",
            "Hexalith.Parties.AdminPortal",
            "Hexalith.Parties.AdminPortal.Tests",
            "Hexalith.Parties.AppHost",
            "Hexalith.Parties.Aspire",
            "Hexalith.Parties.Aspire.Tests",
            "Hexalith.Parties.Authentication",
            "Hexalith.Parties.Authentication.Tests",
            "Hexalith.Parties.Catalog.Tests",
            "Hexalith.Parties.Ci.Tests",
            "Hexalith.Parties.Client",
            "Hexalith.Parties.Client.Tests",
            "Hexalith.Parties.ConsumerPortal",
            "Hexalith.Parties.ConsumerPortal.Tests",
            "Hexalith.Parties.Contracts",
            "Hexalith.Parties.Contracts.Tests",
            "Hexalith.Parties.EventStoreAdminUI.TestHost",
            "Hexalith.Parties.EventStoreExtensions",
            "Hexalith.Parties.EventStoreGateway.TestHost",
            "Hexalith.Parties.EventStoreSample.Contracts.TestArtifact",
            "Hexalith.Parties.EventStoreSample.TestHost",
            "Hexalith.Parties.IntegrationTests",
            "Hexalith.Parties.Mcp",
            "Hexalith.Parties.Mcp.Tests",
            "Hexalith.Parties.Picker",
            "Hexalith.Parties.Picker.Tests",
            "Hexalith.Parties.Platform.AttachedLifecycleTests",
            "Hexalith.Parties.Platform.IntegrationTests",
            "Hexalith.Parties.Platform.LifecycleTests",
            "Hexalith.Parties.Projections",
            "Hexalith.Parties.Projections.Tests",
            "Hexalith.Parties.Sample",
            "Hexalith.Parties.Sample.Tests",
            "Hexalith.Parties.Security",
            "Hexalith.Parties.Security.Tests",
            "Hexalith.Parties.Server.Tests",
            "Hexalith.Parties.Testing",
            "Hexalith.Parties.Tests",
            "Hexalith.Parties.UI",
            "Hexalith.Parties.UI.Tests",
            "Hexalith.Platform.CatalogBuilder",
            "Hexalith.Platform.Composition",
            "Hexalith.Platform.Composition.Tests",
            "Hexalith.Platform.Custody",
            "Hexalith.Platform.Custody.Component",
            "Hexalith.Platform.Custody.Component.Tests",
            "Hexalith.Platform.Custody.Tests",
            "Hexalith.Platform.EventStoreHost",
            "Hexalith.Platform.Identity",
            "Hexalith.Platform.Identity.Tests",
            "Hexalith.Platform.OwnerServices.Tests",
            "Hexalith.Platform.Protection",
            "Hexalith.Platform.Protection.Qualification.Enrollment",
            "Hexalith.Platform.Protection.Qualification.Executor",
            "Hexalith.Platform.Protection.Qualification.Observer",
            "Hexalith.Platform.Protection.Tests",
            "Hexalith.Platform.Secrets.ArchiveProcessFixture",
            "Hexalith.Platform.Secrets.Authority",
            "Hexalith.Platform.Secrets.Authority.Tests",
            "Hexalith.Platform.Secrets.Custody",
            "Hexalith.Platform.Secrets.Custody.Tests",
            "Hexalith.Platform.Secrets.Inventory",
            "Hexalith.Platform.Secrets.Inventory.Tests",
            "Hexalith.Platform.Secrets.Qualification.Enrollment",
            "Hexalith.Platform.Secrets.Qualification.Executor",
            "Hexalith.Platform.Secrets.Qualification.Observer",
            "Hexalith.Platform.Secrets.Runtime",
            "Hexalith.Platform.Secrets.Runtime.Tests",
            "Hexalith.Platform.TenantRegistry.Host",
            "Hexalith.Platform.TestExtension",
            "Hexalith.Platform.TestExtension.Qualification.Tests",
            "Hexalith.Platform.Works.Runtime",
            "Hexalith.Platform.Works.Tests",
            "Hexalith.Builds.Module.AppHost",
            "Hexalith.Builds.Module.EventStoreHost",
            "Hexalith.Builds.Module.UiHost",
        ];
        foreach (string consumer in retainedConsumers)
        {
            snapshot.ProjectVersions.ContainsKey(consumer).ShouldBeTrue(consumer);
            IReadOnlyDictionary<string, string> versions = snapshot.ProjectVersions[consumer];
            bool currentPlatformView = consumer.StartsWith("Hexalith.Platform.", StringComparison.Ordinal);
            AssertEventStoreFamily(versions, "4.2.0", consumer);
            versions["Microsoft.AspNetCore.OpenApi"].ShouldBe("10.0.15", consumer);
            versions["Microsoft.OpenApi"].ShouldBe("2.13.0", consumer);
            foreach (KeyValuePair<string, string> package in versions.Where(pair => pair.Key.StartsWith("Hexalith.Memories.", StringComparison.Ordinal)))
            {
                package.Value.ShouldBe(currentPlatformView ? "3.0.27" : "4.0.0", consumer + ":" + package.Key);
            }
        }

        CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace };
        AssertEventStoreFamily(snapshot.Versions, "5.1.0", "default");
        snapshot.Versions["Hexalith.Memories.Contracts"].ShouldBe("4.0.0");
        options.CatalogVersion("memories", "probe", "Hexalith.Platform.Composition").ShouldBe("3.0.27");
        options.CatalogVersion("eventstore", "probe").ShouldBe("5.1.0");
        options.CatalogVersion("eventstore", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("4.2.0");
        CompositionDeclarationOptions platform = options with { ConsumerProjectName = "Hexalith.Platform.EventStoreHost" };
        platform.CatalogVersion("eventstore", "probe").ShouldBe("4.2.0");
        options.CatalogPackageVersion("Hexalith.EventStore.DomainService", "probe").ShouldBe("5.1.0");
        options.CatalogPackageVersion("Microsoft.AspNetCore.OpenApi", "probe").ShouldBe("11.0.0");
        options.CatalogPackageVersion("Microsoft.OpenApi", "probe").ShouldBe("3.11.0");
        options.CatalogPackageVersion("Microsoft.AspNetCore.OpenApi", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("10.0.15");
        options.CatalogPackageVersion("Microsoft.OpenApi", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("2.13.0");
        options.CatalogPackageVersion("Hexalith.EventStore.DomainService", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("4.2.0");
        options.CatalogPackageVersion("Hexalith.EventStore.Server", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("4.2.0");
        options.CatalogPackageVersion("Hexalith.Platform.TestExtension", "probe", "Hexalith.Platform.EventStoreHost").ShouldBeNull();
        options.CatalogPackageVersion("Hexalith.EventStore.DomainService", "probe", "Hexalith.Parties.EventStoreGateway.TestHost").ShouldBe("4.2.0");
        options.CatalogPackageVersion("Hexalith.EventStore.DomainService", "probe", "Hexalith.Platform.Composition").ShouldBe("4.2.0");
        foreach (string unknown in new[] { "Hexalith.Parties.Future", "Hexalith.Platform.Future", "Hexalith.Builds.Module.Future", "Hexalith.Builds.Module.EventStoreHost.Future" })
        {
            snapshot.ProjectVersions.ContainsKey(unknown).ShouldBeFalse();
            Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion("Hexalith.EventStore.DomainService", "probe", unknown))
                .Message.ShouldContain(unknown);
        }
    }

    /// <summary>Preserves an explicitly selected historical SDK while recording the actual composed-host view.</summary>
    /// <param name="selected">The explicit historical SDK selection.</param>
    /// <param name="aspNetOpenApi">The consumer's compatible ASP.NET OpenAPI selection.</param>
    /// <param name="openApi">The consumer's compatible OpenAPI ABI selection.</param>
    /// <returns>The materialized host-view version assertion.</returns>
    [Theory]
    [InlineData("4.0.4", "11.0.0", "3.11.0")]
    [InlineData("4.1.0", "11.0.0", "3.11.0")]
    [InlineData("4.2.0", "10.0.15", "2.13.0")]
    [InlineData("5.1.0", "11.0.0", "3.11.0")]
    public async Task OwningHostViewPreservesExplicitHistoricalSdkSelectionAsync(string selected, string aspNetOpenApi, string openApi)
    {
        string catalog = await File.ReadAllTextAsync(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        System.Xml.Linq.XDocument document = System.Xml.Linq.XDocument.Parse(catalog);
        document.Root!.AddFirst(new System.Xml.Linq.XElement("PropertyGroup", new System.Xml.Linq.XElement("HexalithEventStoreVersion", selected)));
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateWithCatalogAsync(document.ToString()).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "historical-host-consumer");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(workspace, "source-mapping", "catalog-versions.json"));
        AssertEventStoreFamily(snapshot.Versions, selected, "default");
        AssertEventStoreFamily(snapshot.ProjectVersions["Hexalith.Platform.EventStoreHost"], selected, "Hexalith.Platform.EventStoreHost");
        snapshot.ProjectVersions["Hexalith.Platform.EventStoreHost"]["Hexalith.Memories.Contracts"]
            .ShouldBe(selected == "4.2.0" ? "3.0.27" : "4.0.0");
        CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace };
        options.CatalogVersion("eventstore", "probe").ShouldBe(selected);
        options.CatalogVersion("eventstore", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe(selected);
        CompositionDeclarationOptions platform = options with { ConsumerProjectName = "Hexalith.Platform.EventStoreHost" };
        platform.CatalogVersion("eventstore", "probe").ShouldBe(selected);
        options.CatalogPackageVersion("Hexalith.EventStore.Server", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe(selected);
        options.CatalogPackageVersion("Hexalith.EventStore.Server", "probe").ShouldBe(selected);
        options.CatalogPackageVersion("Microsoft.AspNetCore.OpenApi", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe(aspNetOpenApi);
        options.CatalogPackageVersion("Microsoft.OpenApi", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe(openApi);
        options.CatalogPackageVersion("Microsoft.AspNetCore.OpenApi", "probe").ShouldBe("11.0.0");
        options.CatalogPackageVersion("Microsoft.OpenApi", "probe").ShouldBe("3.11.0");
    }

    /// <summary>Owner and package APIs share an explicit consumer without changing the default view.</summary>
    [Fact]
    public void OwnerAndPackageLookupsShareExplicitConsumerContext()
    {
        string root = Directory.CreateTempSubdirectory("catalog-owner-view-").FullName;
        try
        {
            CompositionDeclarationOptions options = Options(root);
            options.CatalogVersion("eventstore", "probe").ShouldBe("5.0.0");
            options.CatalogVersion("eventstore", "probe", "Hexalith.Platform.EventStoreHost").ShouldBe("4.2.0");
            CompositionDeclarationOptions consumer = options with { ConsumerProjectName = "Hexalith.Platform.EventStoreHost" };
            consumer.CatalogVersion("eventstore", "probe").ShouldBe("4.2.0");
            consumer.CatalogPackageVersion("Hexalith.EventStore.Aspire", "probe").ShouldBe("4.2.0");
            consumer.CatalogPackageVersion("Package.Probe", "probe").ShouldBe("2.0.0");
            options.CatalogVersion("eventstore", "probe").ShouldBe("5.0.0");
            Should.Throw<InvalidDataException>(() => options.CatalogVersion("eventstore", "probe", "Unrecorded.Host"))
                .Message.ShouldContain("Unrecorded.Host");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Real pinned consumer reads refuse source, pin, mapping, view and snapshot drift.</summary>
    /// <param name="fault">The independently changed authority.</param>
    /// <returns>The pinned consumer refusal assertions.</returns>
    [Theory]
    [InlineData("source")]
    [InlineData("pin")]
    [InlineData("mapping")]
    [InlineData("view")]
    [InlineData("missing-view")]
    [InlineData("missing-snapshot")]
    public async Task PinnedConsumerAuthorityCannotBeReplacedAsync(string fault)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "consumer-integrity");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace, ConsumerProjectName = "Hexalith.Folders.Aspire" };
        const string package = "CommunityToolkit.Aspire.Hosting.Dapr";
        options.CatalogPackageVersion(package, "probe").ShouldBe("13.0.0");
        string path = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        if (fault == "source")
        {
            await File.AppendAllTextAsync(Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else if (fault == "pin")
        {
            CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadSnapshot(path) with { CatalogCommit = new string('f', 40) };
            snapshot = snapshot with { Hash = CatalogVersionSnapshot.ComputeHash(snapshot.MappingHash, snapshot.CatalogCommit, snapshot.CatalogFileHash, snapshot.PinnedFiles, snapshot.Versions, snapshot.ProjectVersions) };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(snapshot), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else if (fault == "mapping")
        {
            options = options with { Mapping = mapping with { ActiveModule = "Changed.Module" } };
        }
        else if (fault == "view")
        {
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("13.0.0", "13.0.1", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else if (fault == "missing-view")
        {
            options = options with { ConsumerProjectName = "Unrecorded.Host" };
        }
        else
        {
            File.Delete(path);
        }

        Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion(package, "probe"))
            .Message.ShouldContain("catalog snapshot");
    }

    /// <summary>Concurrent supervised catalog queries retain independent SDK process lifetimes.</summary>
    /// <returns>The independent pinned version observations.</returns>
    [Fact]
    public async Task ParallelPinnedCatalogReadsRemainIsolatedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string[] workspaces = [.. Enumerable.Range(0, 3).Select(index => Path.Combine(fixture.Directory, "parallel-catalog-" + index))];
        await Task.WhenAll(workspaces.Select(workspace => SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken))).ConfigureAwait(true);
        foreach (string workspace in workspaces)
        {
            CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace, ConsumerProjectName = "Hexalith.Folders.Aspire" };
            options.CatalogPackageVersion("CommunityToolkit.Aspire.Hosting.Dapr", "parallel-probe").ShouldBe("13.0.0");
        }
    }

    private static void AssertEventStoreFamily(IReadOnlyDictionary<string, string> versions, string expectedSdk, string consumer)
    {
        string[] packages =
        [
            "Hexalith.EventStore.Admin.Abstractions",
            "Hexalith.EventStore.Admin.Server",
            "Hexalith.EventStore.Aspire",
            "Hexalith.EventStore.Client",
            "Hexalith.EventStore.Contracts",
            "Hexalith.EventStore.DomainService",
            "Hexalith.EventStore.Gateway",
            "Hexalith.EventStore.RestApi.Generators",
            "Hexalith.EventStore.Server",
            "Hexalith.EventStore.ServiceDefaults",
            "Hexalith.EventStore.SignalR",
            "Hexalith.EventStore.Testing",
            "Hexalith.EventStore.Testing.Integration",
        ];
        foreach (string package in packages)
        {
            versions.ContainsKey(package).ShouldBeTrue($"{consumer}: {package}");
            versions[package].ShouldBe(expectedSdk, $"{consumer}: {package}");
        }
    }

    private static CompositionDeclarationOptions Options(string root)
    {
        SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Platform", [], false);
        Dictionary<string, string> versions = new() { ["Package.Probe"] = "1.0.0", ["Hexalith.EventStore.Aspire"] = "5.0.0" };
        Dictionary<string, string> pinned = new() { ["Props/Directory.Packages.props"] = new string('a', 64) };
        Dictionary<string, IReadOnlyDictionary<string, string>> projects = new()
        {
            ["Hexalith.Platform.EventStoreHost"] = new Dictionary<string, string> { ["Package.Probe"] = "2.0.0", ["Hexalith.EventStore.Aspire"] = "4.2.0" },
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
