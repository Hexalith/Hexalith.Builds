// <copyright file="PlatformModelCatalogTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Runtime.Loader;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Exercises the model builder against early catalog imports and immutable pin authority.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class PlatformModelCatalogTests
{
    /// <summary>Builds a real source-only contract through the exact mapped dependency while retaining pinned package authority.</summary>
    /// <returns>The actual SDK build and restored dependency assertions.</returns>
    [Fact]
    public async Task ModelBuildCompilesActualMappedSourceDependencyAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "source-model-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string dependency = Path.Combine(mapping.Entries.Single(entry => entry.Identity == "Hexalith.Dep").Path!, "src", "Hexalith.Dep");
        _ = Directory.CreateDirectory(dependency);
        await File.WriteAllTextAsync(Path.Combine(dependency, "Hexalith.Dep.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Dep</PackageId><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(dependency, "SourceOnlyContract.cs"), "namespace MappedOwner; public sealed class SourceOnlyContract { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string root = mapping.PlatformIdentity!.PlatformPath;
        string model = Path.Combine(root, "src", "Hexalith.Platform.Composition");
        _ = Directory.CreateDirectory(model);
        await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        const string modelProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup>"
            + "<ProjectReference Include=\"$(HexalithDepRoot)/src/Hexalith.Dep/Hexalith.Dep.csproj\" Condition=\"'$(HexalithDepFromSource)' == 'true'\" />"
            + "<PackageReference Include=\"Hexalith.Dep\" Condition=\"'$(HexalithDepFromSource)' != 'true'\" /></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(model, "Hexalith.Platform.Composition.csproj"), modelProject, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(model, "Consumer.cs"), "public sealed class Consumer { public MappedOwner.SourceOnlyContract Value { get; } = new(); }", TestContext.Current.CancellationToken).ConfigureAwait(true);

        string assembly = await PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(assembly).ShouldBeTrue();
        string assetsPath = Directory.GetFiles(Path.Combine(workspace, "build", "platform-model", "obj", "Hexalith.Platform.Composition"), "project.assets.json", SearchOption.AllDirectories).ShouldHaveSingleItem();
        using System.Text.Json.JsonDocument assets = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        assets.RootElement.GetProperty("libraries").GetProperty("Hexalith.Dep/1.0.0").GetProperty("type").GetString().ShouldBe("project");
    }

    /// <summary>Builds an actual SDK project graph with early properties and conditional pinned versions.</summary>
    /// <param name="mode">The selected source or package build mode.</param>
    /// <param name="changeLiveCatalog">Whether the live catalog disagrees with the committed pin.</param>
    /// <returns>The bounded offline build assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, false)]
    [InlineData(WorkspaceMode.Source, true)]
    [InlineData(WorkspaceMode.Package, false)]
    [InlineData(WorkspaceMode.Package, true)]
    public async Task ModelBuildAppliesPinnedCatalogOnceBeforeOwnerPropsAsync(WorkspaceMode mode, bool changeLiveCatalog)
    {
        // The pin probe is intentionally a package in both configurations; source binding is tested separately above.
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true, conditionalPackageId: "Local.CatalogProbe").ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "model-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string root = mapping.PlatformIdentity!.PlatformPath;
        string liveCatalog = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", "Props", "Directory.Packages.props");
        if (changeLiveCatalog)
        {
            string live = await File.ReadAllTextAsync(liveCatalog, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(liveCatalog, live.Replace("13.6.0-preview.1", "99.0.0", StringComparison.Ordinal).Replace("13.0.0", "99.0.0", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string feed = Path.Combine(fixture.Directory, "feed");
        string packageDirectory = Path.Combine(fixture.Directory, "package");
        string packageCache = Path.Combine(fixture.Directory, "isolated-packages");
        _ = Directory.CreateDirectory(feed);
        _ = Directory.CreateDirectory(packageDirectory);
        string packageProject = Path.Combine(packageDirectory, "Package.csproj");
        await File.WriteAllTextAsync(packageProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Local.CatalogProbe</PackageId><Version>1.0.0</Version></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "RuntimeValue.cs"), "public static class OfflineCatalogRuntime { public static string Value => \"catalog-runtime\"; }", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult pack = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["pack", packageProject, "-c", "Debug", "-o", feed, "-v:q", "-p:NuGetAudit=false", "-p:RestoreSources=" + feed], packageDirectory, null),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        pack.ExitCode.ShouldBe(0, pack.Output + pack.ErrorOutput);
        string nugetConfig = "<configuration><packageSources><clear /><add key=\"local\" value=\"" + System.Security.SecurityElement.Escape(feed)
            + "\" /></packageSources><config><add key=\"globalPackagesFolder\" value=\"" + System.Security.SecurityElement.Escape(packageCache) + "\" /></config></configuration>";
        await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), nugetConfig, TestContext.Current.CancellationToken).ConfigureAwait(true);

        // Like the real owner early props, this import also provides properties needed before SDK package evaluation.
        string earlyProps = "<Project><Import Project=\"" + System.Security.SecurityElement.Escape(liveCatalog)
            + "\" Condition=\"'$(DaprVersion)' == ''\" /><PropertyGroup><EarlyCatalogVersion>$(DaprVersion)</EarlyCatalogVersion><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.props"), earlyProps, TestContext.Current.CancellationToken).ConfigureAwait(true);
        const string targets = "<Project><Target Name=\"ObserveCatalog\" BeforeTargets=\"CollectPackageReferences;CoreCompile\">"
            + "<Error Condition=\"'@(PackageVersion->Count())' != '2'\" Text=\"Duplicate or missing catalog identities\" />"
            + "<Error Condition=\"'$(EarlyCatalogVersion)' == ''\" Text=\"Early catalog properties missing\" />"
            + "<Error Condition=\"'$(_CentralPackageVersionsEnabled)' != 'true'\" Text=\"Central package resolution disabled\" />"
            + "<WriteLinesToFile File=\"$(MSBuildProjectDirectory)/observed-catalog.txt\" Lines=\"$(Configuration);$(EarlyCatalogVersion);@(PackageVersion->'%(Identity)=%(Version)')\" Overwrite=\"true\" />"
            + "</Target></Project>";
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Build.targets"), targets, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string modelDirectory = Path.Combine(root, "src", "Hexalith.Platform.Composition");
        string dependencyDirectory = Path.Combine(root, "early");
        _ = Directory.CreateDirectory(modelDirectory);
        _ = Directory.CreateDirectory(dependencyDirectory);
        const string model = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup>"
            + "<ItemGroup><ProjectReference Include=\"../../early/Hexalith.Folders.Aspire.csproj\" /><PackageReference Include=\"Local.CatalogProbe\" /></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "Hexalith.Platform.Composition.csproj"), model, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "RuntimeProbe.cs"), "public static class PinnedRuntimeProbe { public static string Read() => OfflineCatalogRuntime.Value; }", TestContext.Current.CancellationToken).ConfigureAwait(true);
        const string dependency = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageReference Include=\"Local.CatalogProbe\" /></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(dependencyDirectory, "Hexalith.Folders.Aspire.csproj"), dependency, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string assembly = await PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(assembly).ShouldBeTrue();
        string intermediate = Path.Combine(workspace, "build", "platform-model", "obj");
        foreach ((string directory, string projectName, string version) in new[] { (modelDirectory, "Hexalith.Platform.Composition", "13.6.0-preview.1"), (dependencyDirectory, "Hexalith.Folders.Aspire", "13.0.0") })
        {
            string[] observed = await File.ReadAllLinesAsync(Path.Combine(directory, "observed-catalog.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observed.ShouldBe([mode == WorkspaceMode.Source ? "Debug" : "Release", version, "Local.CatalogProbe=1.0.0", "CommunityToolkit.Aspire.Hosting.Dapr=" + version]);
            string marker = Path.DirectorySeparatorChar + projectName + Path.DirectorySeparatorChar;
            string assetsPath = Directory.GetFiles(intermediate, "project.assets.json", SearchOption.AllDirectories).Single(path => path.Contains(marker, StringComparison.Ordinal));
            using System.Text.Json.JsonDocument assets = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            assets.RootElement.GetProperty("libraries").TryGetProperty("Local.CatalogProbe/1.0.0", out _).ShouldBeTrue();
            assets.RootElement.GetProperty("project").GetProperty("restore").GetProperty("centralPackageVersionsManagementEnabled").GetBoolean().ShouldBeTrue();
            assets.RootElement.GetProperty("project").GetProperty("frameworks").GetProperty("net10.0").GetProperty("dependencies").GetProperty("Local.CatalogProbe").GetProperty("versionCentrallyManaged").GetBoolean().ShouldBeTrue();
            assets.RootElement.GetProperty("project").GetProperty("restore").GetProperty("packagesPath").GetString()!.TrimEnd(Path.DirectorySeparatorChar).ShouldBe(packageCache);
        }

        ReadRuntimeProbe(assembly).ShouldBe("catalog-runtime");
    }

    /// <summary>Refuses unavailable or changed pinned authority before a model process can build.</summary>
    /// <param name="mode">The selected source or package mode.</param>
    /// <param name="drift">The missing or altered authority.</param>
    /// <returns>The refusal assertion.</returns>
    /// <exception cref="InvalidOperationException">The fixture requests an unknown drift.</exception>
    [Theory]
    [InlineData(WorkspaceMode.Source, "catalog")]
    [InlineData(WorkspaceMode.Source, "snapshot")]
    [InlineData(WorkspaceMode.Source, "bytes")]
    [InlineData(WorkspaceMode.Source, "mapping")]
    [InlineData(WorkspaceMode.Source, "pin")]
    [InlineData(WorkspaceMode.Source, "mode")]
    [InlineData(WorkspaceMode.Source, "path")]
    [InlineData(WorkspaceMode.Package, "catalog")]
    [InlineData(WorkspaceMode.Package, "snapshot")]
    [InlineData(WorkspaceMode.Package, "bytes")]
    [InlineData(WorkspaceMode.Package, "mapping")]
    [InlineData(WorkspaceMode.Package, "pin")]
    [InlineData(WorkspaceMode.Package, "mode")]
    [InlineData(WorkspaceMode.Package, "path")]
    public async Task ModelBuildRefusesMissingOrAlteredCatalogAuthorityAsync(WorkspaceMode mode, string drift)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "refused-model");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string modelDirectory = Path.Combine(mapping.PlatformIdentity!.PlatformPath, "src", "Hexalith.Platform.Composition");
        _ = Directory.CreateDirectory(modelDirectory);
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "Hexalith.Platform.Composition.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string pinnedCatalog = Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props");
        switch (drift)
        {
            case "catalog": File.Delete(pinnedCatalog); break;
            case "snapshot": File.Delete(Path.Combine(workspace, "source-mapping", "catalog-versions.json")); break;
            case "bytes": await File.AppendAllTextAsync(pinnedCatalog, "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true); break;
            case "mapping": mapping = mapping with { ActiveModule = "foreign" }; break;
            case "pin": mapping = mapping with { PlatformIdentity = mapping.PlatformIdentity with { CatalogCommit = new string('a', 40) } }; break;
            case "path":
                string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
                System.Text.Json.Nodes.JsonNode snapshot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
                snapshot["PinnedFiles"]!["invalid\0path"] = "malformed";
                await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
                break;
            case "mode": mapping = mapping with { Mode = mode == WorkspaceMode.Source ? WorkspaceMode.Package : WorkspaceMode.Source }; break;
            default: throw new InvalidOperationException("Unknown authority drift.");
        }

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Message.ShouldContain(drift == "catalog" ? "was not materialized" : "cannot use its pinned catalog");
        Directory.Exists(Path.Combine(workspace, "hosts")).ShouldBeFalse();
    }

    /// <summary>Refuses missing or modified production early imports before any model process.</summary>
    /// <param name="mode">The mapped source or package mode.</param>
    /// <param name="remove">Whether to remove rather than modify the import.</param>
    /// <returns>The fail-closed import assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, false)]
    [InlineData(WorkspaceMode.Source, true)]
    [InlineData(WorkspaceMode.Package, false)]
    [InlineData(WorkspaceMode.Package, true)]
    public async Task ModelBuildRefusesChangedProductionCommonPropsAsync(WorkspaceMode mode, bool remove)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "model-import-refusal");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string modelDirectory = Path.Combine(mapping.PlatformIdentity!.PlatformPath, "src", "Hexalith.Platform.Composition");
        _ = Directory.CreateDirectory(modelDirectory);
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "Hexalith.Platform.Composition.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string import = SourceMappingMaterializer.ModelCommonPropsPath(workspace);
        if (remove)
        {
            File.Delete(import);
        }
        else
        {
            await File.AppendAllTextAsync(import, "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        InvalidDataException refusal = await Should.ThrowAsync<InvalidDataException>(() => PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Message.ShouldContain("production model common-props import");
        Directory.Exists(Path.Combine(workspace, "hosts")).ShouldBeFalse();
    }

    /// <summary>Refuses changed imported files and newly present optional imports before any model process.</summary>
    /// <param name="mode">The selected source or package mode.</param>
    /// <param name="drift">The imported file mutation.</param>
    /// <returns>The controlled refusal assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "optional")]
    [InlineData(WorkspaceMode.Package, "optional")]
    [InlineData(WorkspaceMode.Source, "changed")]
    [InlineData(WorkspaceMode.Package, "changed")]
    [InlineData(WorkspaceMode.Source, "deleted")]
    [InlineData(WorkspaceMode.Package, "deleted")]
    public async Task ModelBuildRefusesImportedCatalogClosureDriftAsync(WorkspaceMode mode, string drift)
    {
        bool optional = drift == "optional";
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(relativeCatalogImport: !optional, optionalCatalogImport: optional).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "import-drift");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, snapshotPath);
        string relativePath = optional ? "Props/Absent.props" : "Props/Imported.Packages.props";
        snapshot.PinnedFiles.ContainsKey(relativePath).ShouldBe(!optional);
        string imported = Path.Combine(workspace, "source-mapping", "pinned-tree", relativePath);
        if (optional)
        {
            File.Exists(imported).ShouldBeFalse();
            await File.WriteAllTextAsync(imported, "<Project><ItemGroup><PackageVersion Include=\"Unrecorded.Package\" Version=\"9.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else if (drift == "changed")
        {
            await File.AppendAllTextAsync(imported, "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            File.Delete(imported);
        }

        string modelDirectory = Path.Combine(mapping.PlatformIdentity!.PlatformPath, "src", "Hexalith.Platform.Composition");
        _ = Directory.CreateDirectory(modelDirectory);
        await File.WriteAllTextAsync(Path.Combine(modelDirectory, "Hexalith.Platform.Composition.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Message.ShouldContain("cannot use its pinned catalog");
        Directory.Exists(Path.Combine(workspace, "hosts")).ShouldBeFalse();
    }

    private static object? ReadRuntimeProbe(string assembly)
    {
        AssemblyLoadContext context = new("model-runtime-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        context.Resolving += (loader, name) =>
        {
            string dependency = Path.Combine(Path.GetDirectoryName(assembly)!, name.Name + ".dll");
            return File.Exists(dependency) ? loader.LoadFromAssemblyPath(dependency) : null;
        };
        try
        {
            return context.LoadFromAssemblyPath(assembly).GetType("PinnedRuntimeProbe", throwOnError: true)!
                .GetMethod("Read")!.Invoke(null, null);
        }
        finally
        {
            context.Unload();
        }
    }
}
