// <copyright file="CatalogVersionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

using Shouldly;

using Xunit;

using Task = System.Threading.Tasks.Task;

/// <summary>Verifies package versions against the isolated pinned Builds catalog.</summary>
public sealed class CatalogVersionTests
{
    /// <summary>Verifies the pinned catalog is materialized with the mapping hash.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageMappingWritesPinnedCatalogSnapshotAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");

        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        snapshot.RootElement.GetProperty("MappingHash").GetString().ShouldBe(mapping.ContentHash);
        snapshot.RootElement.GetProperty("CatalogCommit").GetString().ShouldBe(mapping.PlatformIdentity!.CatalogCommit);
        snapshot.RootElement.GetProperty("Versions").GetProperty("Hexalith.Dep").GetString().ShouldBe("1.0.0");
        snapshot.RootElement.GetProperty("Hash").GetString().ShouldNotBeNullOrWhiteSpace();
        (await File.ReadAllTextAsync(SourceMappingMaterializer.TargetsPath(workspace), TestContext.Current.CancellationToken).ConfigureAwait(true))
            .ShouldContain("CatalogVersionValidationTask");
    }

    /// <summary>Verifies source composition materializes the catalog when the identity supplies a pin.</summary>
    /// <param name="hasPin">Whether the source identity has a catalog commit.</param>
    /// <returns>The source-mode materialization assertion.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SourceMappingMaterializesOnlyTheAvailableCatalogPinAsync(bool hasPin)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping mapping = hasPin ? resolved : resolved with { PlatformIdentity = resolved.PlatformIdentity! with { CatalogCommit = null } };
        string workspace = Path.Combine(fixture.Directory, "source-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        File.Exists(snapshotPath).ShouldBe(hasPin);
        File.Exists(Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props")).ShouldBe(hasPin);
        File.Exists(Path.Combine(workspace, "source-mapping", "mapping.json")).ShouldBeTrue();
        if (hasPin)
        {
            using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
            snapshot.RootElement.GetProperty("MappingHash").GetString().ShouldBe(mapping.ContentHash);
            snapshot.RootElement.GetProperty("CatalogCommit").GetString().ShouldBe(mapping.PlatformIdentity!.CatalogCommit);
            snapshot.RootElement.GetProperty("Versions").GetProperty("Hexalith.Dep").GetString().ShouldBe("1.0.0");
        }
    }

    /// <summary>Verifies a local catalog snapshot write failure retains its HXW prerequisite and exact path.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task CatalogSnapshotIoFailureReportsHxwPathAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        _ = Directory.CreateDirectory(snapshotPath);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostic.RuleId.ShouldBe("HXW009");
        refusal.Diagnostic.Field.ShouldBe(snapshotPath);
        refusal.Diagnostic.Message.ShouldContain("writing pinned catalog snapshot");
    }

    /// <summary>Verifies an empty evaluated catalog version is refused before snapshot writing.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task EmptyEvaluatedCatalogVersionFailsWithHxw009Async()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(emptyCatalogVersion: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostic.RuleId.ShouldBe("HXW009");
        refusal.Diagnostic.Message.ShouldContain("Empty package ID or catalog version");
        File.Exists(Path.Combine(workspace, "source-mapping", "catalog-versions.json")).ShouldBeFalse();
    }

    /// <summary>Verifies on-demand isolated evaluation retains HXW009 and its pinned file path.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task OnDemandCatalogFailureRetainsHxw009Async()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(onDemandCatalogFailure: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            ProjectName = "Broken",
            Packages = [new TaskItem("Hexalith.Dep")],
        };

        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW009");
        engine.Error.File.ShouldBe(Path.Combine(directory, "pinned-tree", "Props", "Directory.Packages.props"));
    }

    /// <summary>Verifies a direct version or VersionOverride mismatch fails with HXW012.</summary>
    /// <param name="metadata">The effective version source.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("Version")]
    [InlineData("VersionOverride")]
    public async Task ReferencedCatalogPackageMustUsePinnedVersionAsync(string metadata)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        TaskItem package = new("Hexalith.Dep");
        package.SetMetadata(metadata, "2.0.0");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            Packages = [package],
        };

        task.Execute().ShouldBeFalse();
        BuildErrorEventArgs error = engine.Error.ShouldNotBeNull();
        error.Code.ShouldBe("HXW012");
        error.Message.ShouldNotBeNull().ShouldContain("Hexalith.Dep");
        error.Message.ShouldNotBeNull().ShouldContain("2.0.0");
        error.Message.ShouldNotBeNull().ShouldContain("1.0.0");
    }

    /// <summary>Verifies a restored package that differs from its requested pin is rejected.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task RestoredCatalogPackageMustUsePinnedVersionAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string assets = Path.Combine(workspace, "project.assets.json");
        await File.WriteAllTextAsync(assets, "{\"targets\":{\"net10.0\":{\"Hexalith.Dep/2.0.0\":{\"type\":\"package\"}}}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        TaskItem package = new("Hexalith.Dep");
        package.SetMetadata("Version", "1.0.0");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            AssetsFile = assets,
            CheckAssets = true,
            TargetFramework = "net10.0",
            Packages = [package],
        };

        task.Execute().ShouldBeFalse();
        BuildErrorEventArgs error = engine.Error.ShouldNotBeNull();
        error.Code.ShouldBe("HXW012");
        error.Message.ShouldNotBeNull().ShouldContain("2.0.0");
    }

    /// <summary>Verifies the isolated catalog selects the version for the actual consumer project.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ConditionalCatalogVersionUsesConsumerNameAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "catalog-versions.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        snapshot.RootElement.GetProperty("Versions").GetProperty("CommunityToolkit.Aspire.Hosting.Dapr").GetString().ShouldBe("13.6.0-preview.1");
        snapshot.RootElement.GetProperty("ProjectVersions").GetProperty("Hexalith.Folders.Aspire")
            .GetProperty("CommunityToolkit.Aspire.Hosting.Dapr").GetString().ShouldBe("13.0.0");

        TaskItem package = new("CommunityToolkit.Aspire.Hosting.Dapr");
        package.SetMetadata("Version", "13.0.0");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            ProjectName = "Hexalith.Folders.Aspire",
            Packages = [package],
        };

        task.Execute().ShouldBeTrue();
        task.ProjectName = "Different.Consumer";
        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
    }

    /// <summary>Verifies an equivalent exact NuGet range reaches restore.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task EquivalentExactRangeIsAcceptedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        TaskItem package = new("Hexalith.Dep");
        package.SetMetadata("Version", "[1.0.0]");
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = new CatalogTestBuildEngine(),
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            Packages = [package],
        };

        task.Execute().ShouldBeTrue();
    }

    /// <summary>Verifies a malformed snapshot produces the stable HXW012 rule.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingSnapshotVersionsFailWithHxw012Async()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string catalog = Path.Combine(directory, "catalog-versions.json");
        JsonNode snapshot = JsonNode.Parse(await File.ReadAllTextAsync(catalog, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        snapshot["Versions"] = null;
        await File.WriteAllTextAsync(catalog, snapshot.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = catalog,
            Packages = [new TaskItem("Hexalith.Dep")],
        };

        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
    }

    /// <summary>Verifies a modified pinned tree or snapshot binding is refused.</summary>
    /// <param name="tamper">The binding to break.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("pinned-file")]
    [InlineData("pinned-entry")]
    [InlineData("hash")]
    [InlineData("mapping")]
    [InlineData("commit")]
    public async Task TamperedSnapshotFailsWithHxw012Async(string tamper)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string catalog = Path.Combine(directory, "catalog-versions.json");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = catalog,
            Packages = [new TaskItem("Hexalith.Dep")],
        };
        task.Execute().ShouldBeTrue();

        CatalogVersionSnapshot snapshot = JsonSerializer.Deserialize<CatalogVersionSnapshot>(await File.ReadAllTextAsync(catalog, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        CatalogVersionSnapshot changed = tamper switch
        {
            "pinned-entry" => snapshot with { PinnedFiles = new Dictionary<string, string>(snapshot.PinnedFiles, StringComparer.Ordinal) { ["Props/Extra.props"] = new string('0', 64) } },
            "hash" => snapshot with { Hash = new string('0', 64) },
            "mapping" => snapshot with { MappingHash = new string('a', 64) },
            "commit" => snapshot with { CatalogCommit = new string('b', 40) },
            _ => snapshot,
        };
        if (tamper is "pinned-entry" or "mapping" or "commit")
        {
            changed = changed with
            {
                Hash = CatalogVersionSnapshot.ComputeHash(changed.MappingHash, changed.CatalogCommit, changed.CatalogFileHash, changed.PinnedFiles, changed.Versions, changed.ProjectVersions),
            };
        }

        await File.WriteAllTextAsync(catalog, JsonSerializer.Serialize(changed), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (tamper == "pinned-file")
        {
            await File.AppendAllTextAsync(Path.Combine(directory, "pinned-tree", "Props", "Directory.Packages.props"), "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
        engine.Error.Message.ShouldNotBeNull().ShouldContain("differs from the command-resolved identity");
    }

    /// <summary>Verifies a target framework absent from the assets project section reports the missing active target.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnmatchedTargetFrameworkReportsNoActiveTargetAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string assets = Path.Combine(workspace, "project.assets.json");
        await File.WriteAllTextAsync(assets, "{\"project\":{\"frameworks\":{\"net9.0\":{\"targetAlias\":\"net9.0\"}}},\"targets\":{\"net9.0\":{\"Hexalith.Dep/1.0.0\":{\"type\":\"package\"}}}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            AssetsFile = assets,
            CheckAssets = true,
            TargetFramework = "net10.0",
            Packages = [new TaskItem("Hexalith.Dep")],
        };

        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
        engine.Error.Message.ShouldNotBeNull().ShouldContain("no active target 'net10.0'");
    }

    /// <summary>Verifies an ancestor <c>Directory.Build.rsp</c> cannot change the isolated catalog evaluation.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AncestorResponseFileDoesNotChangeIsolatedCatalogAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Directory, "Directory.Build.rsp"), "-p:DaprVersion=9.9.9", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");

        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        CatalogVersionSnapshot snapshot = JsonSerializer.Deserialize<CatalogVersionSnapshot>(await File.ReadAllTextAsync(Path.Combine(workspace, "source-mapping", "catalog-versions.json"), TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        snapshot.Versions["CommunityToolkit.Aspire.Hosting.Dapr"].ShouldBe("13.6.0-preview.1");
        snapshot.ProjectVersions["Hexalith.Folders.Aspire"]["CommunityToolkit.Aspire.Hosting.Dapr"].ShouldBe("13.0.0");
    }

    /// <summary>Verifies generated and Builds host builds run the package catalog guard.</summary>
    /// <param name="hostName">The generated or source-tree host name.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("Generated")]
    [InlineData("Hexalith.Builds.Module.EventStoreHost")]
    [InlineData("Hexalith.Builds.Module.UiHost")]
    public async Task HostBuildRejectsVersionOverrideAsync(string hostName)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string host = hostName != "Generated"
            ? Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", "src", "hosts", hostName)
            : Path.Combine(workspace, "hosts", hostName);
        _ = Directory.CreateDirectory(host);
        string project = Path.Combine(host, hostName + ".csproj");
        await File.WriteAllTextAsync(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep\" VersionOverride=\"2.0.0\" /></ItemGroup></Project>",
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-nologo", "-target:Build"], host, SourceMappingMaterializer.Environment(mapping, workspace)),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        build.ExitCode.ShouldNotBe(0);
        (build.Output + build.ErrorOutput).ShouldContain("HXW012");
        (build.Output + build.ErrorOutput).ShouldContain("Hexalith.Dep");
    }

    /// <summary>Verifies only the active target framework is compared with its pinned version.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AssetsUseOnlyActiveTargetFrameworkAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string assets = Path.Combine(workspace, "project.assets.json");
        await File.WriteAllTextAsync(assets, "{\"targets\":{\"net9.0\":{\"Hexalith.Dep/2.0.0\":{\"type\":\"package\"}},\"net10.0\":{\"Hexalith.Dep/1.0.0\":{\"type\":\"package\"}}}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        TaskItem package = new("Hexalith.Dep");
        package.SetMetadata("Version", "[1.0.0,3.0.0)");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            AssetsFile = assets,
            CheckAssets = true,
            TargetFramework = "net10.0",
            Packages = [package],
        };

        task.Execute().ShouldBeTrue();
        task.TargetFramework = "net9.0";
        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
    }

    /// <summary>Verifies NuGet's framework alias selects the active target and requires every direct catalog package.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NormalizedTargetRequiresReferencedPackageAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        string assets = Path.Combine(workspace, "project.assets.json");
        await File.WriteAllTextAsync(assets, "{\"project\":{\"frameworks\":{\".NETFramework,Version=v4.8\":{\"targetAlias\":\"net48\"}}},\"targets\":{\".NETFramework,Version=v4.8\":{\"Hexalith.Dep/1.0.0\":{\"type\":\"package\"}}}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            AssetsFile = assets,
            CheckAssets = true,
            TargetFramework = "net48",
            Packages = [new TaskItem("Hexalith.Dep")],
        };

        task.Execute().ShouldBeTrue();
        await File.WriteAllTextAsync(assets, "{\"project\":{\"frameworks\":{\".NETFramework,Version=v4.8\":{\"targetAlias\":\"net48\"}}},\"targets\":{\".NETFramework,Version=v4.8\":{}}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
        engine.Error.Message.ShouldNotBeNull().ShouldContain("absent");
    }

    /// <summary>Verifies a compound project condition in a relative pinned import controls generated consumer builds.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task RelativeImportedCompoundConditionControlsGeneratedBuildAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(relativeCatalogImport: true, compoundCatalogCondition: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        snapshot.RootElement.GetProperty("PinnedFiles").GetProperty("Props/Imported.Packages.props").GetString().ShouldNotBeNullOrWhiteSpace();
        snapshot.RootElement.GetProperty("ProjectVersions").GetProperty("Hexalith.Folders.Aspire")
            .GetProperty("CommunityToolkit.Aspire.Hosting.Dapr").GetString().ShouldBe("13.0.0");

        string host = Path.Combine(workspace, "hosts", "Hexalith.Folders.Aspire");
        _ = Directory.CreateDirectory(host);
        string project = Path.Combine(host, "Hexalith.Folders.Aspire.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" VersionOverride=\"13.6.0-preview.1\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-nologo", "-target:Build"], host, SourceMappingMaterializer.Environment(mapping, workspace)),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        build.ExitCode.ShouldNotBe(0);
        (build.Output + build.ErrorOutput).ShouldContain("HXW012");
        (build.Output + build.ErrorOutput).ShouldContain("13.0.0");
    }

    /// <summary>Verifies an absent conditional import stays absent and does not block the pinned catalog.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AbsentConditionalImportDoesNotBlockCatalogAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(optionalCatalogImport: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");

        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "source-mapping", "catalog-versions.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        snapshot.RootElement.GetProperty("Versions").GetProperty("Hexalith.Dep").GetString().ShouldBe("1.0.0");
        snapshot.RootElement.GetProperty("PinnedFiles").TryGetProperty("Props/Absent.props", out _).ShouldBeFalse();
    }

    /// <summary>Verifies a valid project-name function is evaluated for the actual consumer and recorded in the hashed snapshot.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ProjectNameFunctionUsesOnDemandIsolatedEvaluationAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(functionCatalogCondition: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        using (JsonDocument before = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true)))
        {
            before.RootElement.GetProperty("ProjectVersions").TryGetProperty("Hexalith.Folders.Aspire", out _).ShouldBeFalse();
        }

        string host = Path.Combine(workspace, "hosts", "Hexalith.Folders.Aspire");
        _ = Directory.CreateDirectory(host);
        string project = Path.Combine(host, "Hexalith.Folders.Aspire.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" VersionOverride=\"13.6.0-preview.1\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-nologo", "-target:Build"], host, SourceMappingMaterializer.Environment(mapping, workspace)),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        build.ExitCode.ShouldNotBe(0);
        (build.Output + build.ErrorOutput).ShouldContain("HXW012");
        (build.Output + build.ErrorOutput).ShouldContain("13.0.0");
        using JsonDocument after = JsonDocument.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        after.RootElement.GetProperty("MappingHash").GetString().ShouldBe(mapping.ContentHash);
        after.RootElement.GetProperty("ProjectVersions").GetProperty("Hexalith.Folders.Aspire")
            .GetProperty("CommunityToolkit.Aspire.Hosting.Dapr").GetString().ShouldBe("13.0.0");
    }

    /// <summary>Verifies a required post-restore comparison fails when its assets are missing.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingAssetsFailWithHxw012Async()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(workspace, "source-mapping");
        CatalogTestBuildEngine engine = new();
        CatalogVersionValidationTask task = new()
        {
            BuildEngine = engine,
            MappingFile = Path.Combine(directory, "mapping.json"),
            MappingHash = mapping.ContentHash,
            CatalogFile = Path.Combine(directory, "catalog-versions.json"),
            AssetsFile = Path.Combine(workspace, "missing.assets.json"),
            CheckAssets = true,
            TargetFramework = "net10.0",
            Packages = [new TaskItem("Hexalith.Dep")],
        };

        task.Execute().ShouldBeFalse();
        engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
    }

    /// <summary>Verifies the catalog capture handles a realistic number of pinned packages.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task LargeCatalogMaterializesWithoutTruncationAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(largeCatalog: true).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");

        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "source-mapping", "catalog-versions.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        snapshot.RootElement.GetProperty("Versions").EnumerateObject().Count().ShouldBeGreaterThan(500);
    }

    /// <summary>Verifies a global reference is checked by the generated build guard.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task GlobalPackageReferenceBuildRejectsMismatchAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string host = Path.Combine(workspace, "hosts", "Generated");
        _ = Directory.CreateDirectory(host);
        string project = Path.Combine(host, "Generated.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(host, "Directory.Packages.props"), "<Project><ItemGroup><GlobalPackageReference Include=\"Hexalith.Dep\" Version=\"2.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);

        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-nologo", "-target:Build"], host, SourceMappingMaterializer.Environment(mapping, workspace)),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        build.ExitCode.ShouldNotBe(0);
        (build.Output + build.ErrorOutput).ShouldContain("HXW012");
        (build.Output + build.ErrorOutput).ShouldContain("Hexalith.Dep");
    }

    /// <summary>Verifies a locally restored range is checked by the generated post-restore target.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task LocalFeedRestoreRejectsResolvedRangeMismatchAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string feed = Path.Combine(fixture.Directory, "feed");
        string packageDirectory = Path.Combine(fixture.Directory, "package");
        _ = Directory.CreateDirectory(feed);
        _ = Directory.CreateDirectory(packageDirectory);
        string packageProject = Path.Combine(packageDirectory, "Hexalith.Dep.csproj");
        await File.WriteAllTextAsync(packageProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Dep</PackageId><Version>2.0.0</Version></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "Class1.cs"), "public class Class1 {}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult pack = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["pack", packageProject, "-o", feed, "-v:q", "-p:NuGetAudit=false", "-p:RestoreSources=" + feed], packageDirectory, null),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        pack.ExitCode.ShouldBe(0, pack.Output + pack.ErrorOutput);

        string host = Path.Combine(workspace, "hosts", "Generated");
        _ = Directory.CreateDirectory(host);
        await File.WriteAllTextAsync(Path.Combine(host, "NuGet.Config"), "<configuration><packageSources><clear /><add key=\"local\" value=\"" + feed + "\" /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string project = Path.Combine(host, "Generated.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep\" Version=\"[1.0.0,3.0.0)\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Dictionary<string, string> environment = new(SourceMappingMaterializer.Environment(mapping, workspace), StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = Path.Combine(fixture.Directory, "isolated-packages"),
        };
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "-v:q", "-p:NuGetAudit=false"], host, environment),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(Path.Combine(host, "obj", "project.assets.json")).ShouldBeTrue(build.Output + build.ErrorOutput);
        build.ExitCode.ShouldNotBe(0);
        (build.Output + build.ErrorOutput).ShouldContain("HXW012");
        (build.Output + build.ErrorOutput).ShouldContain("2.0.0");
    }

    /// <summary>Verifies a generated build accepts a locally restored package matching its consumer-specific pin.</summary>
    /// <param name="conditional">Whether the pinned version depends on the project name.</param>
    /// <param name="central">Whether the reference has no version and uses central package management.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task LocalFeedRestoreAcceptsMatchingPinnedVersionAsync(bool conditional, bool central)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(conditionalCatalog: conditional).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string packageId = conditional ? "CommunityToolkit.Aspire.Hosting.Dapr" : "Hexalith.Dep";
        string version = conditional ? "13.0.0" : "1.0.0";
        string projectName = conditional ? "Hexalith.Folders.Aspire" : "Generated";
        string feed = Path.Combine(fixture.Directory, "feed");
        string packageDirectory = Path.Combine(fixture.Directory, "package");
        _ = Directory.CreateDirectory(feed);
        _ = Directory.CreateDirectory(packageDirectory);
        string packageProject = Path.Combine(packageDirectory, "Package.csproj");
        await File.WriteAllTextAsync(packageProject, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>{packageId}</PackageId><Version>{version}</Version></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(packageDirectory, "Class1.cs"), "public class Class1 {}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult pack = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["pack", packageProject, "-o", feed, "-v:q", "-p:NuGetAudit=false", "-p:RestoreSources=" + feed], packageDirectory, null),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        pack.ExitCode.ShouldBe(0, pack.Output + pack.ErrorOutput);

        string host = Path.Combine(workspace, "hosts", projectName);
        _ = Directory.CreateDirectory(host);
        await File.WriteAllTextAsync(Path.Combine(host, "NuGet.Config"), "<configuration><packageSources><clear /><add key=\"local\" value=\"" + feed + "\" /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (central)
        {
            await File.WriteAllTextAsync(Path.Combine(host, "Directory.Packages.props"), $"<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"{packageId}\" Version=\"{version}\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string project = Path.Combine(host, projectName + ".csproj");
        string reference = central ? $"<PackageReference Include=\"{packageId}\" />" : $"<PackageReference Include=\"{packageId}\" Version=\"[{version}]\" />";
        await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>{reference}</ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Dictionary<string, string> environment = new(SourceMappingMaterializer.Environment(mapping, workspace), StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = Path.Combine(fixture.Directory, "isolated-packages"),
        };
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "-v:q", "-p:NuGetAudit=false"], host, environment),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        build.ExitCode.ShouldBe(0, build.Output + build.ErrorOutput);
        File.Exists(Path.Combine(host, "obj", "project.assets.json")).ShouldBeTrue();
    }
}
