// <copyright file="WorkspaceCommandModeTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies the public mode option belongs only to run and test.</summary>
public sealed class WorkspaceCommandModeTests
{
    /// <summary>Verifies the public command carries its resolved mode and hash into the engine-written plan.</summary>
    /// <param name="command">The public command.</param>
    /// <param name="mode">The requested mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("run", "source")]
    [InlineData("run", "package")]
    [InlineData("test", "source")]
    [InlineData("test", "package")]
    public async Task PublicCommandWritesResolvedMappingIntoEnginePlanAsync(string command, string mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (string Aspire, string Docker, string DaprHome, string NativeDotnet)? portable = OperatingSystem.IsWindows()
            ? await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true)
            : null;
        string repository = CompositionTestFiles.RepositoryRoot();
        CopyFixtureTree(Path.Combine(repository, "artifacts", "g4-fixture", "bin"), Path.Combine(fixture.Checkout, "artifacts", "g4-fixture", "bin"), skipBuildFolders: false);
        CopyFixtureTree(Path.Combine(repository, "test", "fixtures", "module", "executable"), Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable"), skipBuildFolders: true);
        string manifest = Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        string fakeAppHost = await CompositionTestFiles.BuildFakeAppHostAsync(fixture.Directory, "capture", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngineOptions options = new(fakeAppHost, typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = portable?.Aspire ?? CompositionTestFiles.CreateAspire(fixture.Directory),
            DockerCommand = portable?.Docker ?? CompositionTestFiles.CreateStubDocker(fixture.Directory),
            DaprHome = portable?.DaprHome ?? CompositionTestFiles.CreateDaprHome(fixture.Directory, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
            ReadinessTimeout = TimeSpan.FromSeconds(5),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        string[] arguments = command == "test"
            ? [command, "--manifest", manifest, "--profile", "full", "--mode", mode, "--output", "json"]
            : [command, "--manifest", manifest, "--mode", mode, "--output", "json"];
        _ = await ModuleCommandApplication.InvokeAsync(
            arguments,
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        string capturedPlan = Path.Combine(fixture.Directory, "fake-apphost", "out", "captured-plan.json");
        File.Exists(capturedPlan).ShouldBeTrue(output.ToString());
        using System.Text.Json.JsonDocument plan = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(capturedPlan, TestContext.Current.CancellationToken).ConfigureAwait(true));
        System.Text.Json.JsonElement sourceMapping = plan.RootElement.GetProperty("sourceMapping");
        sourceMapping.GetProperty("mode").GetString().ShouldBe(mode);
        sourceMapping.GetProperty("entries").GetArrayLength().ShouldBe(2);
        sourceMapping.GetProperty("entries").EnumerateArray().Single(entry => entry.GetProperty("identity").GetString() == "Hexalith.Dep")
            .GetProperty("origin").GetString().ShouldBe(mode);
        string hash = plan.RootElement.GetProperty("sourceMappingHash").GetString()!;
        string mapping = await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "fake-apphost", "out", "captured-mapping.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        mapping.ShouldContain(hash);
        (await File.ReadAllTextAsync(Path.Combine(fixture.Directory, "fake-apphost", "out", "captured-mapping-hash.txt"), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(hash);
    }

    /// <summary>Verifies public test mode carries its resolved hash into the spawned native test process.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <param name="profile">The selected native test platform.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("source", "full")]
    [InlineData("package", "full")]
    [InlineData("source", "full-mtp")]
    [InlineData("package", "full-mtp")]
    public async Task PublicTestCarriesResolvedHashIntoNativeProcessAsync(string mode, string profile)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string repository = CompositionTestFiles.RepositoryRoot();
        CopyFixtureTree(Path.Combine(repository, "artifacts", "g4-fixture", "bin"), Path.Combine(fixture.Checkout, "artifacts", "g4-fixture", "bin"), skipBuildFolders: false);
        CopyFixtureTree(Path.Combine(repository, "test", "fixtures", "module", "executable"), Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable"), skipBuildFolders: true);
        string manifest = Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        string fakeAppHost = await CompositionTestFiles.BuildFakeAppHostAsync(fixture.Directory, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        (string aspire, string docker, string daprHome, string nativeDotnet) = await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngineOptions options = new(fakeAppHost, typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = aspire,
            DockerCommand = docker,
            DaprHome = daprHome,
            NativeDotnetCommand = nativeDotnet,
            ProfileExecutorOverride = (_, _, _) => Task.FromResult(new ToolCommandResult("completed", ToolOutcome.Passed(), [])),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
            ReadinessTimeout = TimeSpan.FromSeconds(5),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        _ = await ModuleCommandApplication.InvokeAsync(
            ["test", "--manifest", manifest, "--profile", profile, "--mode", mode, "--output", "json"],
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        string planFile = Path.Combine(fixture.Directory, "fake-apphost", "out", "captured-plan.json");
        File.Exists(planFile).ShouldBeTrue(output.ToString());
        using System.Text.Json.JsonDocument plan = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(planFile, TestContext.Current.CancellationToken).ConfigureAwait(true));
        string hash = plan.RootElement.GetProperty("sourceMappingHash").GetString()!;
        plan.RootElement.GetProperty("sourceMapping").GetProperty("mode").GetString().ShouldBe(mode);
        string nativeFolder = profile == "full-mtp" ? "P0Fixture.NativeTests" : "P0Fixture.NativeTests.VsTest";
        string nativeCapture = Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable", nativeFolder, "native-environment.txt");
        File.Exists(nativeCapture).ShouldBeTrue(output.ToString());
        string[] nativeEnvironment = await File.ReadAllLinesAsync(nativeCapture, TestContext.Current.CancellationToken).ConfigureAwait(true);
        nativeEnvironment[0].ShouldBe(hash);
        nativeEnvironment[4].ShouldBe(mode == "source" ? "Debug" : "Release");
        nativeEnvironment[5].ShouldContain("--configuration " + (mode == "source" ? "Debug" : "Release"));
        if (profile == "full-mtp")
        {
            nativeEnvironment[5].ShouldContain("--project");
        }
    }

    /// <summary>Verifies a nonexecutable manifest keeps its prerequisite diagnostic when Aspire is absent.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NonexecutableManifestReportsHxr003BeforeAspireProbeAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string repository = CompositionTestFiles.RepositoryRoot();
        CopyFixtureTree(Path.Combine(repository, "test", "fixtures", "module"), Path.Combine(fixture.Checkout, "test", "fixtures", "module"), skipBuildFolders: true);
        File.Copy(Path.Combine(repository, "test", "fixtures", "module", "positive", "hexalith.module-manifest.v1.json"), fixture.Manifest, overwrite: true);
        CompositionEngineOptions options = new(Path.Combine(fixture.Directory, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = Path.Combine(fixture.Directory, "missing-aspire"),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        _ = await ModuleCommandApplication.InvokeAsync(
            ["run", "--manifest", fixture.Manifest, "--mode", "package", "--output", "json"],
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        output.ToString().ShouldContain("HXR003");
        output.ToString().ShouldNotContain("HXR015");
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Verifies a command discards the manifest object loaded before a direct gitlink checkout.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PublicRunReloadsManifestAfterRecordedGitlinkCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withDirectManifest: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string directManifest = Path.Combine(direct, "module.json");
        string fixtureManifest = Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        await File.WriteAllTextAsync(directManifest, await File.ReadAllTextAsync(fixtureManifest, TestContext.Current.CancellationToken).ConfigureAwait(true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string[] requiredFiles =
        [
            "artifacts/g4-fixture/bin/P0Fixture.Orders.Descriptor/P0Fixture.Orders.Descriptor.dll",
            "artifacts/g4-fixture/bin/P0Fixture.Inventory.Descriptor/P0Fixture.Inventory.Descriptor.dll",
            "artifacts/g4-fixture/bin/P0Fixture.Ui.Descriptor/P0Fixture.Ui.Descriptor.dll",
            "test/fixtures/module/executable/profiles/p0-two-module-full.fixture.json",
            "test/fixtures/module/executable/profiles/p0-two-module-full-mtp.fixture.json",
            "test/fixtures/module/executable/profiles/p0-executable-live.fixture.json",
        ];
        foreach (string required in requiredFiles)
        {
            string file = Path.Combine(direct, required);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "module.json", "artifacts", "test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "advance manifest").ConfigureAwait(true);
        ModuleManifestLoader.Load(directManifest).IsValid.ShouldBeTrue();
        CompositionEngineOptions options = new(Path.Combine(fixture.Directory, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = OperatingSystem.IsWindows()
                ? (await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true)).Aspire
                : CompositionTestFiles.CreateAspire(fixture.Directory),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        _ = await ModuleCommandApplication.InvokeAsync(
            ["run", "--manifest", directManifest, "--mode", "source", "--output", "json"],
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        (await File.ReadAllTextAsync(directManifest, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("{}");
        output.ToString().ShouldContain("HXM");
        output.ToString().ShouldNotContain("HXD");
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Verifies a newly executable staged gitlink is routed using the post-checkout manifest.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PublicRunUsesExecutableShapeAfterStagedCheckoutAsync()
    {
        string oldManifestFile = Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "positive", "hexalith.module-manifest.v1.json");
        string oldManifest = await File.ReadAllTextAsync(oldManifestFile, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withDirectManifest: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string directManifest = Path.Combine(direct, "module.json");
        await File.WriteAllTextAsync(directManifest, oldManifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string fixtureRoot = Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module");
        CopyFixtureTree(Path.Combine(fixtureRoot, "descriptors"), Path.Combine(direct, "test", "fixtures", "module", "descriptors"), skipBuildFolders: false);
        CopyFixtureTree(Path.Combine(fixtureRoot, "profiles"), Path.Combine(direct, "test", "fixtures", "module", "profiles"), skipBuildFolders: false);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "module.json", "test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "add valid persisted manifest").ConfigureAwait(true);
        string old = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Dep").ConfigureAwait(true);
        string fixtureManifest = Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        await File.WriteAllTextAsync(directManifest, await File.ReadAllTextAsync(fixtureManifest, TestContext.Current.CancellationToken).ConfigureAwait(true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "module.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "add executable manifest").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Dep").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "checkout", old).ConfigureAwait(true);
        (await File.ReadAllTextAsync(directManifest, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(oldManifest);
        ModuleManifestLoader.Load(directManifest).IsValid.ShouldBeTrue();
        CompositionEngineOptions options = new(Path.Combine(fixture.Directory, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = OperatingSystem.IsWindows()
                ? (await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true)).Aspire
                : CompositionTestFiles.CreateAspire(fixture.Directory),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        _ = await ModuleCommandApplication.InvokeAsync(
            ["run", "--manifest", directManifest, "--mode", "source", "--output", "json"],
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        output.ToString().ShouldContain("ui.descriptorAssembly");
        output.ToString().ShouldNotContain("HXR003");
        (await File.ReadAllTextAsync(directManifest, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBe(oldManifest);
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Verifies descriptor assemblies in a package-only reference are rejected before child execution.</summary>
    /// <param name="kind">The declared descriptor kind.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("module")]
    [InlineData("ui")]
    public async Task PackageOriginDescriptorFailsBeforeChildAsync(string kind)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string repository = CompositionTestFiles.RepositoryRoot();
        CopyFixtureTree(Path.Combine(repository, "artifacts", "g4-fixture", "bin"), Path.Combine(fixture.Checkout, "artifacts", "g4-fixture", "bin"), skipBuildFolders: false);
        CopyFixtureTree(Path.Combine(repository, "test", "fixtures", "module", "executable"), Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable"), skipBuildFolders: true);
        string source = Path.Combine(repository, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        string manifest = await File.ReadAllTextAsync(source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string original = kind == "module"
            ? "artifacts/g4-fixture/bin/P0Fixture.Orders.Descriptor/P0Fixture.Orders.Descriptor.dll"
            : "artifacts/g4-fixture/bin/P0Fixture.Ui.Descriptor/P0Fixture.Ui.Descriptor.dll";
        manifest = manifest.Replace(original, "references/Hexalith.Dep/Descriptor.dll", StringComparison.Ordinal);
        await File.WriteAllTextAsync(fixture.Manifest, manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string descriptor = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "Descriptor.dll");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(descriptor)!);
        await File.WriteAllTextAsync(descriptor, "not a managed assembly", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngineOptions options = new(Path.Combine(fixture.Directory, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = OperatingSystem.IsWindows()
                ? (await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true)).Aspire
                : CompositionTestFiles.CreateAspire(fixture.Directory),
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter output = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        _ = await ModuleCommandApplication.InvokeAsync(
            ["run", "--manifest", fixture.Manifest, "--mode", "package", "--output", "json"],
            output,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        output.ToString().ShouldContain("HXW006");
        output.ToString().ShouldContain(descriptor);
        output.ToString().ShouldNotContain("HXD002");
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    /// <summary>Verifies a descriptor-returned UI marker under a package-origin root is rejected before loading it.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ReturnedUiMarkerUnderPackageOriginFailsMappingGuardAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string repository = CompositionTestFiles.RepositoryRoot();
        CopyFixtureTree(Path.Combine(repository, "artifacts", "g4-fixture", "bin"), Path.Combine(fixture.Checkout, "artifacts", "g4-fixture", "bin"), skipBuildFolders: false);
        CopyFixtureTree(Path.Combine(repository, "test", "fixtures", "module", "executable"), Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable"), skipBuildFolders: true);
        string marker = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "Marker.dll");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.Copy(Path.Combine(repository, "artifacts", "g4-fixture", "bin", "P0Fixture.Ui", "P0Fixture.Ui.dll"), marker);
        string descriptorSource = Path.Combine(repository, "test", "fixtures", "module", "executable", "P0Fixture.Orders.Descriptor", "ModuleDescriptorV1.cs");
        string descriptorProjectDirectory = Path.Combine(fixture.Directory, "modified-descriptor");
        _ = Directory.CreateDirectory(descriptorProjectDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(descriptorProjectDirectory, "P0Fixture.Orders.Descriptor.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        string source = await File.ReadAllTextAsync(descriptorSource, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            Path.Combine(descriptorProjectDirectory, "ModuleDescriptorV1.cs"),
            source.Replace("artifacts/g4-fixture/bin/P0Fixture.Ui/P0Fixture.Ui.dll", "references/Hexalith.Dep/Marker.dll", StringComparison.Ordinal),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        string output = Path.Combine(fixture.Checkout, "artifacts", "g4-fixture", "bin", "P0Fixture.Orders.Descriptor");
        CompositionProcessResult built = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", Path.Combine(descriptorProjectDirectory, "P0Fixture.Orders.Descriptor.csproj"), "-v:q", "-o", output], fixture.Directory, null),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        built.ExitCode.ShouldBe(0, built.Output);

        string manifestPath = Path.Combine(fixture.Checkout, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        ManifestLoadResult loaded = ModuleManifestLoader.Load(manifestPath);
        loaded.IsValid.ShouldBeTrue();
        SourceMapping mapping = new(
            WorkspaceMode.Package,
            fixture.Checkout,
            Path.GetFileName(fixture.Checkout),
            [new SourceMappingEntry(Path.GetFileName(fixture.Checkout), "source", fixture.Checkout), new SourceMappingEntry("Hexalith.Dep", "package", null)],
            true);
        ExecutableDescriptorLoadResult result = await ExecutableDescriptorLoader.LoadAsync(
            loaded.Manifest!,
            manifestPath,
            typeof(ModuleCommandApplication).Assembly.Location,
            TestContext.Current.CancellationToken,
            mapping).ConfigureAwait(true);

        result.IsValid.ShouldBeFalse();
        result.Diagnostics[0].RuleId.ShouldBe("HXW006");
        result.Diagnostics[0].Message.ShouldContain(marker);
    }

    /// <summary>Verifies both accepted modes advance through parsing for run and test.</summary>
    /// <param name="command">The command.</param>
    /// <param name="mode">The mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("run", "source")]
    [InlineData("run", "package")]
    [InlineData("test", "source")]
    [InlineData("test", "package")]
    public async Task RunAndTestAcceptSourceAndPackageModesAsync(string command, string mode)
    {
        string[] arguments = command == "test"
            ? [command, "--manifest", "missing.json", "--profile", "full", "--mode", mode, "--output", "json"]
            : [command, "--manifest", "missing.json", "--mode", mode, "--output", "json"];
        (int exitCode, string output) = await InvokeAsync(arguments).ConfigureAwait(true);

        exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
        output.ShouldContain("HXM005");
    }

    /// <summary>Verifies an invalid mode and a down mode fail during usage parsing.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InvalidOrDownModeFailsParsingAsync()
    {
        (int invalidExit, string invalidOutput) = await InvokeAsync("run", "--manifest", "missing.json", "--mode", "other", "--output", "json").ConfigureAwait(true);
        (int downExit, string downOutput) = await InvokeAsync("down", "--manifest", "missing.json", "--mode", "package", "--output", "json").ConfigureAwait(true);

        invalidExit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        invalidOutput.ShouldContain("HXC001");
        downExit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        downOutput.ShouldContain("HXC001");
    }

    /// <summary>Verifies the public package option reaches Git resolution before descriptor loading.</summary>
    /// <param name="command">The public command.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("run")]
    [InlineData("test")]
    public async Task PublicPackageModeSkipsMissingDirectSourceAsync(string command)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string manifest = Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
        await File.WriteAllTextAsync(fixture.Manifest, await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string[] requiredFiles =
        [
            "artifacts/g4-fixture/bin/P0Fixture.Orders.Descriptor/P0Fixture.Orders.Descriptor.dll",
            "artifacts/g4-fixture/bin/P0Fixture.Inventory.Descriptor/P0Fixture.Inventory.Descriptor.dll",
            "artifacts/g4-fixture/bin/P0Fixture.Ui.Descriptor/P0Fixture.Ui.Descriptor.dll",
            "test/fixtures/module/executable/profiles/p0-two-module-full.fixture.json",
            "test/fixtures/module/executable/profiles/p0-two-module-full-mtp.fixture.json",
            "test/fixtures/module/executable/profiles/p0-executable-live.fixture.json",
        ];
        foreach (string required in requiredFiles)
        {
            string path = Path.Combine(fixture.Checkout, required);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        Directory.Delete(Path.Combine(fixture.Directory, "dependency"), recursive: true);
        string aspire = OperatingSystem.IsWindows()
            ? (await CompositionTestFiles.BuildPortableToolchainAsync(fixture.Directory, TestContext.Current.CancellationToken).ConfigureAwait(true)).Aspire
            : CompositionTestFiles.CreateAspire(fixture.Directory);
        CompositionEngineOptions options = new(Path.Combine(fixture.Directory, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
        {
            AspireCommand = aspire,
            StateDirectory = Path.Combine(fixture.Directory, "state"),
            WorkspaceRoot = Path.Combine(fixture.Directory, "workspaces"),
        };

#pragma warning disable CA2007 // Test assertions require the xUnit synchronization context.
        await using StringWriter sourceOutput = new();
        await using StringWriter packageOutput = new();
        await using StringWriter error = new();
#pragma warning restore CA2007
        string[] sourceArguments = command == "test"
            ? [command, "--manifest", fixture.Manifest, "--profile", "full", "--mode", "source", "--output", "json"]
            : [command, "--manifest", fixture.Manifest, "--mode", "source", "--output", "json"];
        string[] packageArguments = command == "test"
            ? [command, "--manifest", fixture.Manifest, "--profile", "full", "--mode", "package", "--output", "json"]
            : [command, "--manifest", fixture.Manifest, "--mode", "package", "--output", "json"];
        _ = await ModuleCommandApplication.InvokeAsync(
            sourceArguments,
            sourceOutput,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);
        _ = await ModuleCommandApplication.InvokeAsync(
            packageArguments,
            packageOutput,
            error,
            TestContext.Current.CancellationToken,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        sourceOutput.ToString().ShouldContain("HXW004");
        packageOutput.ToString().ShouldNotContain("HXW004");
        packageOutput.ToString().ShouldContain("HXD002");
        Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
    }

    private static void CopyFixtureTree(string source, string destination, bool skipBuildFolders)
    {
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            if (skipBuildFolders && relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static async Task<(int ExitCode, string Output)> InvokeAsync(params string[] arguments)
    {
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            StringWriter error = new();
            await using (error.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(arguments, output, error, TestContext.Current.CancellationToken).ConfigureAwait(true);
                error.ToString().ShouldBeEmpty();
                return (exitCode, output.ToString());
            }
        }
    }
}
