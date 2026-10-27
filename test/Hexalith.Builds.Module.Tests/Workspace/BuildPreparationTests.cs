// <copyright file="BuildPreparationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies public runtime-free preparation and actual generated MSBuild imports.</summary>
public sealed class BuildPreparationTests
{
    /// <summary>Both modes prepare and evaluate the real producer imports without runtime state.</summary>
    /// <param name="mode">The explicitly selected mode.</param>
    /// <returns>The completed verification.</returns>
    [Theory]
    [InlineData("source")]
    [InlineData("package")]
    public async Task PublicPreparationEvaluatesMappedMsBuildAsync(string mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string declaration = WriteManifest(fixture);
        string output = Path.Combine(fixture.Directory, "prepared");
        StringWriter writer = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(
            ["prepare-build", "--manifest", fixture.Manifest, "--mode", mode, "--workspace", output, "--output", "json"],
            writer,
            TextWriter.Null,
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe(0, writer.ToString());
        using JsonDocument result = JsonDocument.Parse(writer.ToString());
        result.RootElement.TryGetProperty("runId", out _).ShouldBeFalse();
        JsonElement preparation = result.RootElement.GetProperty("buildPreparation");
        preparation.GetProperty("mode").GetString().ShouldBe(mode);
        string hash = preparation.GetProperty("mappingHash").GetString()!;
        SourceMapping mapping = SourceMapping.LoadVerified(Path.Combine(output, "source-mapping", "mapping.json"), hash);
        mapping.Mode.ShouldBe(mode == "source" ? WorkspaceMode.Source : WorkspaceMode.Package);
        (await File.ReadAllTextAsync(fixture.Manifest, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(declaration);
        File.Exists(Path.Combine(output, "plan.json")).ShouldBeFalse();
        if (!OperatingSystem.IsWindows())
        {
            File.GetUnixFileMode(output).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string configuration = mode == "source" ? "Debug" : "Release";
        string selected = mode == "source" ? "true" : "false";
        string project = Path.Combine(fixture.Checkout, "Prepared.csproj");
        await File.WriteAllTextAsync(project, $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name=\"Probe\"><Error Condition=\"'$(Configuration)' != '{configuration}' or '$(UseHexalithProjectReferences)' != '{selected}' or '$(HexalithDepFromSource)' != '{selected}'\" Text=\"Wrong mapped selection\" /><Message Importance=\"high\" Text=\"PREPARED-{mode}-{configuration}\" /></Target></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Dictionary<string, string> environment = preparation.GetProperty("environment").EnumerateObject().ToDictionary(property => property.Name, property => property.Value.GetString()!);
        CompositionProcessResult probe = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-t:Probe", "-nologo"], fixture.Checkout, environment),
            TimeSpan.FromSeconds(60),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        probe.ExitCode.ShouldBe(0, probe.Output);
        probe.Output.ShouldContain($"PREPARED-{mode}-{configuration}");
    }

    /// <summary>Invalid declarations never allocate an output.</summary>
    /// <returns>The completed verification.</returns>
    [Fact]
    public async Task InvalidAndMissingManifestRefuseAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        foreach (string manifest in new[] { fixture.Manifest, Path.Combine(fixture.Checkout, "missing.json") })
        {
            string output = Path.Combine(fixture.Directory, "prepared");
            ToolCommandResult result = await BuildPreparationService.PrepareAsync(manifest, WorkspaceMode.Source, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
            Directory.Exists(output).ShouldBeFalse();
            result.BuildPreparation.ShouldBeNull();
        }
    }

    /// <summary>Occupied outputs and symbolic-link escapes preserve all existing files.</summary>
    /// <returns>The completed verification.</returns>
    [Fact]
    public async Task ExistingAndEscapingOutputRefuseAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = WriteManifest(fixture);
        string output = Path.Combine(fixture.Directory, "occupied");
        _ = Directory.CreateDirectory(output);
        string sentinel = Path.Combine(output, "sentinel");
        await File.WriteAllTextAsync(sentinel, "preserved", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult result = await BuildPreparationService.PrepareAsync(fixture.Manifest, WorkspaceMode.Source, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("preserved");
        if (!OperatingSystem.IsWindows())
        {
            string link = Path.Combine(fixture.Directory, "link");
            _ = Directory.CreateSymbolicLink(link, output);
            result = await BuildPreparationService.PrepareAsync(fixture.Manifest, WorkspaceMode.Source, Path.Combine(link, "escape"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
            Directory.Exists(Path.Combine(output, "escape")).ShouldBeFalse();
        }
    }

    /// <summary>Package provenance failures cannot fall back to source imports.</summary>
    /// <returns>The completed verification.</returns>
    [Fact]
    public async Task InvalidPackageProvenanceRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = WriteManifest(fixture);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.AppendAllTextAsync(Path.Combine(fixture.Checkout, "references", "Hexalith.Platform", "hexalith-platform.json"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string output = Path.Combine(fixture.Directory, "prepared");
        ToolCommandResult result = await BuildPreparationService.PrepareAsync(fixture.Manifest, WorkspaceMode.Package, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        result.Diagnostics.ShouldNotBeEmpty();
        result.BuildPreparation.ShouldBeNull();
        Directory.Exists(output).ShouldBeFalse();
    }

    /// <summary>Cancellation stays structured and leaves no generated imports.</summary>
    /// <returns>The completed verification.</returns>
    [Fact]
    public async Task CancelledPreparationRefusesAsync()
    {
        using PlatformManifestTestWorkspace fixture = new();
        string output = Path.Combine(fixture.Root, "prepared");
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync().ConfigureAwait(true);
        StringWriter writer = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(
            ["prepare-build", "--manifest", fixture.Save(), "--mode", "source", "--workspace", output, "--output", "json"],
            writer,
            TextWriter.Null,
            cancellation.Token).ConfigureAwait(true);
        exit.ShouldBe(130);
        writer.ToString().ShouldContain("HXC130");
        Directory.Exists(output).ShouldBeFalse();
    }

    /// <summary>Blank inputs retain the standard public usage diagnostic metadata.</summary>
    /// <param name="manifest">The blank or otherwise unused manifest argument.</param>
    /// <param name="output">The blank or otherwise unused output argument.</param>
    /// <returns>The completed metadata verification.</returns>
    [Theory]
    [InlineData("", "unused-output")]
    [InlineData(" ", "unused-output")]
    [InlineData("unused-manifest", "")]
    [InlineData("unused-manifest", " ")]
    public async Task BlankArgumentsUseUsageMetadataAsync(string manifest, string output)
    {
        ToolCommandResult result = await BuildPreparationService.PrepareAsync(manifest, WorkspaceMode.Source, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Status.ShouldBe("failed");
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
        result.Outcome.Phase.ShouldBe(ToolPhase.Usage);
        result.Outcome.Category.ShouldBe(ToolFailureCategory.Usage);
        result.Outcome.RuleId.ShouldBe("HXC001");
        ToolDiagnostic diagnostic = result.Diagnostics.ShouldHaveSingleItem();
        diagnostic.RuleId.ShouldBe("HXC001");
        diagnostic.Phase.ShouldBe(ToolPhase.Usage);
        diagnostic.Category.ShouldBe(ToolFailureCategory.Usage);
        result.BuildPreparation.ShouldBeNull();
    }

    /// <summary>Cancellation during a blocked catalog evaluation removes the already owned output.</summary>
    /// <returns>The completed notification, cancellation and cleanup verification.</returns>
    /// <exception cref="InvalidOperationException">Preparation returned before reaching the catalog process barrier.</exception>
    [Fact]
    public async Task CancellationAfterMaterializationStartsRemovesOwnedOutputAsync()
    {
        if (!await IsIsolatedCatalogProbeAsync().ConfigureAwait(true))
        {
            return;
        }

        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = WriteManifest(fixture);
        string tools = await CreateBlockingCatalogCommandAsync(fixture.Directory).ConfigureAwait(true);
        string output = Path.Combine(fixture.Directory, "prepared");
        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource<bool> materializing = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using FileSystemWatcher watcher = new(tools, "materialization-ready");
        watcher.Created += (_, _) => _ = materializing.TrySetResult(true);
        watcher.EnableRaisingEvents = true;
        Task<ToolCommandResult>? preparation = null;
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + originalPath);
            preparation = BuildPreparationService.PrepareAsync(fixture.Manifest, WorkspaceMode.Package, output, cancellation.Token);
            Task first = await Task.WhenAny(materializing.Task, preparation).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (first == preparation)
            {
                ToolCommandResult early = await preparation.ConfigureAwait(true);
                throw new InvalidOperationException("Preparation ended before the materialization barrier: " + JsonSerializer.Serialize(early));
            }

            (await materializing.Task.ConfigureAwait(true)).ShouldBeTrue();
            Directory.Exists(output).ShouldBeTrue();
            File.Exists(Path.Combine(output, "source-mapping", "mapping.json")).ShouldBeTrue();
            preparation.IsCompleted.ShouldBeFalse();
            await cancellation.CancelAsync().ConfigureAwait(true);
            ToolCommandResult result = await preparation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Status.ShouldBe("cancelled");
            result.Outcome.ExitCode.ShouldBe(ToolExitCode.Cancelled);
            result.Outcome.RuleId.ShouldBe("HXC130");
            result.BuildPreparation.ShouldBeNull();
            Directory.Exists(output).ShouldBeFalse();
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
            Environment.SetEnvironmentVariable("PATH", originalPath);
            if (preparation is not null)
            {
                _ = await preparation.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    private static async Task<bool> IsIsolatedCatalogProbeAsync()
    {
        const string isolated = "HEXALITH_BUILD_PREPARATION_ISOLATED_CATALOG";
        if (Environment.GetEnvironmentVariable(isolated) == "true")
        {
            return true;
        }

        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string appHost = Path.Combine(AppContext.BaseDirectory, "Hexalith.Builds.Module.Tests" + extension);
        CompositionProcessResult child = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo(
                appHost,
                ["-method", "*" + nameof(CancellationAfterMaterializationStartsRemovesOwnedOutputAsync), "-noLogo"],
                Directory.GetCurrentDirectory(),
                new Dictionary<string, string> { [isolated] = "true" }),
            TimeSpan.FromSeconds(90),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        child.ExitCode.ShouldBe(0, child.Output + child.ErrorOutput);
        child.Output.ShouldContain("Total: 1");
        return false;
    }

    private static async Task<string> CreateBlockingCatalogCommandAsync(string root)
    {
        string project = Path.Combine(root, "catalog-barrier");
        string tools = Path.Combine(project, "out");
        _ = Directory.CreateDirectory(project);
        await File.WriteAllTextAsync(Path.Combine(project, "CatalogBarrier.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><UseAppHost>true</UseAppHost></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(project, "Program.cs"), "using System; using System.IO; using System.Threading; File.WriteAllText(Path.Combine(AppContext.BaseDirectory, \"materialization-ready\"), \"ready\"); using ManualResetEventSlim barrier = new(false); barrier.Wait();", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionProcessResult built = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "--configuration", "Debug", "--output", tools, "-v:q"], root, null),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        built.ExitCode.ShouldBe(0, built.Output);
        string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        string command = Path.Combine(tools, "dotnet" + extension);
        File.Copy(Path.Combine(tools, "CatalogBarrier" + extension), command);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(command, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return tools;
    }

    private static string WriteManifest(WorkspaceGitFixture fixture)
    {
        string declaration = File.ReadAllText(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "platform", "valid.json"));
        File.WriteAllText(fixture.Manifest, declaration);
        File.WriteAllText(Path.Combine(fixture.Checkout, "README.md"), "Never execute this file.");
        return declaration;
    }
}
