// <copyright file="McpCliRunBuildTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies the run-scoped, never-published McpCli build of an McpCli source run.</summary>
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
    /// <returns>The real-build assertions.</returns>
    [Fact]
    public async Task BuildWritesOnlyTheRunWorkspaceAndIgnoresTheSiblingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping resolved = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string checkout = Path.Combine(fixture.Checkout, "references", "Hexalith.McpCli");
        string sibling = Path.Combine(fixture.Directory, "mcpcli");
        WriteFakeMcpCli(checkout, siblingGuard: false);
        WriteFakeMcpCli(sibling, siblingGuard: true);
        string[] checkoutFiles = Files(checkout);
        string[] siblingFiles = Files(sibling);
        SourceMapping mapping = resolved with { Entries = [.. resolved.Entries, new SourceMappingEntry("Hexalith.McpCli", "source", checkout)] };
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
    /// <returns>The deadline assertions.</returns>
    [Fact]
    public async Task McpCliBuildIsBoundedByTheStartupDeadlineAsync()
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
        string project = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>"
            + "<ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally><PackAsTool>true</PackAsTool><GeneratePackageOnBuild>true</GeneratePackageOnBuild>"
            + "<TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>" + target + "</Project>";
        File.WriteAllText(Path.Combine(projectDirectory, "Hexalith.McpCli.csproj"), project);
        File.WriteAllText(Path.Combine(projectDirectory, "Program.cs"), "return 0;\n");
    }
}
