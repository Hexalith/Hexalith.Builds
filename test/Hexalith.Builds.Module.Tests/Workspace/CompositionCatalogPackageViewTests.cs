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
    [InlineData("Hexalith.Platform.EventStoreHost", "2.0.0")]
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
        Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion(package, "probe", "hexalith.folders.aspire"))
            .Message.ShouldContain("hexalith.folders.aspire");

        string liveCatalog = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        await File.WriteAllTextAsync(liveCatalog, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        options.CatalogPackageVersion(package, "probe", "Hexalith.Folders.Aspire").ShouldBe("13.0.0");
    }

    /// <summary>Unmaterialized owner and package lookups read the current Tenants default from the real catalog.</summary>
    [Fact]
    public void UnmaterializedOwningCatalogUsesCurrentTenantsVersion()
    {
        string root = Directory.CreateTempSubdirectory("catalog-unmaterialized-tenants-").FullName;
        try
        {
            string catalog = Path.Combine(root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
            File.Copy(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"), catalog);
            SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Platform", [], false);
            CompositionDeclarationOptions options = new(mapping, []);
            string expected = System.Xml.Linq.XDocument.Load(catalog).Descendants("HexalithTenantsVersion").Single().Value;
            options.CatalogVersion("tenants", "probe").ShouldBe(expected);
            foreach (string package in new[] { "Hexalith.Tenants.Aspire", "Hexalith.Tenants.Client", "Hexalith.Tenants.Contracts", "Hexalith.Tenants.Server", "Hexalith.Tenants.Testing" })
            {
                options.CatalogPackageVersion(package, "probe").ShouldBe(expected, package);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Default owner and package reads exclude conditional views regardless of declaration position.</summary>
    /// <param name="declarations">The synthetic catalog's property groups.</param>
    [Theory]
    [InlineData("""
        <PropertyGroup>
          <HexalithTenantsVersion Condition="'$(Consumer)' == 'legacy'">7.0.7</HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">8.0.1</HexalithTenantsVersion>
        </PropertyGroup>
        """)]
    [InlineData("""
        <PropertyGroup>
          <HexalithTenantsVersion>8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(Consumer)' == 'legacy'">7.0.7</HexalithTenantsVersion>
        </PropertyGroup>
        """)]
    [InlineData("""
        <PropertyGroup Condition="'$(Consumer)' == 'legacy'">
          <HexalithTenantsVersion>7.0.7</HexalithTenantsVersion>
        </PropertyGroup>
        <PropertyGroup>
          <HexalithTenantsVersion Condition=' "$(hexalithtenantsversion)"=="" '>8.0.1</HexalithTenantsVersion>
        </PropertyGroup>
        """)]
    [InlineData("""
        <PropertyGroup>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">8.0.1</HexalithTenantsVersion>
        </PropertyGroup>
        <PropertyGroup Condition="'$(Consumer)' == 'legacy'">
          <HexalithTenantsVersion>7.0.7</HexalithTenantsVersion>
        </PropertyGroup>
        """)]
    public void UnmaterializedDefaultExcludesConditionalViews(string declarations)
    {
        string root = Directory.CreateTempSubdirectory("catalog-default-selection-").FullName;
        try
        {
            CompositionDeclarationOptions options = UnmaterializedOptions(root, declarations);
            options.CatalogVersion("tenants", "probe").ShouldBe("8.0.1");
            options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe").ShouldBe("8.0.1");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>A conditional-only property never becomes the implicit default.</summary>
    [Fact]
    public void UnmaterializedConditionalOnlyCatalogHasNoDefault()
    {
        string root = Directory.CreateTempSubdirectory("catalog-default-missing-").FullName;
        try
        {
            CompositionDeclarationOptions options = UnmaterializedOptions(root, """
                <PropertyGroup>
                  <HexalithTenantsVersion Condition="'$(Consumer)' == 'legacy'">7.0.7</HexalithTenantsVersion>
                </PropertyGroup>
                """);
            Should.Throw<InvalidDataException>(() => options.CatalogVersion("tenants", "probe"))
                .Message.ShouldContain("image version is missing");
            options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe").ShouldBeNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Ordered defaults follow actual SDK evaluation, including guarded and unconditional reassignments.</summary>
    /// <param name="declarations">The synthetic catalog's ordered property groups.</param>
    /// <param name="expected">The property value independently selected by MSBuild.</param>
    /// <returns>The actual SDK and both public default-lookup assertions.</returns>
    [Theory]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion>8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "8.0.1")]
    [InlineData(
        """
        <PropertyGroup><HexalithTenantsVersion>8.0.1</HexalithTenantsVersion></PropertyGroup>
        <PropertyGroup><HexalithTenantsVersion>9.0.0</HexalithTenantsVersion></PropertyGroup>
        """,
        "9.0.0")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion>9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "9.0.0")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "8.0.1")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion></HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "9.0.0")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion>   </HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "9.0.0")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion>8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion></HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "")]
    [InlineData(
        """
        <PropertyGroup>
          <HexalithTenantsVersion>8.0.1</HexalithTenantsVersion>
          <HexalithTenantsVersion></HexalithTenantsVersion>
          <HexalithTenantsVersion Condition="'$(HexalithTenantsVersion)' == ''">9.0.0</HexalithTenantsVersion>
        </PropertyGroup>
        """,
        "9.0.0")]
    public async Task UnmaterializedDefaultsFollowSdkAssignmentOrderAsync(string declarations, string expected)
    {
        string root = Directory.CreateTempSubdirectory("catalog-default-order-").FullName;
        try
        {
            CompositionDeclarationOptions options = UnmaterializedOptions(root, declarations);
            File.Copy(Path.Combine(CompositionTestFiles.RepositoryRoot(), "global.json"), Path.Combine(root, "global.json"));
            string catalog = Path.Combine(root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
            CompositionProcessResult evaluated = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(
                    "dotnet",
                    ["msbuild", catalog, "-nologo", "-noAutoResponse", "-getProperty:HexalithTenantsVersion"],
                    root,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "false", ["MSBUILDUSESERVER"] = "0" }),
                TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            evaluated.Started.ShouldBeTrue();
            evaluated.TimedOut.ShouldBeFalse();
            evaluated.OutputTruncated.ShouldBeFalse();
            evaluated.SurvivingProcessIds.ShouldBeEmpty();
            evaluated.ExitCode.ShouldBe(0, evaluated.Output + evaluated.ErrorOutput);
            evaluated.Output.Trim().ShouldBe(expected);
            if (expected.Length == 0)
            {
                Should.Throw<InvalidDataException>(() => options.CatalogVersion("tenants", "probe"))
                    .Message.ShouldContain("is not literal");
                Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe"))
                    .Message.ShouldContain("is not literal");
            }
            else
            {
                options.CatalogVersion("tenants", "probe").ShouldBe(expected);
                options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe").ShouldBe(expected);
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Unmaterialized default values cannot retain unresolved MSBuild expressions or empty values.</summary>
    /// <param name="value">The nonliteral or empty catalog default.</param>
    [Theory]
    [InlineData("$(UnresolvedVersion)")]
    [InlineData("@(UnresolvedItems)")]
    [InlineData("%(UnresolvedMetadata)")]
    [InlineData("")]
    public void UnmaterializedDefaultRefusesNonliteralValues(string value)
    {
        string root = Directory.CreateTempSubdirectory("catalog-default-nonliteral-").FullName;
        try
        {
            CompositionDeclarationOptions options = UnmaterializedOptions(root, "<PropertyGroup><HexalithTenantsVersion>" + value + "</HexalithTenantsVersion></PropertyGroup>");
            _ = Should.Throw<InvalidDataException>(() => options.CatalogVersion("tenants", "probe"));
            _ = Should.Throw<InvalidDataException>(() => options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Only direct root property groups can define an unmaterialized default.</summary>
    /// <param name="scope">The unexecuted or unselected MSBuild declaration scope.</param>
    /// <param name="hasDefault">Whether a supported root property group supplies the current default.</param>
    [Theory]
    [InlineData("target", false)]
    [InlineData("target", true)]
    [InlineData("otherwise", false)]
    [InlineData("otherwise", true)]
    public void UnmaterializedDefaultRequiresRootPropertyGroups(string scope, bool hasDefault)
    {
        string root = Directory.CreateTempSubdirectory("catalog-default-scope-").FullName;
        try
        {
            const string assignment = "<PropertyGroup><HexalithTenantsVersion>6.4.2</HexalithTenantsVersion></PropertyGroup>";
            string unsupported = scope == "target"
                ? "<Target Name=\"Unexecuted\">" + assignment + "</Target>"
                : "<Choose><When Condition=\"'true' == 'true'\"><PropertyGroup /></When><Otherwise>" + assignment + "</Otherwise></Choose>";
            string current = hasDefault
                ? "<PropertyGroup><HexalithTenantsVersion>9.2.3</HexalithTenantsVersion></PropertyGroup>"
                : string.Empty;
            CompositionDeclarationOptions options = UnmaterializedOptions(root, current + unsupported);
            if (hasDefault)
            {
                options.CatalogVersion("tenants", "probe").ShouldBe("9.2.3");
                options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe").ShouldBe("9.2.3");
            }
            else
            {
                _ = Should.Throw<InvalidDataException>(() => options.CatalogVersion("tenants", "probe"));
                options.CatalogPackageVersion("Hexalith.Tenants.Server", "probe").ShouldBeNull();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    /// <summary>Verifies that the real pinned catalog records the current family for each named consumer.</summary>
    /// <returns>The verified snapshot assertions.</returns>
    [Fact]
    public async Task NamedConsumersUseTheCurrentEventStoreFamilyAsync()
    {
        string catalog = await File.ReadAllTextAsync(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateWithCatalogAsync(catalog).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "current-consumers");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(workspace, "source-mapping", "catalog-versions.json"));
        string expected = Hexalith.Builds.Tooling.Manifest.PlatformVersionCatalog.Current.EventStoreVersion;
        AssertEventStoreFamily(snapshot.Versions, expected, "default");
        foreach (string consumer in new[] { "Hexalith.Timesheets", "Hexalith.Parties", "Hexalith.Platform.Composition", "Hexalith.Builds.Module.EventStoreHost", "Hexalith.FrontComposer.Shell" })
        {
            AssertEventStoreFamily(snapshot.ProjectVersions[consumer], expected, consumer);
            CompositionDeclarationOptions options = new(mapping, []) { Workspace = workspace, ConsumerProjectName = consumer };
            options.CatalogVersion("eventstore", "probe").ShouldBe(expected);
            options.CatalogPackageVersion("Hexalith.EventStore.Gateway", "probe").ShouldBe(expected);
            options.CatalogVersion("tenants", "probe").ShouldBe(snapshot.Versions["Hexalith.Tenants.Contracts"]);
            options.CatalogVersion("memories", "probe").ShouldBe(snapshot.Versions["Hexalith.Memories.Contracts"]);
        }
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

    private static CompositionDeclarationOptions UnmaterializedOptions(string root, string declarations)
    {
        string catalog = Path.Combine(root, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(catalog)!);
        File.WriteAllText(catalog, $"""
            <Project>
              {declarations}
              <ItemGroup>
                <PackageVersion Include="Hexalith.Tenants.Server" Version="$(HexalithTenantsVersion)" />
              </ItemGroup>
            </Project>
            """);
        SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Platform", [], false);
        return new CompositionDeclarationOptions(mapping, []);
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
