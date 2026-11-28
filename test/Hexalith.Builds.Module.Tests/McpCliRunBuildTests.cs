// <copyright file="McpCliRunBuildTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies the run-scoped, never-published McpCli build of an McpCli source run.</summary>
// Finite startup assertions must not compete with independent SDK/process fixtures.
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class McpCliRunBuildTests
{
    private const string _mcpCliModule = """
        {"identity":{"moduleId":"mcpcli","servers":[],"dependencies":["eventstore"]},
        "runtime":{"dapr":[],"extensions":[],"resources":[]},
        "surfaces":{"interfaces":[]},
        "integration":{"topics":[],"secrets":[],"dynamicSecretNamespaces":[],"identityNeeds":[],"egress":[],"providers":[],"workers":[]},
        "lifecycle":{"readiness":[],"tasks":[],"recoveryHooks":[],"fenceHooks":[],"replay":{"reopenBeforeReplay":false},"startup":{},
        "criticalFlows":[],"smokeSurfaces":[],"classification":{"changeClass":"additive","reason":"Declare the McpCli minimum environment."},"recoveryInventory":[]}}
        """;

    /// <summary>Exercises the public run, debug and down commands with a real run-private tool build.</summary>
    /// <param name="command">The public lifecycle command.</param>
    /// <param name="package">Whether Contracts are restored from the pinned package feed.</param>
    /// <param name="ignoredInput">Whether the initialized package checkout contains an ignored build input.</param>
    /// <returns>The run and teardown assertions.</returns>
    [Theory]
    [InlineData("run", false, false)]
    [InlineData("debug", false, false)]
    [InlineData("run", true, false)]
    [InlineData("run", true, true)]
    public async Task CompletePlatformCommandsBuildMappedMcpCliAndDownRemovesItAsync(string command, bool package, bool ignoredInput)
    {
        SkipWindows();
        CancellationToken token = TestContext.Current.CancellationToken;
        using WorkspaceGitFixture fixture = await CompleteEnvironmentFixture.CreateGitAsync().ConfigureAwait(true);
        string mcpCliReference = ignoredInput ? "Hexalith.MCPCLI" : "Hexalith.McpCli";
        (string manifest, Dictionary<string, string> commits, Dictionary<string, string> hashes) =
            await CompleteEnvironmentFixture.PopulateGitAsync(fixture, package, mcpCliReference: mcpCliReference).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string checkout = Path.Combine(platform, "references", mcpCliReference);
        string ignoredTarget = Path.Combine(checkout, "Directory.Build.targets");
        if (ignoredInput)
        {
            await File.WriteAllTextAsync(
                ignoredTarget,
                """
                <Project><Target Name="RefuseLiveCheckout" BeforeTargets="CoreCompile"><Error Text="Ignored checkout input was executed." /></Target></Project>
                """,
                token).ConfigureAwait(true);
        }
        else if (package)
        {
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "submodule", "deinit", "--", "references/" + mcpCliReference).ConfigureAwait(true);
            File.Exists(Path.Combine(checkout, ".git")).ShouldBeFalse();
        }

        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(fixture.Directory, "ready", token).ConfigureAwait(true);
        CompositionEngineOptions options = Options(fixture.Directory, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(60),
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
            DeclarationOptionsOverride = declaration => declaration with { ReleaseCommits = commits, ReleaseSnapshotHashes = hashes },
        };
        string mode = package ? "package" : "source";
        string configuration = package ? "Release" : "Debug";
        await using StringWriter output = new();
        int exit = await ModuleCommandApplication.InvokeAsync(
            [command, "--manifest", manifest, "--mode", mode, "--output", "json"], output, TextWriter.Null, token, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.Success, output.ToString());
        using JsonDocument result = JsonDocument.Parse(output.ToString());
        string runId = result.RootElement.GetProperty("runId").GetString()!;
        string runWorkspace = Path.Combine(fixture.Directory, "workspaces", runId);
        string artifactRoot = Path.Combine(runWorkspace, "tools", "mcpcli");
        string privateSource = Path.Combine(runWorkspace, "tools", "source", "Hexalith.McpCli");
        string tool = Path.Combine(artifactRoot, "bin", "Hexalith.McpCli", configuration.ToLowerInvariant(), "Hexalith.McpCli.dll");
        try
        {
            File.Exists(tool).ShouldBeTrue();
            if (package)
            {
                string head = (await WorkspaceGitFixture.RunGitAsync(checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
                head.ShouldBe(commits["mcpcli/9.3.6"]);
                File.Exists(Path.Combine(privateSource, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj")).ShouldBeTrue();
                File.Exists(Path.Combine(privateSource, "Directory.Build.targets")).ShouldBeFalse();
            }

            Directory.EnumerateFiles(artifactRoot, "*.nupkg", SearchOption.AllDirectories).ShouldBeEmpty();
            using JsonDocument assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(artifactRoot, "obj", "Hexalith.McpCli", "project.assets.json"), token).ConfigureAwait(true));
            JsonElement libraries = assets.RootElement.GetProperty("libraries");
            libraries.GetProperty(CompleteEnvironmentFixture.ContractLibrary).GetProperty("type").GetString().ShouldBe(package ? "package" : "project");
            libraries.EnumerateObject().ShouldContain(library => library.Name.StartsWith("Hexalith.McpCli.Core/", StringComparison.Ordinal) && library.Value.GetProperty("type").GetString() == "project");

            using JsonDocument plan = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(appHost)!, "captured-plan.json"), token).ConfigureAwait(true));
            plan.RootElement.GetProperty("platformActiveModule").GetString().ShouldBe("platform");
            plan.RootElement.GetProperty("mcpCliToolPath").GetString().ShouldBe(tool);
            SourceMapping mapping = SourceMapping.LoadVerified(
                Path.Combine(runWorkspace, "source-mapping", "mapping.json"),
                plan.RootElement.GetProperty("sourceMapping").GetProperty("contentHash").GetString()!);
            IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [manifest])
            {
                Workspace = runWorkspace,
                ActiveModule = "platform",
                ReleaseCommits = commits,
                ReleaseSnapshotHashes = hashes,
            });
            closure.Select(declaration => CompositionDeclarationClosure.Id(declaration.Declaration)).Order(StringComparer.Ordinal)
                .ShouldBe(CompleteEnvironmentFixture.SelectedOwners);
            plan.RootElement.GetProperty("compositionContentIdentity").GetString().ShouldBe(TestEnvironmentDescriptorWriter.CompositionIdentity(closure, null));

            string diagnostics = output.ToString();
            diagnostics.ShouldContain("HXI010");
            diagnostics.ShouldContain(PlatformManifestCommand.McpCliProjectPath(mapping));
            diagnostics.ShouldContain("Conversations fixture startup");
            diagnostics.ShouldContain(configuration + " build");
            diagnostics.ShouldContain(tool);

            using JsonDocument readiness = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(runWorkspace, "readiness.json"), token).ConfigureAwait(true));
            double duration = readiness.RootElement.GetProperty("startupDurationSeconds").GetDouble();
            duration.ShouldBeGreaterThan(0);
            JsonElement readinessDiagnostic = result.RootElement.GetProperty("diagnostics").EnumerateArray()
                .Single(diagnostic => diagnostic.GetProperty("ruleId").GetString() == "HXI010" && diagnostic.GetProperty("field").GetString() == "readiness");
            readinessDiagnostic.GetProperty("message").GetString().ShouldStartWith(
                string.Create(CultureInfo.InvariantCulture, $"Every run resource reported healthy in {duration:0.###} seconds."));
            readiness.RootElement.GetProperty("effectiveDeadlineSeconds").GetDouble().ShouldBe(120);
            readiness.RootElement.GetProperty("effectiveDeadlineModuleId").GetString().ShouldBe("conversations");
        }
        finally
        {
            await using StringWriter down = new();
            int downExit = await ModuleCommandApplication.InvokeAsync(
                ["down", "--manifest", manifest, "--run-id", runId, "--output", "json"], down, TextWriter.Null, CancellationToken.None, typeof(ModuleCommandApplication).Assembly.Location, options).ConfigureAwait(true);
            downExit.ShouldBe((int)ToolExitCode.Success, down.ToString());
        }

        File.Exists(tool).ShouldBeFalse();
        Directory.Exists(runWorkspace).ShouldBeFalse();
        File.Exists(Path.Combine(checkout, ".git")).ShouldBeTrue();
        File.Exists(ignoredTarget).ShouldBe(ignoredInput);
    }

    /// <summary>Refuses an unpinned transitive Contracts assembly before building or starting the AppHost.</summary>
    /// <returns>The startup refusal assertions.</returns>
    [Fact]
    public async Task CompletePackageRunRefusesUnpinnedTransitiveContractsAsync()
    {
        SkipWindows();
        using WorkspaceGitFixture fixture = await CompleteEnvironmentFixture.CreateGitAsync().ConfigureAwait(true);
        (string manifest, Dictionary<string, string> commits, Dictionary<string, string> hashes) =
            await CompleteEnvironmentFixture.PopulateGitAsync(fixture, package: true, unpinnedTransitively: true).ConfigureAwait(true);
        CancellationToken token = TestContext.Current.CancellationToken;
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(fixture.Directory, "ready", token).ConfigureAwait(true);
        int modelBuilds = 0;
        CompositionEngineOptions options = Options(fixture.Directory, appHost) with
        {
            ReadinessTimeout = TimeSpan.FromSeconds(60),
            PlatformModelBuildOverride = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref modelBuilds);
                return Task.FromResult(appHost);
            },
            DeclarationOptionsOverride = declaration => declaration with { ReleaseCommits = commits, ReleaseSnapshotHashes = hashes },
        };

        await using StringWriter output = new();
        int exit = await ModuleCommandApplication.InvokeAsync(
            ["run", "--manifest", manifest, "--mode", "package", "--output", "json"],
            output,
            TextWriter.Null,
            token,
            typeof(ModuleCommandApplication).Assembly.Location,
            options).ConfigureAwait(true);

        exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, output.ToString());
        string diagnostics = output.ToString();
        diagnostics.ShouldContain("HXR020");
        diagnostics.ShouldContain("Hexalith.Unpinned.Contracts/9.3.6", customMessage: diagnostics);
        diagnostics.ShouldContain("without the catalog-pinned Contracts version");
        modelBuilds.ShouldBe(0);
        File.Exists(Path.Combine(Path.GetDirectoryName(appHost)!, "captured-plan.json")).ShouldBeFalse();
    }

    /// <summary>Refuses a missing direct package project or drift from the initially selected gitlink.</summary>
    /// <param name="changeGitlink">Whether to change the staged gitlink after mapping.</param>
    /// <returns>The exact direct-path refusal assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletePackageToolRefusesMissingProjectOrGitlinkDriftAsync(bool changeGitlink)
    {
        using WorkspaceGitFixture fixture = await CompleteEnvironmentFixture.CreateGitAsync().ConfigureAwait(true);
        (string manifest, _, _) = await CompleteEnvironmentFixture.PopulateGitAsync(fixture, package: false).ConfigureAwait(true);
        CancellationToken token = TestContext.Current.CancellationToken;
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, token).ConfigureAwait(true);
        string required = PlatformManifestCommand.McpCliProjectPath(mapping);
        string nearby = Path.Combine(fixture.Directory, "mcpcli");
        WriteFakeMcpCli(nearby, siblingGuard: true);
        if (changeGitlink)
        {
            string checkout = Path.Combine(mapping.Root, "references", "Hexalith.McpCli");
            _ = await WorkspaceGitFixture.RunGitAsync(checkout, "config", "user.name", "Workspace Test").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(checkout, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(checkout, "commit", "--allow-empty", "-m", "move fixture tool").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(mapping.Root, "add", "references/Hexalith.McpCli").ConfigureAwait(true);
        }
        else
        {
            File.Delete(required);
        }

        string workspace = Path.Combine(fixture.Directory, "missing-mcpcli-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, token).ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() =>
            PlatformManifestCommand.BuildMcpCliAsync(mapping, workspace, null, completeEnvironment: true, token)).ConfigureAwait(true);
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW004" && diagnostic.Message.Contains(required, StringComparison.Ordinal));
        Directory.Exists(Path.Combine(workspace, "tools")).ShouldBeFalse();
    }

    /// <summary>Verifies the build is Debug, writes only below the run workspace, uses the pinned catalog and never packs.</summary>
    [Fact]
    public void BuildArgumentsUseDebugRunWorkspaceArtifactsAndThePinnedCatalog()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "run");
        string project = Path.Combine(Path.GetTempPath(), "references", "Hexalith.McpCli", "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");
        string catalog = Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props");

        IReadOnlyList<string> arguments = PlatformManifestCommand.McpCliBuildArguments(project, workspace, catalog);

        arguments.Take(6).ShouldBe(["build", project, "--configuration", "Debug", "--artifacts-path", Path.Combine(workspace, "tools", "mcpcli")]);
        arguments.ShouldContain("-p:GeneratePackageOnBuild=false");
        arguments.ShouldContain("-p:CentralPackageTransitivePinningEnabled=true");
        arguments.ShouldContain("-p:CustomBeforeDirectoryBuildProps=" + catalog);
        arguments.ShouldContain("-p:DirectoryPackagesPropsPath=" + catalog);
        arguments.ShouldContain("-p:ImportDirectoryPackagesProps=false");
        arguments.ShouldContain("-p:CentralPackageVersionsFileImported=true");
        arguments.ShouldContain("-nodeReuse:false");
        arguments.ShouldNotContain("--output");
        arguments.ShouldNotContain("pack");
        arguments.ShouldNotContain("publish");
        PlatformManifestCommand.McpCliToolPath(workspace).ShouldBe(Path.Combine(workspace, "tools", "mcpcli", "bin", "Hexalith.McpCli", "debug", "Hexalith.McpCli.dll"));
    }

    /// <summary>Verifies only an McpCli source run builds the tool.</summary>
    /// <param name="activeModule">The selected v2 module identity.</param>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="expected">Whether the run builds McpCli.</param>
    [Theory]
    [InlineData("mcpcli", WorkspaceMode.Source, true)]
    [InlineData("mcpcli", WorkspaceMode.Package, false)]
    [InlineData("eventstore", WorkspaceMode.Source, false)]
    [InlineData("memories", WorkspaceMode.Source, false)]
    [InlineData("tenants", WorkspaceMode.Source, false)]
    [InlineData(null, WorkspaceMode.Source, false)]
    public void OnlyAnMcpCliSourceRunBuildsTheTool(string? activeModule, WorkspaceMode mode, bool expected) =>
        PlatformManifestCommand.BuildsMcpCli(activeModule, new SourceMapping(mode, Path.GetTempPath(), "Hexalith.Platform", [], true)).ShouldBe(expected);

    /// <summary>Verifies an unmapped, uninitialized, project-less or linked-out checkout fails without falling back to a sibling copy.</summary>
    /// <param name="state">The mapped McpCli checkout state.</param>
    [Theory]
    [InlineData("unmapped")]
    [InlineData("uninitialized")]
    [InlineData("project-missing")]
    [InlineData("linked-outside")]
    public void MissingCheckoutFailsNamingMcpCliAndTheExpectedProject(string state)
    {
        if (state == "linked-outside" && OperatingSystem.IsWindows())
        {
            Assert.Skip("Directory symbolic links need elevated rights on Windows.");
        }

        string directory = CompositionTestFiles.CreateDirectory();
        try
        {
            string root = Path.Combine(directory, "platform");
            string checkout = Path.Combine(root, "references", "Hexalith.McpCli");
            string sibling = Path.Combine(directory, "mcpcli");
            WriteFakeMcpCli(sibling, siblingGuard: true);
            List<SourceMappingEntry> entries = [new("Hexalith.Platform", "source", root)];
            if (state != "unmapped")
            {
                _ = Directory.CreateDirectory(checkout);
                if (state == "project-missing")
                {
                    File.WriteAllText(Path.Combine(checkout, "README.md"), "no tool project");
                }

                if (state == "linked-outside")
                {
                    // The tool project resolves only through the link, physically outside the mapped checkout.
                    _ = Directory.CreateDirectory(Path.Combine(checkout, "src"));
                    _ = Directory.CreateSymbolicLink(Path.Combine(checkout, "src", "Hexalith.McpCli"), Path.Combine(sibling, "src", "Hexalith.McpCli"));
                }

                entries.Add(new SourceMappingEntry("Hexalith.McpCli", "source", checkout));
            }

            SourceMapping mapping = new(WorkspaceMode.Source, root, "Hexalith.Platform", entries, true);
            string expected = Path.Combine(checkout, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");

            WorkspaceMappingException refusal = Should.Throw<WorkspaceMappingException>(() => PlatformManifestCommand.ResolveMcpCliProject(mapping));

            refusal.Diagnostic.RuleId.ShouldBe("HXW004");
            refusal.Diagnostic.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
            refusal.Diagnostic.Field.ShouldBe(expected);
            refusal.Diagnostic.Message.ShouldContain("'Hexalith.McpCli'");
            refusal.Diagnostic.Message.ShouldContain(expected);
            refusal.Diagnostic.Message.ShouldNotContain(sibling);
            refusal.Diagnostic.Message.ShouldContain(state == "unmapped" ? "selects no direct" : "is missing or resolves outside the checkout");
            refusal.Diagnostic.Hint.ShouldNotBeNull().ShouldContain("lies inside the checkout rather than behind a link");
            PlatformManifestCommand.McpCliProjectPath(mapping).ShouldBe(expected);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies the mapped direct reference is the only McpCli source, even when a sibling checkout exists.</summary>
    [Fact]
    public void MappedCheckoutIsSelectedAndASiblingIsIgnored()
    {
        string directory = CompositionTestFiles.CreateDirectory();
        try
        {
            string root = Path.Combine(directory, "platform");
            string checkout = Path.Combine(root, "references", "Hexalith.McpCli");
            WriteFakeMcpCli(checkout, siblingGuard: false);
            WriteFakeMcpCli(Path.Combine(directory, "mcpcli"), siblingGuard: true);
            SourceMapping mapping = new(WorkspaceMode.Source, root, "Hexalith.Platform", [new("Hexalith.Platform", "source", root), new("Hexalith.McpCli", "source", checkout)], true);

            (string selectedRoot, string project) = PlatformManifestCommand.ResolveMcpCliProject(mapping);

            selectedRoot.ShouldBe(checkout);
            project.ShouldBe(Path.Combine(checkout, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Builds an actual project: Debug, the pinned catalog, artifacts in the run workspace, no package, no checkout or sibling output.</summary>
    /// <param name="mode">The input mapping mode before the individual build chooses Debug.</param>
    /// <returns>The real-build assertions.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task BuildWritesOnlyTheRunWorkspaceAndIgnoresTheSiblingAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string checkout = Path.Combine(fixture.Checkout, "references", "Hexalith.McpCli");
        string sibling = Path.Combine(fixture.Directory, "mcpcli");
        WriteFakeMcpCli(checkout, siblingGuard: false);
        WriteFakeMcpCli(sibling, siblingGuard: true);
        string[] checkoutFiles = Files(checkout);
        string[] siblingFiles = Files(sibling);
        SourceMapping mapping = resolved with { Mode = mode, Entries = [.. resolved.Entries, new SourceMappingEntry("Hexalith.McpCli", "source", checkout)] };
        string workspace = Path.Combine(fixture.Directory, "mcpcli-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string tool = await PlatformManifestCommand.BuildMcpCliAsync(mapping, workspace, null, TestContext.Current.CancellationToken).ConfigureAwait(true);

        tool.ShouldBe(Path.Combine(workspace, "tools", "mcpcli", "bin", "Hexalith.McpCli", "debug", "Hexalith.McpCli.dll"));
        File.Exists(tool).ShouldBeTrue();
        string observed = Directory.GetFiles(workspace, "observed-catalog.txt", SearchOption.AllDirectories).ShouldHaveSingleItem();
        observed.ShouldStartWith(Path.Combine(workspace, "tools", "mcpcli", "obj") + Path.DirectorySeparatorChar);
        (await File.ReadAllLinesAsync(observed, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(["Debug", "Hexalith.Dep=1.0.0"]);
        Files(checkout).ShouldBe(checkoutFiles);
        Files(sibling).ShouldBe(siblingFiles);
        Directory.GetFiles(fixture.Directory, "*.nupkg", SearchOption.AllDirectories).ShouldBeEmpty();
        _ = await Should.ThrowAsync<TimeoutException>(() =>
            PlatformManifestCommand.BuildMcpCliAsync(mapping, workspace, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    /// <summary>Verifies a real failed build names McpCli, its project and the build's error.</summary>
    /// <returns>The failure assertions.</returns>
    [Fact]
    public async Task FailedBuildNamesMcpCliItsProjectAndTheBuildErrorAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string checkout = Path.Combine(fixture.Checkout, "references", "Hexalith.McpCli");
        WriteFakeMcpCli(checkout, siblingGuard: true);
        SourceMapping mapping = resolved with { Entries = [.. resolved.Entries, new SourceMappingEntry("Hexalith.McpCli", "source", checkout)] };
        string workspace = Path.Combine(fixture.Directory, "mcpcli-failed-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string project = Path.Combine(checkout, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => PlatformManifestCommand.BuildMcpCliAsync(mapping, workspace, null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Message.ShouldContain($"McpCli '{project}' could not build");
        refusal.Message.ShouldContain("The sibling McpCli checkout was built.");
        File.Exists(PlatformManifestCommand.McpCliToolPath(workspace)).ShouldBeFalse();
    }

    /// <summary>Refuses an unavailable or changed pinned authority before the McpCli build process starts.</summary>
    /// <param name="drift">The missing or altered authority.</param>
    /// <returns>The refusal assertion.</returns>
    /// <exception cref="InvalidOperationException">The fixture requests an unknown drift.</exception>
    [Theory]
    [InlineData("catalog")]
    [InlineData("snapshot")]
    [InlineData("bytes")]
    [InlineData("mapping")]
    [InlineData("pin")]
    [InlineData("mode")]
    [InlineData("path")]
    public async Task McpCliBuildRefusesMissingOrAlteredCatalogAuthorityAsync(string drift)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string checkout = Path.Combine(fixture.Checkout, "references", "Hexalith.McpCli");
        WriteFakeMcpCli(checkout, siblingGuard: false);
        SourceMapping mapping = resolved with { Entries = [.. resolved.Entries, new SourceMappingEntry("Hexalith.McpCli", "source", checkout)] };
        string workspace = Path.Combine(fixture.Directory, "mcpcli-refused-run");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string project = Path.Combine(checkout, "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");
        string pinnedCatalog = Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props");
        string snapshotPath = Path.Combine(workspace, "source-mapping", "catalog-versions.json");
        switch (drift)
        {
            case "catalog": File.Delete(pinnedCatalog); break;
            case "snapshot": File.Delete(snapshotPath); break;
            case "bytes": await File.AppendAllTextAsync(pinnedCatalog, "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true); break;
            case "mapping": mapping = mapping with { ActiveModule = "foreign" }; break;
            case "pin": mapping = mapping with { PlatformIdentity = mapping.PlatformIdentity! with { CatalogCommit = new string('a', 40) } }; break;
            case "mode": mapping = mapping with { Mode = WorkspaceMode.Package }; break;
            case "path":
                System.Text.Json.Nodes.JsonNode snapshot = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(snapshotPath, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
                snapshot["PinnedFiles"]!["invalid\0path"] = "malformed";
                await File.WriteAllTextAsync(snapshotPath, snapshot.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
                break;
            default: throw new InvalidOperationException("Unknown authority drift.");
        }

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(() => PlatformManifestCommand.BuildMcpCliAsync(mapping, workspace, null, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Message.ShouldStartWith($"McpCli '{project}' cannot use");
        refusal.Message.ShouldContain(drift == "catalog" ? "was not materialized" : "cannot use its pinned catalog");
        Directory.Exists(Path.Combine(workspace, "tools")).ShouldBeFalse();
    }

    /// <summary>Verifies the McpCli declaration shape validates, and an MCP interface on it is rejected.</summary>
    /// <param name="withMcpInterface">Whether the declaration adds an MCP host interface.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void McpCliShapedDeclarationValidatesAndRejectsAnMcpInterface(bool withMcpInterface)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        if (withMcpInterface)
        {
            workspace.Change(
                "modules.0.surfaces.interfaces",
                """[{"name":"mcpcli-mcp","server":"cli","protocol":"mcp","routePrefix":"/mcp","exposure":"internal-only","surfaceClass":"service","authorization":{"policy":"authenticated","permissions":[]},"callers":[],"operations":[]}]""");
        }

        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);

        if (withMcpInterface)
        {
            result.IsValid.ShouldBeFalse();
            result.Declarations.ShouldBeNull();
            result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP023" && diagnostic.Field == "modules[0].surfaces.interfaces[0].protocol");
        }
        else
        {
            result.IsValid.ShouldBeTrue(string.Join("; ", result.Diagnostics.Select(diagnostic => $"{diagnostic.RuleId} {diagnostic.Field}: {diagnostic.Message}")));
            CompositionDeclarationClosure.Id(result.Declarations.ShouldHaveSingleItem().Declaration).ShouldBe("mcpcli");
        }
    }

    /// <summary>Verifies an McpCli run or debug reports the run-scoped build once ready, and down deletes it with the workspace.</summary>
    /// <param name="debugAttach">Whether the command is <c>debug</c> rather than <c>run</c>.</param>
    /// <returns>The readiness and teardown assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpCliRunReportsTheRunScopedBuildAndDownDeletesItAsync(bool debugAttach)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        SourceMapping? buildMapping = null;
        string? buildWorkspace = null;
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = mapping,
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
            McpCliBuildOverride = async (selectedMapping, run, token) =>
            {
                buildMapping = selectedMapping;
                buildWorkspace = run;
                string tool = PlatformManifestCommand.McpCliToolPath(run);
                _ = Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
                await File.WriteAllTextAsync(tool, "run-scoped", token).ConfigureAwait(false);
                return tool;
            },
            DebugAttach = debugAttach,
        });

        CompositionStartResult result = await engine.StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        bool stopped = false;
        try
        {
            result.IsReady.ShouldBeTrue(string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            CompositionRunSession session = result.Session.ShouldNotBeNull();
            session.Plan.PlatformActiveModule.ShouldBe("mcpcli");
            buildMapping.ShouldBeSameAs(mapping);
            buildWorkspace.ShouldBe(session.Plan.Workspace);
            string tool = session.Plan.McpCliToolPath.ShouldNotBeNull();
            tool.ShouldBe(PlatformManifestCommand.McpCliToolPath(session.Plan.Workspace));
            File.Exists(tool).ShouldBeTrue();
            ToolDiagnostic reported = result.Result.Diagnostics.Where(diagnostic => diagnostic.Field == "mcpcli").ShouldHaveSingleItem();
            reported.RuleId.ShouldBe("HXI010");
            reported.Message.ShouldContain(tool);
            reported.Message.ShouldContain(PlatformManifestCommand.McpCliProjectPath(mapping));
            string capturedPlan = await File.ReadAllTextAsync(Path.Combine(Path.GetDirectoryName(appHost)!, "captured-plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            capturedPlan.ShouldContain("\"mcpCliToolPath\"");

            CompositionDownResult down = await engine.DownAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            stopped = true;
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            File.Exists(tool).ShouldBeFalse();
            Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
        }
        finally
        {
            if (result.Session is not null && !stopped)
            {
                _ = await engine.DownAsync(result.Session, CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies an EventStore or Memories run never builds McpCli.</summary>
    /// <param name="owner">The active owner module.</param>
    /// <returns>The no-build assertions.</returns>
    [Theory]
    [InlineData("eventstore")]
    [InlineData("memories")]
    public async Task OtherActiveModuleDoesNotBuildMcpCliAsync(string owner)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        string manifest = Path.Combine(mapping.Entries.Single(entry => entry.Identity == CompositionDeclarationClosure.Repository(owner)).Path!, "module", "hexalith.module-manifest.v2.json");
        string appHost = await CompositionTestFiles.BuildFakeAppHostAsync(workspace.Root, "ready", TestContext.Current.CancellationToken).ConfigureAwait(true);
        int builds = 0;
        CompositionEngine engine = new(Options(workspace.Root, appHost) with
        {
            SourceMapping = mapping,
            PlatformModelBuildOverride = (_, _, _) => Task.FromResult(appHost),
            McpCliBuildOverride = (_, run, _) =>
            {
                _ = Interlocked.Increment(ref builds);
                return Task.FromResult(PlatformManifestCommand.McpCliToolPath(run));
            },
        });

        CompositionStartResult result = await engine.StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            result.IsReady.ShouldBeTrue(string.Join(';', result.Result.Diagnostics.Select(diagnostic => diagnostic.Message)));
            result.Session!.Plan.PlatformActiveModule.ShouldBe(owner);
            builds.ShouldBe(0);
            result.Session.Plan.McpCliToolPath.ShouldBeNull();
            result.Result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Field == "mcpcli");
            Directory.Exists(Path.Combine(result.Session.Plan.Workspace, "tools")).ShouldBeFalse();
        }
        finally
        {
            if (result.Session is not null)
            {
                _ = await engine.DownAsync(result.Session, CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies the McpCli build stage is bounded by the startup deadline and stops before the model build.</summary>
    /// <param name="childTimeout">Whether the process deadline is observed before runner cancellation.</param>
    /// <returns>The deadline assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task McpCliBuildIsBoundedByTheStartupDeadlineAsync(bool childTimeout)
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        int modelBuilds = 0;
        CompositionEngineOptions options = Options(workspace.Root, typeof(McpCliRunBuildTests).Assembly.Location) with
        {
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            PlatformModelBuildOverride = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref modelBuilds);
                throw new InvalidOperationException("The model must not build after the McpCli stage exhausted the deadline.");
            },
            McpCliBuildOverride = async (_, run, token) =>
            {
                if (childTimeout)
                {
                    throw new TimeoutException("The fixture build child exhausted its deadline.");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                return PlatformManifestCommand.McpCliToolPath(run);
            },
        };
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        CompositionStartResult result = await new CompositionEngine(options).StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(8));
        result.IsReady.ShouldBeFalse();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        modelBuilds.ShouldBe(0);
        Directory.Exists(Path.Combine(options.WorkspaceRoot, result.RunId.ShouldNotBeNull())).ShouldBeFalse();
    }

    /// <summary>Verifies an McpCli build that ignores cancellation and returns after the deadline still stops before the model build.</summary>
    /// <returns>The deadline assertions.</returns>
    [Fact]
    public async Task McpCliBuildReturningAfterTheDeadlineStopsBeforeTheModelBuildAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        int modelBuilds = 0;
        CompositionEngineOptions options = Options(workspace.Root, typeof(McpCliRunBuildTests).Assembly.Location) with
        {
            SourceMapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root),
            ReadinessTimeout = TimeSpan.FromSeconds(2),
            PlatformModelBuildOverride = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref modelBuilds);
                throw new InvalidOperationException("The model must not build after the McpCli stage exhausted the deadline.");
            },
            McpCliBuildOverride = async (_, run, _) =>
            {
                // The build ignores the startup token, so only the post-build deadline check can stop the run.
                await Task.Delay(TimeSpan.FromSeconds(4), CancellationToken.None).ConfigureAwait(false);
                return PlatformManifestCommand.McpCliToolPath(run);
            },
        };
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        CompositionStartResult result = await new CompositionEngine(options).StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
        result.IsReady.ShouldBeFalse();
        result.Result.Outcome.RuleId.ShouldBe("HXR021");
        modelBuilds.ShouldBe(0);
        Directory.Exists(Path.Combine(options.WorkspaceRoot, result.RunId.ShouldNotBeNull())).ShouldBeFalse();
    }

    /// <summary>Verifies a missing McpCli checkout refuses the run before the model build or any resource.</summary>
    /// <returns>The refusal assertions.</returns>
    [Fact]
    public async Task MissingCheckoutRefusesTheRunBeforeLaunchAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        int modelBuilds = 0;
        CompositionEngineOptions options = Options(workspace.Root, typeof(McpCliRunBuildTests).Assembly.Location) with
        {
            SourceMapping = mapping,
            PlatformModelBuildOverride = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref modelBuilds);
                throw new InvalidOperationException("The model must not build after an McpCli refusal.");
            },
        };

        CompositionStartResult result = await new CompositionEngine(options).StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Session.ShouldBeNull();
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.PrerequisiteUnavailable);
        result.Result.Outcome.RuleId.ShouldBe("HXW004");
        ToolDiagnostic refusal = result.Result.Diagnostics.ShouldHaveSingleItem();
        string expected = Path.Combine(workspace.Root, "references", "Hexalith.McpCli", "src", "Hexalith.McpCli", "Hexalith.McpCli.csproj");
        refusal.Field.ShouldBe(expected);
        refusal.Message.ShouldContain("'Hexalith.McpCli'");
        refusal.Message.ShouldContain(expected);
        modelBuilds.ShouldBe(0);
        Directory.GetDirectories(options.WorkspaceRoot).ShouldBeEmpty();
    }

    /// <summary>Verifies a failed McpCli build names McpCli and its project and starts nothing.</summary>
    /// <returns>The refusal assertions.</returns>
    [Fact]
    public async Task BuildFailureNamesMcpCliAndItsProjectAsync()
    {
        SkipWindows();
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules", "[" + _mcpCliModule + "]");
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        int modelBuilds = 0;
        CompositionEngineOptions options = Options(workspace.Root, typeof(McpCliRunBuildTests).Assembly.Location) with
        {
            SourceMapping = mapping,
            PlatformModelBuildOverride = (_, _, _) =>
            {
                _ = Interlocked.Increment(ref modelBuilds);
                throw new InvalidOperationException("The model must not build after an McpCli refusal.");
            },
            McpCliBuildOverride = (_, _, _) => throw new InvalidOperationException("Hexalith.McpCli.csproj: CS1002 expected ;"),
        };

        CompositionStartResult result = await new CompositionEngine(options).StartAsync(workspace.Save(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Session.ShouldBeNull();
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        result.Result.Outcome.RuleId.ShouldBe("HXR020");
        ToolDiagnostic refusal = result.Result.Diagnostics.ShouldHaveSingleItem();
        refusal.Message.ShouldContain("McpCli run-scoped build failed: ");
        refusal.Message.ShouldContain("CS1002");
        refusal.Field.ShouldBe(PlatformManifestCommand.McpCliProjectPath(mapping));
        modelBuilds.ShouldBe(0);
        CompositionRunState state = (await new CompositionEngine(options).StateStore.TryReadAsync(result.RunId.ShouldNotBeNull(), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        state.Status.ShouldBe(CompositionRunStatus.Failed);
        state.AppHostProcessId.ShouldBeNull();
        Directory.GetDirectories(options.WorkspaceRoot).ShouldBeEmpty();
    }

    private static CompositionEngineOptions Options(string root, string appHost) => new(appHost, typeof(ModuleCommandApplication).Assembly.Location)
    {
        AspireCommand = CompositionTestFiles.CreateAspire(root),
        AspirePackagesRoot = CompositionTestFiles.CreateAspirePackages(root).Root,
        DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
        DockerCommand = CompositionTestFiles.CreateStubDocker(root),
        StateDirectory = Path.Combine(root, "state"),
        WorkspaceRoot = Path.Combine(root, "workspaces"),
        ReadinessTimeout = TimeSpan.FromSeconds(30),
        StopTimeout = TimeSpan.FromSeconds(5),
    };

    private static string[] Files(string directory) =>
        [.. Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(directory, path)).Order(StringComparer.Ordinal)];

    private static void SkipWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake toolchain uses POSIX scripts and process environment tags.");
        }
    }

    private static void WriteFakeMcpCli(string root, bool siblingGuard)
    {
        string projectDirectory = Path.Combine(root, "src", "Hexalith.McpCli");
        _ = Directory.CreateDirectory(projectDirectory);

        // A sibling copy fails if anything builds or restores it; the mapped copy records the catalog it was evaluated with.
        string target = siblingGuard
            ? "<Target Name=\"SiblingMustNotBuild\" BeforeTargets=\"Restore;_GenerateRestoreProjectSpec;Build\"><Error Text=\"The sibling McpCli checkout was built.\" /></Target>"
            : "<Target Name=\"ObserveCatalog\" BeforeTargets=\"CoreCompile\"><WriteLinesToFile File=\"$(IntermediateOutputPath)observed-catalog.txt\" Lines=\"$(Configuration);@(PackageVersion->'%(Identity)=%(Version)')\" Overwrite=\"true\" /></Target>";
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net11.0</TargetFramework>"
            + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><PackAsTool>true</PackAsTool><GeneratePackageOnBuild>true</GeneratePackageOnBuild>"
            + "<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>" + target + "</Project>";
        File.WriteAllText(Path.Combine(projectDirectory, "Hexalith.McpCli.csproj"), project);
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), "return 0;\n");
    }
}
