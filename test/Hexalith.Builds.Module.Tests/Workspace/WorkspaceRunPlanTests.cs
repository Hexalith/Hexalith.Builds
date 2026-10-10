// <copyright file="WorkspaceRunPlanTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies one mapping is persisted and handed to both build routes.</summary>
public sealed class WorkspaceRunPlanTests
{
    /// <summary>Verifies the managed process double launches through the supported process boundary.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PortableToolchainReportsSelectedAspireVersionAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            (string aspire, string docker, string daprHome, _) = await CompositionTestFiles.BuildPortableToolchainAsync(root, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(aspire, ["--version"], root, null),
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.ExitCode.ShouldBe(0, result.Output);
            result.Output.ShouldContain(CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0]);
            (await CompositionPrerequisiteProbe.ProbeAspireAsync(aspire, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
            CompositionPrerequisiteResult prerequisites = await CompositionPrerequisiteProbe.ProbeAsync(daprHome, docker, TestContext.Current.CancellationToken).ConfigureAwait(true);
            prerequisites.IsAvailable.ShouldBeTrue(string.Join(Environment.NewLine, prerequisites.Diagnostics.Select(diagnostic => diagnostic.Message)));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a package-origin reference cannot be used as the primary native test project.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NativeTestPrimaryProjectMustBeSourceMappedAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = Path.Combine(root, "module.json");
            string dependency = Path.Combine(root, "references", "Hexalith.Dep");
            _ = Directory.CreateDirectory(dependency);
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(dependency, "Tests.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", root), new SourceMappingEntry("Hexalith.Dep", "package", null)], true);
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(sample.RunId, sample.Workspace, sample.DaprHome, sample.Ports, sample.Modules, sample.UiMarkers, mapping);
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, []);
            using Process current = Process.GetCurrentProcess();
            CompositionRunSession session = new(plan, readiness, CompositionSigningKey.Create(), current);
            string unavailableDotnet = Path.Combine(root, "missing-dotnet");

            NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(
                session,
                new PersistedProfileNativeTests("references/Hexalith.Dep/Tests.csproj", "vstest"),
                manifest,
                TestContext.Current.CancellationToken,
                unavailableDotnet).ConfigureAwait(true);

            result.Result.Outcome.RuleId.ShouldBe("HXW006");
            result.Result.Diagnostics[0].Message.ShouldContain(Path.Combine(dependency, "Tests.csproj"));
            Directory.Exists(Path.Combine(plan.Workspace, "native-tests")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies plan, native-test arguments, and generated imports share the mapping hash and mode.</summary>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="configuration">The expected build configuration.</param>
    /// <returns>A task for the assertions.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "Debug")]
    [InlineData(WorkspaceMode.Package, "Release")]
    public async Task RunPlanAndNativeTestsCarryOneMappingAsync(WorkspaceMode mode, string configuration)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            SourceMapping mapping = new(mode, root, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", root)], true);
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(
                sample.RunId,
                sample.Workspace,
                sample.DaprHome,
                sample.Ports,
                sample.Modules,
                sample.UiMarkers,
                mapping);
            await SourceMappingMaterializer.WriteAsync(mapping, plan.Workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string planJson = CompositionDocumentStore.Serialize(plan);
            string mappingJson = await File.ReadAllTextAsync(Path.Combine(plan.Workspace, "source-mapping", "mapping.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);

            plan.SourceMappingHash.ShouldBe(mapping.ContentHash);
            planJson.ShouldContain(mapping.ContentHash);
            mappingJson.ShouldContain(mapping.ContentHash);
            SourceMappingMaterializer.Environment(mapping, plan.Workspace)["HEXALITH_SOURCE_MAPPING_HASH"].ShouldBe(mapping.ContentHash);
            SourceMappingMaterializer.Environment(mapping, plan.Workspace)["Configuration"].ShouldBe(configuration);
            NativeTestExecutor.CreateArguments("vstest", "/repo/Tests.csproj", "/run/native-tests", mapping).TakeLast(2).ShouldBe(["--configuration", configuration]);
            CompositionRunPlan? restored = JsonSerializer.Deserialize<CompositionRunPlan>(planJson, CompositionDocumentStore.SerializerOptions);
            restored.ShouldNotBeNull().SourceMappingHash.ShouldBe(mapping.ContentHash);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the engine writes the passed mapping into its actual run plan and workspace.</summary>
    /// <param name="mode">The tool-selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task EngineWritesResolvedMappingIntoPlanAndWorkspaceAsync(WorkspaceMode mode)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            (string Aspire, string Docker, string DaprHome, string NativeDotnet)? portable = OperatingSystem.IsWindows()
                ? await CompositionTestFiles.BuildPortableToolchainAsync(root, TestContext.Current.CancellationToken).ConfigureAwait(true)
                : null;
            string repository = CompositionTestFiles.RepositoryRoot();
            string manifest = Path.Combine(repository, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
            SourceMapping mapping = new(mode, repository, "Hexalith.Builds", [new SourceMappingEntry("Hexalith.Builds", "source", repository)], false);
            string fakeAppHost = await CompositionTestFiles.BuildFakeAppHostAsync(root, "capture", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngineOptions options = new(fakeAppHost, typeof(ModuleCommandApplication).Assembly.Location)
            {
                SourceMapping = mapping,
                AspireCommand = portable?.Aspire ?? CompositionTestFiles.CreateAspire(root),
                DockerCommand = portable?.Docker ?? CompositionTestFiles.CreateStubDocker(root),
                DaprHome = portable?.DaprHome ?? CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
                StateDirectory = Path.Combine(root, "state"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
                ReadinessTimeout = TimeSpan.FromSeconds(5),
            };

            CompositionStartResult result = await new CompositionEngine(options).StartAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string plan = await File.ReadAllTextAsync(Path.Combine(root, "fake-apphost", "out", "captured-plan.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
            string persisted = await File.ReadAllTextAsync(Path.Combine(root, "fake-apphost", "out", "captured-mapping.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Outcome.RuleId.ShouldBe("HXR020");
            plan.ShouldContain(mapping.ContentHash);
            plan.ShouldContain(mode == WorkspaceMode.Source ? "source" : "package");
            persisted.ShouldContain(mapping.ContentHash);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the spawned native test process receives the mapping imports, hash and configuration in both modes.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <param name="configuration">The expected build configuration.</param>
    /// <param name="platform">The native test platform.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "Debug", "vstest")]
    [InlineData(WorkspaceMode.Package, "Release", "vstest")]
    [InlineData(WorkspaceMode.Source, "Debug", "mtp")]
    [InlineData(WorkspaceMode.Package, "Release", "mtp")]
    public async Task NativeTestProcessReceivesMappingEnvironmentAsync(WorkspaceMode mode, string configuration, string platform)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = Path.Combine(root, "module.json");
            string testProject = Path.Combine(root, "Test.csproj");
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(testProject, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = new(mode, root, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", root)], true);
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(sample.RunId, sample.Workspace, sample.DaprHome, sample.Ports, sample.Modules, sample.UiMarkers, mapping);
            await SourceMappingMaterializer.WriteAsync(mapping, plan.Workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, []);
            using Process current = Process.GetCurrentProcess();
            CompositionRunSession session = new(plan, readiness, CompositionSigningKey.Create(), current);
            string capture = Path.Combine(root, "native-environment.txt");
            string fakeDotnet;
            if (OperatingSystem.IsWindows())
            {
                fakeDotnet = (await CompositionTestFiles.BuildPortableToolchainAsync(root, TestContext.Current.CancellationToken).ConfigureAwait(true)).NativeDotnet;
            }
            else
            {
                fakeDotnet = Path.Combine(root, "fake-dotnet");
                string script = "printf '%s\\n' \"$HEXALITH_SOURCE_MAPPING_HASH\" \"$CustomBeforeDirectoryBuildProps\" \"$CustomAfterMicrosoftCommonProps\" \"$CustomAfterDirectoryBuildTargets\" \"$AfterMicrosoftNETSdkTargets\" \"$Configuration\" \"$*\" > '" + capture + "'\n"
                    + "while [ $# -gt 0 ]; do if [ \"$1\" = '--results-directory' ]; then shift; results=\"$1\"; fi; shift; done\n"
                    + "mkdir -p \"$results\"\n"
                    + "printf '%s' '<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>' > \"$results/native.trx\"";
                CompositionTestFiles.WriteScript(fakeDotnet, script);
            }

            NativeTestExecutionResult result = await NativeTestExecutor.ExecuteAsync(
                session,
                new PersistedProfileNativeTests("Test.csproj", platform),
                manifest,
                TestContext.Current.CancellationToken,
                fakeDotnet).ConfigureAwait(true);
            string[] captured = await File.ReadAllLinesAsync(capture, TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            captured[0].ShouldBe(mapping.ContentHash);
            captured[1].ShouldBe(SourceMappingMaterializer.TargetsPath(plan.Workspace));
            captured[2].ShouldBe(SourceMappingMaterializer.PropsPath(plan.Workspace));
            captured[3].ShouldBe(SourceMappingMaterializer.LatePropsPath(plan.Workspace));
            captured[4].ShouldBe(SourceMappingMaterializer.FinalTargetsPath(plan.Workspace));
            captured[5].ShouldBe(configuration);
            captured[6].ShouldContain("--configuration " + configuration);
            captured[6].ShouldContain(platform == "mtp" ? "--project" : "--logger");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
