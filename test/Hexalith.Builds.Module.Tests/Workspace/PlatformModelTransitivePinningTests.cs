// <copyright file="PlatformModelTransitivePinningTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Runtime.Loader;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Exercises real transitive package selection after the production builder bypasses owner package props.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class PlatformModelTransitivePinningTests
{
    /// <summary>The actual model builder restores and executes the centrally pinned transitive API.</summary>
    /// <param name="mode">The selected source or package mode.</param>
    /// <returns>The actual restored package graph and execution assertions.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task ModelBuildKeepsCentralTransitivePinsAndAuditsAsync(WorkspaceMode mode)
    {
        const string catalog = "<Project><PropertyGroup><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup><ItemGroup>"
            + "<PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" />"
            + "<PackageVersion Include=\"Local.Parent\" Version=\"1.0.0\" />"
            + "<PackageVersion Include=\"Local.Runtime\" Version=\"2.0.0\" /></ItemGroup></Project>";
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateWithCatalogAsync(catalog).ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "transitive-model");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string feed = Path.Combine(fixture.Directory, "feed");
        _ = Directory.CreateDirectory(feed);
        await PackAsync(fixture.Directory, feed, "Local.Runtime", "1.0.0", "public static class SelectedRuntime { public static string Old() => \"old\"; }", null).ConfigureAwait(true);
        await PackAsync(fixture.Directory, feed, "Local.Runtime", "2.0.0", "public static class SelectedRuntime { public static string Current() => \"central-two\"; }", null).ConfigureAwait(true);
        await PackAsync(fixture.Directory, feed, "Local.Parent", "1.0.0", "public sealed class ParentMarker { }", "<PackageReference Include=\"Local.Runtime\" Version=\"1.0.0\" />").ConfigureAwait(true);
        string root = mapping.PlatformIdentity!.PlatformPath;
        string cache = Path.Combine(fixture.Directory, "packages");
        string configuration = "<configuration><packageSources><clear /><add key=\"local\" value=\"" + Escape(feed)
            + "\" /></packageSources><config><add key=\"globalPackagesFolder\" value=\"" + Escape(cache)
            + "\" /></config></configuration>";
        await File.WriteAllTextAsync(Path.Combine(root, "NuGet.Config"), configuration, TestContext.Current.CancellationToken).ConfigureAwait(true);

        // The real owner enables transitive pinning in Directory.Packages.props, which the builder intentionally bypasses.
        const string ownerPackages = "<Project><PropertyGroup><CentralPackageTransitivePinningEnabled>true</CentralPackageTransitivePinningEnabled></PropertyGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(root, "Directory.Packages.props"), ownerPackages, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string model = Path.Combine(root, "src", "Hexalith.Platform.Composition");
        _ = Directory.CreateDirectory(model);
        const string modelProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>"
            + "<ItemGroup><PackageReference Include=\"Local.Parent\" /></ItemGroup></Project>";
        await File.WriteAllTextAsync(Path.Combine(model, "Hexalith.Platform.Composition.csproj"), modelProject, TestContext.Current.CancellationToken).ConfigureAwait(true);
        const string modelSource = "public static class ModelProbe { public static string Read() => SelectedRuntime.Current(); }";
        await File.WriteAllTextAsync(Path.Combine(model, "ModelProbe.cs"), modelSource, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string assembly = await PlatformManifestCommand.BuildModelAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string assetsPath = Directory.GetFiles(Path.Combine(workspace, "build", "platform-model", "obj"), "project.assets.json", SearchOption.AllDirectories).ShouldHaveSingleItem();
        using JsonDocument assets = JsonDocument.Parse(await File.ReadAllTextAsync(assetsPath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        assets.RootElement.GetProperty("libraries").TryGetProperty("Local.Runtime/2.0.0", out _).ShouldBeTrue();
        assets.RootElement.GetProperty("libraries").TryGetProperty("Local.Runtime/1.0.0", out _).ShouldBeFalse();
        JsonElement audit = assets.RootElement.GetProperty("project").GetProperty("restore").GetProperty("restoreAuditProperties");
        audit.GetProperty("enableAudit").GetString().ShouldBe("true");
        audit.GetProperty("auditMode").GetString().ShouldBe("all");
        audit.GetProperty("auditLevel").GetString().ShouldBe("low");
        AssemblyLoadContext context = new("transitive-model-" + Guid.NewGuid().ToString("N"), isCollectible: true);
        context.Resolving += (load, name) => load.LoadFromAssemblyPath(Path.Combine(Path.GetDirectoryName(assembly)!, name.Name + ".dll"));
        try
        {
            context.LoadFromAssemblyPath(assembly).GetType("ModelProbe", throwOnError: true)!.GetMethod("Read")!.Invoke(null, null).ShouldBe("central-two");
        }
        finally
        {
            context.Unload();
        }
    }

    private static string Escape(string value) => System.Security.SecurityElement.Escape(value);

    private static async Task PackAsync(string root, string feed, string id, string version, string source, string? dependency)
    {
        string directory = Path.Combine(root, id + "-" + version);
        _ = Directory.CreateDirectory(directory);
        string project = Path.Combine(directory, id + ".csproj");
        string packageProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework><PackageId>" + id + "</PackageId><Version>" + version
            + "</Version><RestoreSources>" + Escape(feed) + "</RestoreSources></PropertyGroup><ItemGroup>" + dependency + "</ItemGroup></Project>";
        await File.WriteAllTextAsync(project, packageProject, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "Contract.cs"), source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["pack", project, "-c", "Release", "-o", feed, "-v:q"], directory, null),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
    }
}
