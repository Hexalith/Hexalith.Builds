// <copyright file="PlatformModelBuildIsolationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Builds concurrent provider graphs without writing restore or compile outputs into shared source trees.</summary>
public sealed class PlatformModelBuildIsolationTests
{
    /// <summary>Concurrent runs retain the copied provider path and pinned configuration while isolating every project in the graph.</summary>
    /// <param name="mode">The selected Debug/source or Release/package boundary.</param>
    /// <returns>The real concurrent build and filesystem assertions.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task ConcurrentProviderBuildsOwnAllGraphOutputsAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping mapping = resolved with { Mode = mode };
        string platform = mapping.PlatformIdentity!.PlatformPath;
        await WriteGraphAsync(platform).ConfigureAwait(true);
        string source = Path.Combine(platform, "src");
        string[] before = Files(source);
        string[] workspaces = [Path.Combine(fixture.Directory, "run-one"), Path.Combine(fixture.Directory, "run-two")];
        foreach (string workspace in workspaces)
        {
            await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddMinutes(1);
        string[] assemblies = await Task.WhenAll(workspaces.Select(workspace => PlatformManifestCommand.BuildModelAsync(mapping, workspace, deadline, TestContext.Current.CancellationToken))).ConfigureAwait(true);

        Files(source).ShouldBe(before);
        for (int index = 0; index < workspaces.Length; index++)
        {
            string workspace = workspaces[index];
            string output = Path.Combine(workspace, "hosts", "platform-model");
            assemblies[index].ShouldBe(Path.Combine(output, "Hexalith.Platform.Composition.dll"));
            foreach (string assembly in new[] { "Hexalith.Platform.Composition", "Provider.Dependency", "Provider.Leaf" })
            {
                File.Exists(Path.Combine(output, assembly + ".dll")).ShouldBeTrue();
            }

            string intermediate = Path.Combine(workspace, "build", "platform-model", "obj");
            Directory.GetFiles(intermediate, "project.assets.json", SearchOption.AllDirectories).Length.ShouldBe(3);
            string[] observations = Directory.GetFiles(intermediate, "observed-build.txt", SearchOption.AllDirectories);
            observations.Length.ShouldBe(3);
            foreach (string observation in observations)
            {
                string[] lines = await File.ReadAllLinesAsync(observation, TestContext.Current.CancellationToken).ConfigureAwait(true);
                lines.ShouldContain(mode == WorkspaceMode.Source ? "Debug" : "Release");
                lines.ShouldContain("Hexalith.Dep=1.0.0");
                lines[3].ShouldStartWith(intermediate + Path.DirectorySeparatorChar);
                lines[4].ShouldStartWith(workspace + Path.DirectorySeparatorChar);
            }
        }
    }

    private static string[] Files(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal)];

    private static async Task WriteGraphAsync(string root)
    {
        await File.WriteAllTextAsync(
            Path.Combine(root, "Directory.Build.targets"),
            """
            <Project><Target Name="ObserveBuild" BeforeTargets="CoreCompile">
              <WriteLinesToFile File="$(IntermediateOutputPath)observed-build.txt" Lines="$(MSBuildProjectName);$(Configuration);@(PackageVersion->'%(Identity)=%(Version)');$(MSBuildProjectExtensionsPath);$(OutputPath)" Overwrite="true" />
            </Target></Project>
            """,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        await WriteProjectAsync(root, "Provider.Leaf", null, "public static class Leaf { public const string Value = \"fixture\"; }").ConfigureAwait(true);
        await WriteProjectAsync(root, "Provider.Dependency", "Provider.Leaf", "public static class Dependency { public static string Value => Leaf.Value; }").ConfigureAwait(true);
        await WriteProjectAsync(root, "Hexalith.Platform.Composition", "Provider.Dependency", "public static class Provider { public static string Value => Dependency.Value; }").ConfigureAwait(true);
    }

    private static async Task WriteProjectAsync(string root, string name, string? reference, string code)
    {
        string directory = Path.Combine(root, "src", name);
        _ = Directory.CreateDirectory(directory);
        string projectReference = reference is null ? string.Empty : "<ItemGroup><ProjectReference Include=\"../" + reference + "/" + reference + ".csproj\" /></ItemGroup>";
        await File.WriteAllTextAsync(Path.Combine(directory, name + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>" + projectReference + "</Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "Code.cs"), code, TestContext.Current.CancellationToken).ConfigureAwait(true);
    }
}
