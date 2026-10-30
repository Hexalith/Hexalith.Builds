// <copyright file="WorkspaceGitFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Runtime;

using Xunit;

/// <summary>Builds local Git superprojects and file-backed submodules for workspace tests.</summary>
internal sealed class WorkspaceGitFixture : IDisposable
{
    private static readonly Lock _protocolLock = new();
    private static int _protocolLeases;
    private static string? _originalAllowedProtocols;

    private WorkspaceGitFixture(string directory)
    {
        Directory = directory;
        lock (_protocolLock)
        {
            if (_protocolLeases++ == 0)
            {
                _originalAllowedProtocols = Environment.GetEnvironmentVariable("GIT_ALLOW_PROTOCOL");
                Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", "file");
            }
        }
    }

    /// <summary>Gets the fixture directory.</summary>
    public string Directory { get; }

    /// <summary>Gets the cloned workspace directory.</summary>
    public string Checkout => Path.Combine(Directory, "checkout");

    /// <summary>Gets the absolute manifest path in the checkout.</summary>
    public string Manifest => Path.Combine(Checkout, "module.json");

    /// <summary>Creates a local superproject with a required direct submodule.</summary>
    /// <param name="withNested">Whether the direct submodule declares a nested submodule.</param>
    /// <param name="withSecond">Whether the root declares a second direct submodule.</param>
    /// <param name="withDirectManifest">Whether the direct submodule contains a committed manifest.</param>
    /// <param name="conditionalCatalog">Whether the catalog has a project-name-specific version.</param>
    /// <param name="largeCatalog">Whether the catalog has enough items to exceed the generic process capture bound.</param>
    /// <param name="relativeCatalogImport">Whether the pinned catalog imports a sibling props file.</param>
    /// <param name="compoundCatalogCondition">Whether the project-name condition uses a compound expression.</param>
    /// <param name="optionalCatalogImport">Whether an absent conditional import is present.</param>
    /// <param name="malformedCatalog">Whether the pinned catalog cannot be evaluated as XML.</param>
    /// <param name="functionCatalogCondition">Whether the project-name condition uses a valid MSBuild property function.</param>
    /// <param name="toolVersionRange">The Platform's accepted tool range.</param>
    /// <param name="emptyCatalogVersion">Whether the catalog evaluates an empty package version.</param>
    /// <param name="onDemandCatalogFailure">Whether one project name fails during isolated evaluation.</param>
    /// <param name="conditionalPackageId">The local package sentinel used by the conditional catalog fixture.</param>
    /// <returns>The fixture.</returns>
    public static async Task<WorkspaceGitFixture> CreateAsync(bool withNested = false, bool withSecond = false, bool withDirectManifest = false, bool conditionalCatalog = false, bool largeCatalog = false, bool relativeCatalogImport = false, bool compoundCatalogCondition = false, bool optionalCatalogImport = false, bool malformedCatalog = false, bool functionCatalogCondition = false, string? toolVersionRange = null, bool emptyCatalogVersion = false, bool onDemandCatalogFailure = false, string conditionalPackageId = "Hexalith.Dep")
    {
        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = System.IO.Directory.CreateDirectory(directory);
        WorkspaceGitFixture fixture = new(directory);
        try
        {
            if (withNested)
            {
                string nested = await fixture.CreateRepositoryAsync("nested", "nested.txt").ConfigureAwait(true);
                string dependency = await fixture.CreateRepositoryAsync("dependency", "dep.txt").ConfigureAwait(true);
                _ = await RunGitAsync(dependency, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(nested).AbsoluteUri, "nested/Other").ConfigureAwait(true);
                _ = await RunGitAsync(dependency, "commit", "-am", "add nested").ConfigureAwait(true);
            }
            else
            {
                _ = await fixture.CreateRepositoryAsync("dependency", "dep.txt").ConfigureAwait(true);
            }

            if (withDirectManifest)
            {
                string dependency = Path.Combine(directory, "dependency");
                await File.WriteAllTextAsync(Path.Combine(dependency, "module.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = await RunGitAsync(dependency, "add", "module.json").ConfigureAwait(true);
                _ = await RunGitAsync(dependency, "commit", "-m", "add recorded manifest").ConfigureAwait(true);
            }

            if (withSecond)
            {
                _ = await fixture.CreateRepositoryAsync("second", "second.txt").ConfigureAwait(true);
            }

            string builds = await fixture.CreateRepositoryAsync("builds", "builds.txt").ConfigureAwait(true);
            _ = System.IO.Directory.CreateDirectory(Path.Combine(builds, "Props"));
            string catalog = conditionalCatalog
                ? "<Project><PropertyGroup><DaprVersion>13.6.0-preview.1</DaprVersion><DaprVersion Condition=\"'$(msbuildprojectname)' == 'Hexalith.Folders.Aspire'\">13.0.0</DaprVersion></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" /><PackageVersion Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" Version=\"$(DaprVersion)\" /></ItemGroup></Project>".Replace("Hexalith.Dep", conditionalPackageId, StringComparison.Ordinal)
                : "<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" /></ItemGroup></Project>";
            if (compoundCatalogCondition)
            {
                catalog = "<Project><PropertyGroup><DaprVersion>13.6.0-preview.1</DaprVersion><DaprVersion Condition=\"'$(MSBuildProjectName)' == 'Hexalith.Folders.Aspire' Or '$(Configuration)' == '__never__'\">13.0.0</DaprVersion></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" /><PackageVersion Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" Version=\"$(DaprVersion)\" /></ItemGroup></Project>";
            }

            if (functionCatalogCondition)
            {
                catalog = "<Project><PropertyGroup><DaprVersion>13.6.0-preview.1</DaprVersion><DaprVersion Condition=\"$([System.String]::Copy('$(MSBuildProjectName)').StartsWith('Hexalith.Folders.'))\">13.0.0</DaprVersion></PropertyGroup><ItemGroup><PackageVersion Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" Version=\"$(DaprVersion)\" /></ItemGroup></Project>";
            }

            if (emptyCatalogVersion)
            {
                catalog = "<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\" \" /></ItemGroup></Project>";
            }

            if (onDemandCatalogFailure)
            {
                catalog = "<Project><PropertyGroup><BrokenVersion Condition=\"$([System.String]::Copy('$(MSBuildProjectName)').StartsWith('Broken'))\">$([System.Int32]::Parse('bad'))</BrokenVersion></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" /></ItemGroup></Project>";
            }

            if (relativeCatalogImport)
            {
                await File.WriteAllTextAsync(Path.Combine(builds, "Props", "Imported.Packages.props"), catalog, TestContext.Current.CancellationToken).ConfigureAwait(true);
                catalog = "<Project><Import Project=\"$(MSBuildThisFileDirectory)Imported.Packages.props\" /></Project>";
            }

            if (largeCatalog)
            {
                catalog = "<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" />"
                    + string.Concat(Enumerable.Range(0, 600).Select(index => $"<PackageVersion Include=\"Hexalith.Fixture.Package.{index:D4}\" Version=\"1.0.0\" />"))
                    + "</ItemGroup></Project>";
            }

            if (optionalCatalogImport)
            {
                catalog = catalog.Replace("</Project>", "<ImportGroup Condition=\"Exists('Absent.props')\"><Import Project=\"Absent.props\" /></ImportGroup></Project>", StringComparison.Ordinal);
            }

            if (malformedCatalog)
            {
                catalog = "<Project><ItemGroup>";
            }

            await File.WriteAllTextAsync(
                Path.Combine(builds, "Props", "Directory.Packages.props"),
                catalog,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await RunGitAsync(builds, "add", "Props").ConfigureAwait(true);
            _ = await RunGitAsync(builds, "commit", "-m", "add catalog").ConfigureAwait(true);
            string platform = await fixture.CreateRepositoryAsync("platform", "platform.txt").ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(platform, "hexalith-platform.json"),
                $"{{\"schemaVersion\":1,\"toolVersionRange\":\"{toolVersionRange ?? "[0.0.0,)"}\"}}",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
            _ = await RunGitAsync(platform, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(builds).AbsoluteUri, "references/Hexalith.Builds").ConfigureAwait(true);
            _ = await RunGitAsync(platform, "commit", "-am", "add Platform identity").ConfigureAwait(true);
            _ = await RunGitAsync(platform, "tag", "v1.0.0").ConfigureAwait(true);

            string root = Path.Combine(directory, "root");
            _ = System.IO.Directory.CreateDirectory(root);
            _ = await RunGitAsync(root, "init", "-b", "main").ConfigureAwait(true);
            _ = await RunGitAsync(root, "config", "protocol.file.allow", "always").ConfigureAwait(true);
            _ = await RunGitAsync(root, "config", "user.name", "Workspace Test").ConfigureAwait(true);
            _ = await RunGitAsync(root, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(root, "module.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await RunGitAsync(root, "add", "module.json").ConfigureAwait(true);
            _ = await RunGitAsync(root, "commit", "-m", "initial").ConfigureAwait(true);
            _ = await RunGitAsync(root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(Path.Combine(directory, "dependency")).AbsoluteUri, "references/Hexalith.Dep").ConfigureAwait(true);
            _ = await RunGitAsync(root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(builds).AbsoluteUri, "references/Hexalith.Builds").ConfigureAwait(true);
            _ = await RunGitAsync(root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(platform).AbsoluteUri, "references/Hexalith.Platform").ConfigureAwait(true);
            if (withSecond)
            {
                _ = await RunGitAsync(root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(Path.Combine(directory, "second")).AbsoluteUri, "references/Hexalith.Second").ConfigureAwait(true);
            }

            _ = await RunGitAsync(root, "commit", "-am", "add direct references").ConfigureAwait(true);
            _ = await RunGitAsync(directory, "clone", root, fixture.Checkout).ConfigureAwait(true);
            _ = await RunGitAsync(fixture.Checkout, "config", "protocol.file.allow", "always").ConfigureAwait(true);
            return fixture;
        }
        catch
        {
            fixture.Dispose();
            throw;
        }
    }

    /// <summary>Runs one local Git command.</summary>
    /// <param name="directory">The working directory.</param>
    /// <param name="arguments">The Git arguments.</param>
    /// <returns>The captured result.</returns>
    /// <exception cref="InvalidOperationException">Git failed.</exception>
    public static async Task<CompositionProcessResult> RunGitAsync(string directory, params string[] arguments)
    {
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("git", arguments, directory, null),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        return !result.Started || result.ExitCode != 0
            ? throw new InvalidOperationException($"Local Git command failed in '{directory}' ({string.Join(' ', arguments)}): {result.Output}")
            : result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        finally
        {
            lock (_protocolLock)
            {
                if (--_protocolLeases == 0)
                {
                    Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", _originalAllowedProtocols);
                }
            }
        }
    }

    private async Task<string> CreateRepositoryAsync(string name, string fileName)
    {
        string path = Path.Combine(Directory, name);
        _ = System.IO.Directory.CreateDirectory(path);
        _ = await RunGitAsync(path, "init", "-b", "main").ConfigureAwait(true);
        _ = await RunGitAsync(path, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await RunGitAsync(path, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(path, fileName), name, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await RunGitAsync(path, "add", fileName).ConfigureAwait(true);
        _ = await RunGitAsync(path, "commit", "-m", "initial").ConfigureAwait(true);
        return path;
    }
}
