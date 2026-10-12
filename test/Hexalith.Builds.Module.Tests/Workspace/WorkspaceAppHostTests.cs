// <copyright file="WorkspaceAppHostTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Net;
using System.Reflection;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>Verifies composed AppHost module projects receive the runner's mapping settings.</summary>
public sealed class WorkspaceAppHostTests
{
    /// <summary>Verifies every composed module project receives the same imports, hash and configuration.</summary>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="configuration">The expected configuration.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "Debug")]
    [InlineData(WorkspaceMode.Package, "Release")]
    public async Task ComposedModulesReceiveMappingSettingsAsync(WorkspaceMode mode, string configuration)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            foreach (CompositionRunModule module in sample.Modules)
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(module.ProjectPath)!);
                await File.WriteAllTextAsync(module.ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            SourceMapping mapping = new(mode, root, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", root)], true);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(sample.RunId, sample.Workspace, sample.DaprHome, sample.Ports, sample.Modules, sample.UiMarkers, mapping);
            await SourceMappingMaterializer.WriteAsync(mapping, plan.Workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await CompositionDaprComponentRenderer.WriteAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true);
            DistributedApplicationOptions launchOptions = new()
            {
                Args = [],
                AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(launchOptions);
            _ = builder.AddDapr(options => options.DaprPath = Path.Combine(root, "dapr-home", "tools", "dapr"));

            RunTopology.Compose(builder, plan, CompositionSigningKey.Create());
            RunReadiness.InvocationProbes(plan, new DistributedApplicationModel(builder.Resources)).ShouldAllBe(probe => probe.Endpoint == "/alive");

            foreach (CompositionRunModule module in plan.Modules)
            {
                ProjectResource project = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == module.ModuleId);
                project.TryGetEnvironmentVariables(out IEnumerable<EnvironmentCallbackAnnotation>? annotations).ShouldBeTrue();
                Dictionary<string, object> environment = new(StringComparer.Ordinal);
                EnvironmentCallbackContext context = new(builder.ExecutionContext, project, environment, TestContext.Current.CancellationToken);
                foreach (EnvironmentCallbackAnnotation annotation in annotations.TakeLast(SourceMappingMaterializer.Environment(mapping, plan.Workspace).Count))
                {
                    await annotation.Callback(context).ConfigureAwait(true);
                }

                environment["HEXALITH_SOURCE_MAPPING_HASH"].ShouldBe(mapping.ContentHash);
                environment["CustomBeforeDirectoryBuildProps"].ShouldBe(SourceMappingMaterializer.TargetsPath(plan.Workspace));
                environment["CustomAfterMicrosoftCommonProps"].ShouldBe(SourceMappingMaterializer.PropsPath(plan.Workspace));
                environment["CustomAfterDirectoryBuildTargets"].ShouldBe(SourceMappingMaterializer.LatePropsPath(plan.Workspace));
                environment["AfterMicrosoftNETSdkTargets"].ShouldBe(SourceMappingMaterializer.FinalTargetsPath(plan.Workspace));
                environment["Configuration"].ShouldBe(configuration);
                project.TryGetLastAnnotation<ExecutableAnnotation>(out ExecutableAnnotation? executable).ShouldBeTrue();
                executable.Command.ShouldBe("dotnet");
#pragma warning disable CS0618 // Inspect Aspire's composed resource arguments at the public resource boundary.
                string[] launchArguments = await project.GetArgumentValuesAsync().ConfigureAwait(true);
#pragma warning restore CS0618
                launchArguments.ShouldBe(["run", "--project", module.ProjectPath, "--configuration", configuration, "--no-launch-profile"]);
                string[] generatedArguments = await GenerateAspireLaunchArgumentsAsync(project, launchOptions, launchArguments).ConfigureAwait(true);
                generatedArguments.ShouldBe(launchArguments);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies v2 delegates to the pinned model and probes declared readiness through Dapr.</summary>
    /// <returns>The graph and invocation assertions.</returns>
    [Fact]
    public async Task PlatformPlanDelegatesToThePinnedModelAsync()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "apphost.cs")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            Assert.Skip("This integration assertion requires the enclosing Platform checkout.");
        }

        string platform = directory.FullName;
        string model = Path.Combine(platform, "src", "Hexalith.Platform.Composition", "bin", "Debug", "net10.0", "Hexalith.Platform.Composition.dll");
        if (!File.Exists(model))
        {
            Assert.Skip("Build the Platform composition model before its integration assertion.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            SourceMappingEntry[] entries =
            [
                new SourceMappingEntry("Hexalith.Platform", "source", platform),
                new SourceMappingEntry("Hexalith.EventStore", "source", Path.Combine(platform, "references", "Hexalith.EventStore")),
                new SourceMappingEntry("Hexalith.Tenants", "source", Path.Combine(platform, "references", "Hexalith.Tenants")),
                new SourceMappingEntry("Hexalith.Memories", "source", Path.Combine(platform, "references", "Hexalith.Memories")),
            ];
            SourceMapping mapping = new(WorkspaceMode.Source, platform, "Hexalith.Platform", entries, false);
            CompositionRunPlan plan = sample with
            {
                SourceMapping = mapping,
                Ports = CompositionPortAllocator.Allocate(1),
                PlatformDeclarationPaths = [Path.Combine(platform, "references", "Hexalith.EventStore", "module", "hexalith.module-manifest.v2.json")],
                PlatformModelAssemblyPath = model,
            };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            });
            _ = builder.AddDapr(options => options.DaprPath = Path.Combine(root, "dapr-home", "tools", "dapr"));
            await CompositionDaprComponentRenderer.WriteAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true);

            RunTopology.Compose(builder, plan, CompositionSigningKey.Create());

            builder.Resources.Count(resource => resource.Name == "eventstore").ShouldBe(1);
            builder.Resources.ShouldNotContain(resource => resource.Name == "eventstore-2" || resource.Name.Contains("admin", StringComparison.Ordinal));
            builder.Resources.Count(resource => resource.Annotations.Any(annotation => annotation.GetType().Name == "PlatformServerAnnotation")).ShouldBe(6);
            builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == "eventstore")
                .Annotations.OfType<ExecutableAnnotation>().Last().WorkingDirectory.ShouldBe(Path.Combine(platform, "references", "Hexalith.EventStore", "src", "Hexalith.EventStore"));
            DistributedApplicationModel applicationModel = new(builder.Resources);
            IReadOnlyList<(string Resource, string AppId, string Endpoint)> probes = RunReadiness.InvocationProbes(plan, applicationModel);
            probes.Count.ShouldBe(5);
            probes.ShouldAllBe(probe => probe.Endpoint == "/ready");
            using HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{plan.Ports.EventStoreDaprHttp}/");
            listener.Start();
            List<string> invoked = [];
            Task responder = Task.Run(
                async () =>
                {
                    for (int index = 0; index < probes.Count; index++)
                    {
                        HttpListenerContext context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        invoked.Add(context.Request.RawUrl!);
                        context.Response.StatusCode = 200;
                        context.Response.Close();
                    }
                },
                TestContext.Current.CancellationToken);
            (await RunReadiness.WaitForModuleInvocationAsync(plan, applicationModel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeTrue();
            await responder.ConfigureAwait(true);
            invoked.Order(StringComparer.Ordinal).ShouldBe(probes.Select(probe => $"/v1.0/invoke/{probe.AppId}/method/ready").Order(StringComparer.Ordinal));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static async Task<string[]> GenerateAspireLaunchArgumentsAsync(ProjectResource project, DistributedApplicationOptions launchOptions, string[] resourceArguments)
    {
        // Aspire 13.6.1 keeps its launch-plan recipe and context internal. Exercise
        // that actual recipe so appended AppHost options cannot escape this test.
        Assembly aspire = typeof(ExecutableAnnotation).Assembly;
        Type recipeType = aspire.GetType("Aspire.Hosting.ApplicationModel.ProjectExecutableLaunchRecipe", throwOnError: true)!;
        Type decisionType = aspire.GetType("Aspire.Hosting.ApplicationModel.ExecutableLaunchDecision", throwOnError: true)!;
        Type contextType = aspire.GetType("Aspire.Hosting.ApplicationModel.ExecutableLaunchContext", throwOnError: true)!;
        Type mechanismType = aspire.GetType("Aspire.Hosting.ApplicationModel.ExecutableLaunchMechanism", throwOnError: true)!;
        object[] decisionArguments = [Enum.Parse(mechanismType, "Process"), "Run", null!, false, null!];
        object decision = Activator.CreateInstance(
            decisionType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            decisionArguments,
            null)!;
        object[] contextArguments =
        [
            project,
            new ConfigurationBuilder().Build(),
            launchOptions,
            new LaunchExecutionConfiguration(resourceArguments),
            decision,
            NullLogger.Instance,
            TestContext.Current.CancellationToken,
        ];
        object context = Activator.CreateInstance(
            contextType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null,
            contextArguments,
            null)!;
        object recipe = recipeType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
        object pending = recipeType.GetMethod("CreateLaunchPlanAsync", BindingFlags.Public | BindingFlags.Instance)!.Invoke(recipe, [context])!;
        await ((Task)pending).ConfigureAwait(true);
        object plan = pending.GetType().GetProperty("Result")!.GetValue(pending)!;
        IEnumerable<string> arguments = (IEnumerable<string>)plan.GetType().GetProperty("Arguments")!.GetValue(plan)!;
        return [.. arguments];
    }

    private sealed class LaunchExecutionConfiguration(string[] arguments) : IExecutionConfigurationResult
    {
        public IEnumerable<object> References => [];

        public IEnumerable<(object Unprocessed, string Processed, bool IsSensitive)> ArgumentsWithUnprocessed =>
            arguments.Select(argument => ((object)argument, argument, false));

        public IEnumerable<(string Value, bool IsSensitive)> Arguments => arguments.Select(argument => (argument, false));

        public IEnumerable<KeyValuePair<string, (object Unprocessed, string Processed)>> EnvironmentVariablesWithUnprocessed => [];

        public IEnumerable<KeyValuePair<string, string>> EnvironmentVariables => [];

        public IEnumerable<IExecutionConfigurationData> AdditionalConfigurationData => [];

        public Exception? Exception => null;
    }
}
