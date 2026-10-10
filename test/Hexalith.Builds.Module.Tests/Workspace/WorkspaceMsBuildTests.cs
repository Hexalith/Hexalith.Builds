// <copyright file="WorkspaceMsBuildTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies generated MSBuild imports override probes and reject mixed origins.</summary>
public sealed class WorkspaceMsBuildTests
{
    /// <summary>Verifies ordinary restore and build select the source project or central package.</summary>
    /// <param name="mode">The selected tool mode.</param>
    /// <param name="expected">The expected dependency origin.</param>
    /// <param name="startsWithProject">Whether the consumer starts with a project reference.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "source", false)]
    [InlineData(WorkspaceMode.Package, "package", false)]
    [InlineData(WorkspaceMode.Source, "source", true)]
    [InlineData(WorkspaceMode.Package, "package", true)]
    public async Task NormalBuildSelectsMappedOriginAsync(WorkspaceMode mode, string expected, bool startsWithProject)
    {
        string root = NewDirectory();
        try
        {
            string version = "1.0.0-workspace-" + Guid.NewGuid().ToString("N");
            string feed = Path.Combine(root, "feed");
            string packaged = Path.Combine(root, "packaged");
            _ = Directory.CreateDirectory(feed);
            _ = Directory.CreateDirectory(packaged);
            string packageProject = WriteProject(packaged, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Dep.Contracts</PackageId><Version>" + version + "</Version></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(packaged, "Origin.cs"), "namespace Hexalith.Dep.Contracts; public static class Origin { public const string Value = \"package\"; }", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult pack = await RunPlainDotnetAsync(root, "pack", packageProject, "-o", feed, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);
            pack.ExitCode.ShouldBe(0, pack.Output);

            string active = Path.Combine(root, "active");
            string source = Path.Combine(active, "references", "Hexalith.Dep", "src", "Contracts");
            _ = Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /><add key=\"local\" value=\"" + feed + "\" /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(active, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep.Contracts\" Version=\"" + version + "\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(source, "Identity.props"), "<Project><PropertyGroup><PackageId Condition=\"'$(Configuration)' == 'Debug' or '$(Configuration)' == 'Release'\">Hexalith.Dep.Contracts</PackageId></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(source, "Odd.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><Import Project=\"Identity.props\" /><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(source, "Origin.cs"), "namespace Hexalith.Dep.Contracts; public static class Origin { public const string Value = \"source\"; }", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string archived = Path.Combine(active, "references", "Hexalith.Dep", "_bmad-output", "evidence");
            _ = Directory.CreateDirectory(archived);
            await File.WriteAllTextAsync(Path.Combine(archived, "Probe.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string consumerDirectory = Path.Combine(active, "src", "Consumer");
            _ = Directory.CreateDirectory(consumerDirectory);
            string dependencyItem = startsWithProject
                ? "<ProjectReference Include=\"../../references/Hexalith.Dep/src/Contracts/Odd.csproj\" />"
                : "<PackageReference Include=\"Hexalith.Dep.Contracts\" />";
            string consumer = WriteProject(consumerDirectory, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType></PropertyGroup><ItemGroup>" + dependencyItem + "</ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(consumerDirectory, "Program.cs"), "System.Console.Write(Hexalith.Dep.Contracts.Origin.Value);", TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = Mapping(active, mode);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult run = await RunDotnetAsync(active, mapping, workspace, "run", "--project", consumer, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            run.ExitCode.ShouldBe(0, run.Output);
            run.Output.ShouldContain(expected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an uninitialized package root uses the single matching catalog identity.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UninitializedPackageRootUsesCatalogIdentityAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            await File.WriteAllTextAsync(Path.Combine(active, "Directory.Packages.props"), "<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep.Special\" Version=\"1.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Dep/src/Odd.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Items").GetProperty("ProjectReference").GetArrayLength().ShouldBe(0);
            output.RootElement.GetProperty("Items").GetProperty("PackageReference")[0].GetProperty("Identity").GetString().ShouldBe("Hexalith.Dep.Special");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies separate missing projects cannot collapse to one catalog package.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MultipleAbsentPackageProjectsAreAmbiguousAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            await File.WriteAllTextAsync(Path.Combine(active, "Directory.Packages.props"), "<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep.Special\" Version=\"1.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Dep/src/One.csproj\" /><ProjectReference Include=\"references/Hexalith.Dep/src/Two.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("multiple absent project paths");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies build tasks reject mapping bytes changed after the command selected its hash.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ChangedMappingFileFailsBeforeItemSelectionAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string mappingFile = Path.Combine(workspace, "source-mapping", "mapping.json");
            string contents = await File.ReadAllTextAsync(mappingFile, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(mappingFile, contents.Replace("Hexalith.Dep", "Hexalith.Other", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("command-resolved hash");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies the real root-declared EventStore tree selects its production package project.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task RootDeclaredEventStoreCandidatesExcludeArchivedEvidenceAsync()
    {
        string platform = Path.GetFullPath(Path.Combine(CompositionTestFiles.RepositoryRoot(), "..", ".."));
        string eventStore = Path.Combine(platform, "references", "Hexalith.EventStore");
        string declarations = Path.Combine(platform, ".gitmodules");
        if (!File.Exists(declarations) || !Directory.Exists(eventStore))
        {
            Assert.Skip("This repository is not inside the Platform root with its EventStore checkout.");
        }

        (await File.ReadAllTextAsync(declarations, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldContain("path = references/Hexalith.EventStore");
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.EventStore.Contracts\" /></ItemGroup></Project>");
            SourceMapping mapping = new(WorkspaceMode.Source, active, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", active), new SourceMappingEntry("Hexalith.EventStore", "source", eventStore)], true);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Items").GetProperty("PackageReference").GetArrayLength().ShouldBe(0);
            string selected = output.RootElement.GetProperty("Items").GetProperty("ProjectReference")[0].GetProperty("FullPath").GetString()!;
            selected.ShouldBe(Path.Combine(eventStore, "src", "Hexalith.EventStore.Contracts", "Hexalith.EventStore.Contracts.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies the root-declared Commons library layout selects its real source package project.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task RootDeclaredCommonsLibraryCandidateSelectsSourceAsync()
    {
        string platform = Path.GetFullPath(Path.Combine(CompositionTestFiles.RepositoryRoot(), "..", ".."));
        string commons = Path.Combine(platform, "references", "Hexalith.Commons");
        string declarations = Path.Combine(platform, ".gitmodules");
        if (!File.Exists(declarations) || !Directory.Exists(commons))
        {
            Assert.Skip("This repository is not inside the Platform root with its Commons checkout.");
        }

        (await File.ReadAllTextAsync(declarations, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldContain("path = references/Hexalith.Commons");
        string expected = Path.Combine(commons, "src", "libraries", "Hexalith.Commons.Http", "Hexalith.Commons.Http.csproj");
        File.Exists(expected).ShouldBeTrue();
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Commons.Http\" /></ItemGroup></Project>");
            SourceMapping mapping = new(WorkspaceMode.Source, active, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", active), new SourceMappingEntry("Hexalith.Commons", "source", commons)], true);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Items").GetProperty("PackageReference").GetArrayLength().ShouldBe(0);
            output.RootElement.GetProperty("Items").GetProperty("ProjectReference")[0].GetProperty("FullPath").GetString().ShouldBe(expected);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies unsupported item metadata is diagnosed before it can change dependency behavior.</summary>
    /// <param name="metadata">The metadata field.</param>
    /// <param name="sourceMode">Whether a package item becomes a project item.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("SetConfiguration", false)]
    [InlineData("SetPlatform", false)]
    [InlineData("Version", false)]
    [InlineData("VersionOverride", false)]
    [InlineData("SetConfiguration", true)]
    [InlineData("SetPlatform", true)]
    [InlineData("ExcludeAssets", true)]
    [InlineData("IncludeAssets", true)]
    public async Task UnsupportedConversionMetadataHasDiagnosticAsync(string metadata, bool sourceMode)
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string source = Path.Combine(active, "references", "Hexalith.Dep", "src", "Contracts");
            _ = Directory.CreateDirectory(source);
            string dependency = Path.Combine(source, "Hexalith.Dep.Contracts.csproj");
            await File.WriteAllTextAsync(dependency, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string item = sourceMode
                ? "<PackageReference Include=\"Hexalith.Dep.Contracts\" " + metadata + "=\"none\" />"
                : "<ProjectReference Include=\"references/Hexalith.Dep/src/Contracts/Hexalith.Dep.Contracts.csproj\" " + metadata + "=\"none\" />";
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup>" + item + "</ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, sourceMode ? WorkspaceMode.Source : WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain(metadata);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies retained project items cannot override the selected build configuration.</summary>
    /// <param name="metadata">The project metadata that would override mapped build settings.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("SetConfiguration")]
    [InlineData("SetPlatform")]
    [InlineData("AdditionalProperties")]
    public async Task RetainedProjectConfigurationMetadataHasDiagnosticAsync(string metadata)
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string dependency = Path.Combine(active, "Dep.csproj");
            await File.WriteAllTextAsync(dependency, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"Dep.csproj\" " + metadata + "=\"Release\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain(metadata);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies explicit project-reference resolution rejects an unmapped child before evaluating it.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ExplicitResolveProjectReferencesFailsBeforeChildEvaluationAsync()
    {
        string root = NewDirectory();
        try
        {
            string outside = Path.Combine(root, "outside");
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(outside);
            _ = Directory.CreateDirectory(active);
            string marker = Path.Combine(outside, "evaluated.txt");
            string external = WriteProject(outside, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><ChildEvaluationMarker>" + marker + "</ChildEvaluationMarker></PropertyGroup><Target Name=\"WriteMarker\" BeforeTargets=\"GetTargetPath\"><WriteLinesToFile File=\"$(ChildEvaluationMarker)\" Lines=\"evaluated\" /></Target></Project>");
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"" + external + "\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:ResolveProjectReferences", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            Assert.True(result.Output.Contains("HXW006", StringComparison.Ordinal), result.Output);
            File.Exists(marker).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an explicit package identity selects its source project and keeps dependency metadata.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DistinctPackageIdSelectsSourceAndKeepsMetadataAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string source = Path.Combine(active, "references", "Hexalith.Dep", "src", "Contracts");
            _ = Directory.CreateDirectory(source);
            string dependency = Path.Combine(source, "Contracts.csproj");
            await File.WriteAllTextAsync(dependency, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Hexalith.Dep.Contracts</PackageId></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            foreach (string utility in new[] { "tools", "samples", "evidence" })
            {
                foreach (string relative in new[] { utility, Path.Combine("libraries", utility), Path.Combine("Contracts", utility) })
                {
                    string utilityDirectory = Path.Combine(active, "references", "Hexalith.Dep", "src", relative);
                    _ = Directory.CreateDirectory(utilityDirectory);
                    await File.WriteAllTextAsync(
                        Path.Combine(utilityDirectory, "Probe.csproj"),
                        "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Hexalith.Dep.Contracts</PackageId></PropertyGroup></Project>",
                        TestContext.Current.CancellationToken).ConfigureAwait(true);
                }
            }

            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep.Contracts\" Aliases=\"global,DepAlias\" PrivateAssets=\"all\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            JsonElement items = output.RootElement.GetProperty("Items");
            items.GetProperty("PackageReference").GetArrayLength().ShouldBe(0);
            JsonElement selected = items.GetProperty("ProjectReference")[0];
            selected.GetProperty("FullPath").GetString().ShouldBe(dependency);
            selected.GetProperty("Aliases").GetString().ShouldBe("global,DepAlias");
            selected.GetProperty("PrivateAssets").GetString().ShouldBe("all");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies generated project items keep literal wildcard characters in Unix paths.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SourceProjectPathWithWildcardsIsLiteralAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows does not allow wildcard characters in directory names.");
        }

        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string source = Path.Combine(active, "references", "Hexalith.Dep", "src", "Contracts*?");
            _ = Directory.CreateDirectory(source);
            string dependency = Path.Combine(source, "Contracts.csproj");
            await File.WriteAllTextAsync(dependency, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Hexalith.Dep.Contracts</PackageId></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep.Contracts\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            JsonElement items = output.RootElement.GetProperty("Items");
            items.GetProperty("PackageReference").GetArrayLength().ShouldBe(0);
            items.GetProperty("ProjectReference").GetArrayLength().ShouldBe(1);
            items.GetProperty("ProjectReference")[0].GetProperty("FullPath").GetString().ShouldBe(dependency);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies package conversion leaves same-named projects in the active module mapped.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageConversionUsesPhysicalOriginAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string packageRoot = Path.Combine(active, "references", "Hexalith.Dep");
            _ = Directory.CreateDirectory(packageRoot);
            string activeDependency = Path.Combine(active, "Hexalith.Dep.Contracts.csproj");
            string packageDependency = Path.Combine(packageRoot, "Hexalith.Dep.Contracts.csproj");
            const string dependencyProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";
            await File.WriteAllTextAsync(activeDependency, dependencyProject, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(packageDependency, dependencyProject, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"Hexalith.Dep.Contracts.csproj\" Aliases=\"ActiveAlias\" /><ProjectReference Include=\"references/Hexalith.Dep/Hexalith.Dep.Contracts.csproj\" PrivateAssets=\"all\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            JsonElement items = output.RootElement.GetProperty("Items");
            items.GetProperty("ProjectReference").GetArrayLength().ShouldBe(1);
            items.GetProperty("ProjectReference")[0].GetProperty("FullPath").GetString().ShouldBe(activeDependency);
            items.GetProperty("ProjectReference")[0].GetProperty("Aliases").GetString().ShouldBe("ActiveAlias");
            items.GetProperty("PackageReference").GetArrayLength().ShouldBe(1);
            items.GetProperty("PackageReference")[0].GetProperty("Identity").GetString().ShouldBe("Hexalith.Dep.Contracts");
            items.GetProperty("PackageReference")[0].GetProperty("PrivateAssets").GetString().ShouldBe("all");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an absent project behind an escaping package-root link cannot become a package item.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingProjectBehindEscapingLinkIsUnmappedAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string packageRoot = Path.Combine(active, "references", "Hexalith.Dep");
            string outside = Path.Combine(root, "outside");
            _ = Directory.CreateDirectory(packageRoot);
            _ = Directory.CreateDirectory(outside);
            _ = Directory.CreateSymbolicLink(Path.Combine(packageRoot, "alias"), outside);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Dep/alias/Hexalith.Dep.Contracts.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithValidateSourceProjectsBeforeRestore", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("alias");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a project-only reference setting fails conversion with an actionable rule.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnsupportedProjectMetadataStopsPackageConversionAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string packageRoot = Path.Combine(active, "references", "Hexalith.Dep");
            _ = Directory.CreateDirectory(packageRoot);
            await File.WriteAllTextAsync(Path.Combine(packageRoot, "Hexalith.Dep.Contracts.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Dep/Hexalith.Dep.Contracts.csproj\" ReferenceOutputAssembly=\"false\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-v:q").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("ReferenceOutputAssembly");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a consumer target cannot retain items selected after resetting a mapping probe.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task ConsumerTargetItemsFollowToolModeAsync(WorkspaceMode mode)
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string sibling = Path.Combine(root, "sibling");
            string source = Path.Combine(active, "references", "Hexalith.Dep", "src", "Hexalith.Dep.Contracts");
            _ = Directory.CreateDirectory(active);
            _ = Directory.CreateDirectory(sibling);
            _ = Directory.CreateDirectory(source);
            string selectedSource = Path.Combine(source, "Hexalith.Dep.Contracts.csproj");
            await File.WriteAllTextAsync(selectedSource, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(sibling, "Hexalith.Dep.Contracts.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            string reset = mode == WorkspaceMode.Source ? "false" : "true";
            string consumerTargets = "<Project><PropertyGroup><HexalithDepRoot>" + sibling + "</HexalithDepRoot><HexalithDepFromSource>" + reset + "</HexalithDepFromSource></PropertyGroup>"
                + "<Choose><When Condition=\"'$(HexalithDepFromSource)' == 'true'\"><ItemGroup>"
                + "<ProjectReference Include=\"$(HexalithDepRoot)/src/Hexalith.Dep.Contracts/Hexalith.Dep.Contracts.csproj\" />"
                + "</ItemGroup></When><Otherwise><ItemGroup><PackageReference Include=\"Hexalith.Dep.Contracts\" />"
                + "</ItemGroup></Otherwise></Choose></Project>";
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Build.targets"),
                consumerTargets,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = Mapping(active, mode);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:HexalithReconcileSourceMapping", "-getItem:ProjectReference,PackageReference", "-getProperty:AfterMicrosoftNETSdkTargets,HexalithSourceMappingApplies").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Properties").GetProperty("AfterMicrosoftNETSdkTargets").GetString().ShouldBe(SourceMappingMaterializer.TargetsPath(workspace));
            JsonElement items = output.RootElement.GetProperty("Items");
            if (mode == WorkspaceMode.Source)
            {
                items.GetProperty("ProjectReference").GetArrayLength().ShouldBe(1);
                items.GetProperty("ProjectReference")[0].GetProperty("Identity").GetString().ShouldBe(selectedSource);
                items.GetProperty("PackageReference").GetArrayLength().ShouldBe(0);
            }
            else
            {
                items.GetProperty("ProjectReference").GetArrayLength().ShouldBe(0);
                items.GetProperty("PackageReference").GetArrayLength().ShouldBe(1);
                items.GetProperty("PackageReference")[0].GetProperty("Identity").GetString().ShouldBe("Hexalith.Dep.Contracts");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies source and package mode override optional sibling probes from the same consumer tree.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <param name="siblingPresent">Whether the optional sibling exists.</param>
    /// <param name="sourceExpected">The expected source flag.</param>
    /// <param name="nugetExpected">The expected NuGet flag.</param>
    /// <param name="configuration">The expected configuration.</param>
    /// <returns>A task for the assertions.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, true, "true", "false", "Debug")]
    [InlineData(WorkspaceMode.Source, false, "true", "false", "Debug")]
    [InlineData(WorkspaceMode.Package, true, "false", "true", "Release")]
    [InlineData(WorkspaceMode.Package, false, "false", "true", "Release")]
    public async Task ToolModeOverridesConsumerProbesAsync(WorkspaceMode mode, bool siblingPresent, string sourceExpected, string nugetExpected, string configuration)
    {
        string root = NewDirectory();
        try
        {
            string sibling = Path.Combine(root, "Hexalith.Dep");
            if (siblingPresent)
            {
                _ = Directory.CreateDirectory(sibling);
            }

            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Build.props"),
                "<Project><PropertyGroup><HexalithDepRoot Condition=\"Exists('../Hexalith.Dep')\">" + sibling + "</HexalithDepRoot><HexalithDepFromSource>true</HexalithDepFromSource><UseHexalithProjectReferences>false</UseHexalithProjectReferences><UseNuGetDeps>true</UseNuGetDeps></PropertyGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Build.targets"),
                "<Project><PropertyGroup><HexalithDepRoot>" + sibling + "</HexalithDepRoot><HexalithDepFromSource>false</HexalithDepFromSource><UseHexalithProjectReferences>false</UseHexalithProjectReferences><UseNuGetDeps>true</UseNuGetDeps><Configuration>Other</Configuration></PropertyGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = Mapping(active, mode);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(
                active,
                mapping,
                workspace,
                "msbuild",
                project,
                "-getProperty:HexalithDepRoot,HexalithDepFromSource,UseHexalithProjectReferences,UseNuGetDeps,Configuration,HexalithSourceMappingMode,HexalithSourceMappingHash").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain("\"UseHexalithProjectReferences\": \"" + sourceExpected + "\"");
            result.Output.ShouldContain("\"UseNuGetDeps\": \"" + nugetExpected + "\"");
            result.Output.ShouldContain("\"Configuration\": \"" + configuration + "\"");
            result.Output.ShouldContain("\"HexalithSourceMappingHash\": \"" + mapping.ContentHash + "\"");
            result.Output.ShouldContain("\"HexalithDepFromSource\": \"" + sourceExpected + "\"");
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Properties").GetProperty("HexalithDepRoot").GetString().ShouldBe(Path.Combine(active, "references", "Hexalith.Dep"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an external ProjectReference fails instead of resolving a sibling source.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnmappedProjectReferenceStopsBuildAsync()
    {
        string root = NewDirectory();
        try
        {
            string outside = Path.Combine(root, "outside");
            _ = Directory.CreateDirectory(outside);
            string outsideProject = WriteProject(outside, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            string namedOutside = Path.Combine(outside, "Outside.csproj");
            File.Move(outsideProject, namedOutside);
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../outside/Outside.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain(namedOutside);
            File.Exists(Path.Combine(active, "obj", "project.assets.json")).ShouldBeFalse("Unmapped projects must fail before implicit restore writes assets.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies private packaged host shims do not receive consumer source overrides.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackagedHostProjectStaysOutsideMappingScopeAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string host = Path.Combine(workspace, "hosts", "eventstore");
            _ = Directory.CreateDirectory(host);
            string project = WriteProject(host, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");

            CompositionProcessResult result = await RunDotnetAsync(host, mapping, workspace, "msbuild", project, "-getProperty:HexalithSourceMappingApplies,UseHexalithProjectReferences,HexalithSourceMappingHash").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain("\"HexalithSourceMappingApplies\": \"\"");
            result.Output.ShouldContain("\"UseHexalithProjectReferences\": \"\"");
            result.Output.ShouldContain("\"HexalithSourceMappingHash\": \"\"");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an undeclared checkout under <c>references/</c> cannot hide beneath the active root.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnmappedNestedReferenceStopsBuildAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string nested = Path.Combine(active, "references", "Hexalith.Unmapped");
            _ = Directory.CreateDirectory(nested);
            string nestedProject = Path.Combine(nested, "Unmapped.csproj");
            await File.WriteAllTextAsync(nestedProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Unmapped/Unmapped.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain(nestedProject);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a source identity cannot also arrive as a restored package.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageCopyOfSourceIdentityStopsBuildAsync()
    {
        string root = NewDirectory();
        try
        {
            string feed = Path.Combine(root, "feed");
            _ = Directory.CreateDirectory(feed);
            string packageDirectory = Path.Combine(root, "package");
            _ = Directory.CreateDirectory(packageDirectory);
            string packageProject = WriteProject(
                packageDirectory,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Dep.Contracts</PackageId><Version>1.0.0</Version></PropertyGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "Class1.cs"), "public class Class1 {}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /><add key=\"local\" value=\"" + feed + "\" /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult pack = await RunPlainDotnetAsync(root, "pack", packageProject, "-o", feed, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);
            pack.ExitCode.ShouldBe(0, pack.Output);

            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Packages.props"),
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep.Contracts\" Version=\"1.0.0\" /></ItemGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            string mappedSource = Path.Combine(active, "references", "Hexalith.Dep", "src", "Hexalith.Dep.Contracts");
            _ = Directory.CreateDirectory(mappedSource);
            await File.WriteAllTextAsync(Path.Combine(mappedSource, "Hexalith.Dep.Contracts.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(
                active,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"references/Hexalith.Dep/src/Hexalith.Dep.Contracts/Hexalith.Dep.Contracts.csproj\" /><PackageReference Include=\"Hexalith.Dep.Contracts\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0, result.Output);
            result.Output.ShouldContain("HXW005");
            result.Output.ShouldContain("Hexalith.Dep.Contracts/1.0.0");

            string wrapperDirectory = Path.Combine(root, "wrapper");
            _ = Directory.CreateDirectory(wrapperDirectory);
            string wrapperProject = WriteProject(
                wrapperDirectory,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Wrapper</PackageId><Version>1.0.0</Version></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep.Contracts\" Version=\"1.0.0\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(wrapperDirectory, "Class1.cs"), "public class Wrapper {}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult wrapperPack = await RunPlainDotnetAsync(root, "pack", wrapperProject, "-o", feed, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);
            wrapperPack.ExitCode.ShouldBe(0, wrapperPack.Output);
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Packages.props"),
                "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Wrapper\" Version=\"1.0.0\" /></ItemGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                project,
                "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Wrapper\" /></ItemGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult transitive = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            transitive.ExitCode.ShouldNotBe(0, transitive.Output);
            transitive.Output.ShouldContain("HXW005");
            transitive.Output.ShouldContain("Hexalith.Dep.Contracts/1.0.0");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies duplicate packages without compile assets still report the restored version.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AssetlessPackageCopyReportsRestoredVersionAsync()
    {
        string root = NewDirectory();
        try
        {
            string feed = Path.Combine(root, "feed");
            _ = Directory.CreateDirectory(feed);
            await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), "<configuration><packageSources><clear /><add key=\"local\" value=\"" + feed + "\" /></packageSources></configuration>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string packageDirectory = Path.Combine(root, "package");
            _ = Directory.CreateDirectory(packageDirectory);
            string packageProject = WriteProject(packageDirectory, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><PackageId>Hexalith.Dep.Meta</PackageId><Version>2.0.0</Version><IncludeBuildOutput>false</IncludeBuildOutput><NoPackageAnalysis>true</NoPackageAnalysis></PropertyGroup><ItemGroup><None Include=\"data.txt\" Pack=\"true\" PackagePath=\"contentFiles/any/any/\" /></ItemGroup></Project>");
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "data.txt"), "assetless", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult pack = await RunPlainDotnetAsync(root, "pack", packageProject, "-o", feed, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);
            pack.ExitCode.ShouldBe(0, pack.Output);

            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            await File.WriteAllTextAsync(Path.Combine(active, "Directory.Packages.props"), "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep.Meta\" Version=\"2.0.0\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Hexalith.Dep.Meta\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0, result.Output);
            result.Output.ShouldContain("HXW005");
            result.Output.ShouldContain("Hexalith.Dep.Meta/2.0.0");
            CompositionProcessResult compileItems = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-target:ResolvePackageAssets", "-getItem:ResolvedCompileFileDefinitions").ConfigureAwait(true);
            compileItems.ExitCode.ShouldBe(0, compileItems.Output);
            compileItems.Output.ShouldNotContain("Hexalith.Dep.Meta");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies Commons variants and the EventStore Tenants base path follow the selected identity.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <param name="expected">The expected source flag.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "true")]
    [InlineData(WorkspaceMode.Package, "false")]
    public async Task ConsumerSpecificPropertiesFollowMappedIdentityAsync(WorkspaceMode mode, string expected)
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Build.props"),
                "<Project><PropertyGroup><HexalithCommonsRoot>../sibling</HexalithCommonsRoot><HexalithCommonsHttpFromSource>true</HexalithCommonsHttpFromSource><HexalithCommonsServiceDefaultsFromSource>true</HexalithCommonsServiceDefaultsFromSource><HexalithTenantsBasePath>../sibling/src</HexalithTenantsBasePath></PropertyGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(active, "Directory.Build.targets"),
                "<Project><PropertyGroup><HexalithCommonsHttpFromSource>true</HexalithCommonsHttpFromSource><HexalithCommonsServiceDefaultsFromSource>true</HexalithCommonsServiceDefaultsFromSource><HexalithTenantsBasePath>../sibling/src</HexalithTenantsBasePath></PropertyGroup></Project>",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            string commons = Path.Combine(active, "references", "Hexalith.Commons");
            string tenants = Path.Combine(active, "references", "Hexalith.Tenants");
            SourceMapping mapping = new(
                mode,
                active,
                "Hexalith.Active",
                [
                    new SourceMappingEntry("Hexalith.Active", "source", active),
                    new SourceMappingEntry("Hexalith.Commons", mode == WorkspaceMode.Source ? "source" : "package", mode == WorkspaceMode.Source ? commons : null),
                    new SourceMappingEntry("Hexalith.Tenants", mode == WorkspaceMode.Source ? "source" : "package", mode == WorkspaceMode.Source ? tenants : null),
                ],
                true);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(
                active,
                mapping,
                workspace,
                "msbuild",
                project,
                "-getProperty:HexalithCommonsHttpFromSource,HexalithCommonsServiceDefaultsFromSource,HexalithTenantsBasePath").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain("\"HexalithCommonsHttpFromSource\": \"" + expected + "\"");
            result.Output.ShouldContain("\"HexalithCommonsServiceDefaultsFromSource\": \"" + expected + "\"");
            using JsonDocument output = JsonDocument.Parse(result.Output);
            output.RootElement.GetProperty("Properties").GetProperty("HexalithTenantsBasePath").GetString().ShouldBe(mode == WorkspaceMode.Source ? Path.Combine(tenants, "src") : string.Empty);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a symlink under the mapped root cannot import external source.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SymlinkedProjectReferenceOutsideMappedRootStopsBuildAsync()
    {
        string root = NewDirectory();
        try
        {
            string outside = Path.Combine(root, "outside");
            _ = Directory.CreateDirectory(outside);
            string outsideProject = WriteProject(outside, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.Move(outsideProject, Path.Combine(outside, "External.csproj"));
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            _ = Directory.CreateSymbolicLink(Path.Combine(active, "linked"), outside);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"linked/External.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("linked");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies an alias inside the active root cannot expose a package-only reference as source.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AliasToPackageOnlyReferenceStopsBuildAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string dependency = Path.Combine(active, "references", "Hexalith.Dep");
            _ = Directory.CreateDirectory(dependency);
            await File.WriteAllTextAsync(Path.Combine(dependency, "Dep.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = Directory.CreateSymbolicLink(Path.Combine(active, "alias"), dependency);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"alias/Dep.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Package);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain("alias");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a differently cased sibling is distinct on a case sensitive filesystem.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DifferentlyCasedSiblingProjectStopsBuildAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The Windows filesystem is usually case insensitive.");
        }

        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            string sibling = Path.Combine(root, "Active");
            _ = Directory.CreateDirectory(active);
            _ = Directory.CreateDirectory(sibling);
            string siblingProject = WriteProject(sibling, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            File.Move(siblingProject, Path.Combine(sibling, "External.csproj"));
            if (File.Exists(Path.Combine(active, "External.csproj")))
            {
                Assert.Skip("This filesystem is case insensitive.");
            }

            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><ProjectReference Include=\"../Active/External.csproj\" /></ItemGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "build", project, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);

            result.ExitCode.ShouldNotBe(0);
            result.Output.ShouldContain("HXW006");
            result.Output.ShouldContain(Path.Combine(sibling, "External.csproj"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a workspace apostrophe cannot break generated MSBuild conditions.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ApostropheInWorkspacePathKeepsMappingValidAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "O'Brien");
            _ = Directory.CreateDirectory(active);
            string project = WriteProject(active, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "O'Brien-run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-getProperty:HexalithSourceMappingHash").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain(mapping.ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies the private host exclusion uses its path, even when a mapped module shares its name.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MappedProjectNamedLikeHostReceivesMappingAsync()
    {
        string root = NewDirectory();
        try
        {
            string active = Path.Combine(root, "active");
            _ = Directory.CreateDirectory(active);
            string project = Path.Combine(active, "Hexalith.Builds.Module.UiHost.csproj");
            await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = Mapping(active, WorkspaceMode.Source);
            string workspace = Path.Combine(root, "run");
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

            CompositionProcessResult result = await RunDotnetAsync(active, mapping, workspace, "msbuild", project, "-getProperty:HexalithSourceMappingHash").ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain(mapping.ContentHash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static SourceMapping Mapping(string root, WorkspaceMode mode) => new(
        mode,
        root,
        "Hexalith.Active",
        [
            new SourceMappingEntry("Hexalith.Active", "source", root),
            mode == WorkspaceMode.Source
                ? new SourceMappingEntry("Hexalith.Dep", "source", Path.Combine(root, "references", "Hexalith.Dep"))
                : new SourceMappingEntry("Hexalith.Dep", "package", null),
        ],
        true);

    private static string NewDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "hexalith-msbuild-tests", "O'Brien-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteProject(string directory, string content)
    {
        string path = Path.Combine(directory, "Test.csproj");
        File.WriteAllText(path, content);
        return path;
    }

    private static Task<CompositionProcessResult> RunDotnetAsync(string directory, SourceMapping mapping, string workspace, params string[] arguments) =>
        RunProcessAsync(directory, SourceMappingMaterializer.Environment(mapping, workspace), arguments);

    private static Task<CompositionProcessResult> RunPlainDotnetAsync(string directory, params string[] arguments) =>
        RunProcessAsync(directory, null, arguments);

    private static Task<CompositionProcessResult> RunProcessAsync(string directory, IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", arguments, directory, environment),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken);
}
