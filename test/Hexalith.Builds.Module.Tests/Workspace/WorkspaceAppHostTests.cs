// <copyright file="WorkspaceAppHostTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Net;
using System.Reflection;
using System.Security.Cryptography;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using CommunityToolkit.Aspire.Hosting.Dapr;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>Verifies composed AppHost module projects receive the runner's mapping settings.</summary>
public sealed class WorkspaceAppHostTests
{
    /// <summary>Verifies an edited v2 declaration cannot be composed under the signed hash.</summary>
    [Fact]
    public void PlatformDeclarationHashRefusesAnEditedSource()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string declaration = Path.Combine(root, "module.json");
            File.WriteAllText(declaration, "{\"schema\":\"hexalith.module-manifest.v2\"}");
            string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(declaration)));
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { PlatformDeclarationPaths = [declaration], PlatformDeclarationHash = hash };
            RunTopology.VerifyPlatformDeclaration(plan);
            File.AppendAllText(declaration, " ");
            Should.Throw<InvalidDataException>(() => RunTopology.VerifyPlatformDeclaration(plan)).Message.ShouldContain(declaration);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies package composition chooses the retained EventStore host seed from Platform.</summary>
    [Fact]
    public void PackageCompositionSelectsTheSingleEventStoreSeed()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.Platform", [], false)
            {
                PlatformIdentity = new PlatformIdentity("commit", "commit", "commit", root, null, "pin", null, "identity", true),
            };
            CompositionRunPlan plan = sample with { SourceMapping = mapping };
            string seed = RunTopology.ResolvePackageEventStoreSeed(plan, Path.Combine(root, "unpacked-host"));
            seed.ShouldBe(Path.Combine(root, "references", "Hexalith.Builds", "src", "hosts", "Hexalith.Builds.Module.EventStoreHost", "Hexalith.Builds.Module.EventStoreHost.csproj"));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies every composed module project receives mapped imports and the selected launch configuration.</summary>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="debugAttach">Whether source debugging is requested.</param>
    /// <param name="configuration">The expected launch configuration.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, false, "Debug")]
    [InlineData(WorkspaceMode.Package, false, "Release")]
    [InlineData(WorkspaceMode.Source, true, "Debug")]
    [InlineData(WorkspaceMode.Package, true, "Debug")]
    public async Task ComposedModulesReceiveMappingSettingsAsync(WorkspaceMode mode, bool debugAttach, string configuration)
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
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(sample.RunId, sample.Workspace, sample.DaprHome, sample.Ports, sample.Modules, sample.UiMarkers, mapping) with { DebugAttach = debugAttach };
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
            RunReadiness.InvocationProbes(plan, new DistributedApplicationModel(builder.Resources))
                .ShouldBe([.. plan.Modules.Select(module => (module.ModuleId, module.AppId, "/alive"))]);

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
                environment["Configuration"].ShouldBe(mode == WorkspaceMode.Source ? "Debug" : "Release");
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
    /// <exception cref="InvalidOperationException">The selected mapping has no materialized props import.</exception>
    [Fact]
    public async Task PlatformPlanDelegatesToThePinnedModelAsync()
    {
        SourceMapping? verifiedMapping = null;
        string platform;
        string model;
        if (Environment.GetEnvironmentVariable("HEXALITH_SOURCE_MAPPING_HASH") is string mappingHash)
        {
            string import = Environment.GetEnvironmentVariable("CustomAfterMicrosoftCommonProps")
                ?? throw new InvalidOperationException("The selected source mapping has no materialized props import.");
            verifiedMapping = SourceMapping.LoadVerified(Path.Combine(Path.GetDirectoryName(import)!, "mapping.json"), mappingHash);
            if (verifiedMapping.Mode == WorkspaceMode.Package)
            {
                Assert.Skip("The mapped source-model assertion does not apply to package mode; its model project reference is excluded.");
            }

            verifiedMapping.Mode.ShouldBe(WorkspaceMode.Source);
            verifiedMapping.HasGit.ShouldBeTrue();
            SourceMappingEntry entry = verifiedMapping.Entries.Single(entry => entry.Identity == "Hexalith.Platform");
            entry.Origin.ShouldBe("source");
            platform = entry.Path.ShouldNotBeNull();
            model = Path.Combine(AppContext.BaseDirectory, "Hexalith.Platform.Composition.dll");
            File.Exists(model).ShouldBeTrue("The mapped Platform model must be built and copied by the original test project.");
        }
        else
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "apphost.cs")))
            {
                directory = directory.Parent;
            }

            if (directory is null)
            {
                Assert.Skip("This integration assertion requires the enclosing Platform checkout or a verified source mapping.");
            }

            platform = directory.FullName;
            model = Path.Combine(platform, "src", "Hexalith.Platform.Composition", "bin", "Debug", "net10.0", "Hexalith.Platform.Composition.dll");
            if (!File.Exists(model))
            {
                Assert.Skip("Build the Platform composition model before its integration assertion.");
            }
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
            SourceMapping mapping = verifiedMapping ?? new(WorkspaceMode.Source, platform, "Hexalith.Platform", entries, false);
            string eventStore = mapping.Entries.Single(entry => entry.Identity == "Hexalith.EventStore").Path!;
            string declaration = Path.Combine(eventStore, "module", "hexalith.module-manifest.v2.json");
            string declarationHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(declaration, TestContext.Current.CancellationToken).ConfigureAwait(true)));
            CompositionRunPlan plan = sample with
            {
                SourceMapping = mapping,
                Ports = CompositionPortAllocator.Allocate(1),
                PlatformDeclarationPaths = [declaration],
                PlatformDeclarationHash = declarationHash,
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
                .Annotations.OfType<ExecutableAnnotation>().Last().WorkingDirectory.ShouldBe(Path.Combine(eventStore, "src", "Hexalith.EventStore"));
            DistributedApplicationModel applicationModel = new(builder.Resources);
            IReadOnlyList<(string Resource, string AppId, string Endpoint)> probes = RunReadiness.InvocationProbes(plan, applicationModel);
            probes.Count.ShouldBe(5);
            probes.ShouldAllBe(probe => probe.Endpoint == "/ready");
            using HttpListener listener = new();
            int[] sidecarPorts = [.. builder.Resources.Where(resource => resource.Annotations.Any(annotation => annotation.GetType().Name == "PlatformServerAnnotation"))
                .Select(resource => resource.Annotations.OfType<DaprSidecarAnnotation>().Single().Sidecar.Annotations.OfType<DaprSidecarOptionsAnnotation>().Single().Options.DaprHttpPort!.Value),
            ];
            foreach (int port in sidecarPorts)
            {
                listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            }

            listener.Start();
            List<string> invoked = [];
            Task responder = Task.Run(
                async () =>
                {
                    for (int index = 0; index < sidecarPorts.Length; index++)
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
            invoked.Count.ShouldBe(sidecarPorts.Length);
            invoked.ShouldAllBe(endpoint => endpoint == "/v1.0/healthz/outbound");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies v2 invocation readiness in a standalone Builds checkout.</summary>
    /// <returns>The annotation and real HTTP invocation assertions.</returns>
    [Fact]
    public async Task PlatformInvocationProbesUseLocalAnnotationContractAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { PlatformDeclarationPaths = ["module-v2.json"] };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            });
            _ = builder.AddDapr(options => options.DaprPath = Path.Combine(root, "dapr"));
            _ = builder.AddExecutable("eventstore", "unused", root)
                .WithAnnotation(new PlatformServerAnnotation(new { ReadinessEndpoint = "/ready" }))
                .WithDaprSidecar(sidecar => sidecar.WithOptions(new DaprSidecarOptions { AppId = "eventstore", DaprHttpPort = plan.Ports.EventStoreDaprHttp }));
            _ = builder.AddExecutable("selected", "unused", root)
                .WithAnnotation(new PlatformServerAnnotation(new { ReadinessEndpoint = "/declared-ready" }))
                .WithDaprSidecar(sidecar => sidecar.WithOptions(new DaprSidecarOptions { AppId = "selected-app", DaprHttpPort = plan.Ports.EventStoreDaprHttp }));
            _ = builder.AddExecutable("unrelated", "unused", root);
            DistributedApplicationModel model = new(builder.Resources);
            RunReadiness.InvocationProbes(plan, model).ShouldBe([("selected", "selected-app", "/declared-ready")]);
            using HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{plan.Ports.EventStoreDaprHttp}/");
            listener.Start();
            Task responder = Task.Run(
                async () =>
                {
                    for (int index = 0; index < 2; index++)
                    {
                        HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                        request.Request.RawUrl.ShouldBe("/v1.0/healthz/outbound");
                        request.Response.StatusCode = 204;
                        request.Response.Close();
                    }
                },
                TestContext.Current.CancellationToken);
            (await RunReadiness.WaitForModuleInvocationAsync(plan, model, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeTrue();
            await responder.ConfigureAwait(true);
            builder.Resources.Single(resource => resource.Name == "eventstore").Annotations.Clear();
            Should.Throw<InvalidDataException>(() => RunReadiness.InvocationProbes(plan, model)).Message.ShouldContain("no annotated eventstore gateway");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies sidecar refusal retains resource, app identity and the last HTTP or connection reason.</summary>
    /// <param name="status">The last sidecar failure reason.</param>
    /// <returns>The structured failure assertions.</returns>
    [Theory]
    [InlineData("HTTP 503")]
    [InlineData("connection-unavailable")]
    [InlineData("request-timeout")]
    public async Task PlatformSidecarTimeoutWritesStructuredFailureAsync(string status)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { PlatformDeclarationPaths = ["module-v2.json"] };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder();
            _ = builder.AddExecutable("selected", "unused", root)
                .WithAnnotation(new PlatformServerAnnotation(new { Environment = new Dictionary<string, string>() }))
                .WithDaprSidecar(sidecar => sidecar.WithOptions(new DaprSidecarOptions { AppId = "selected-app", DaprHttpPort = plan.Ports.EventStoreDaprHttp }));
            _ = builder.AddExecutable("other-unready", "unused", root);
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            using HttpListener listener = new();
            Task response = Task.CompletedTask;
            if (status != "connection-unavailable")
            {
                listener.Prefixes.Add($"http://127.0.0.1:{plan.Ports.EventStoreDaprHttp}/");
                listener.Start();
                response = Task.Run(
                    async () =>
                    {
                        HttpListenerContext request = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                        if (status == "HTTP 503")
                        {
                            request.Response.StatusCode = 503;
                            request.Response.Close();
                        }
                    },
                    TestContext.Current.CancellationToken);
            }

            (await RunReadiness.WaitForPlatformSidecarsAsync(
                plan,
                new DistributedApplicationModel(builder.Resources),
                TestContext.Current.CancellationToken,
                TimeSpan.FromMilliseconds(500),
                notifications).ConfigureAwait(true)).ShouldBeFalse();
            await response.ConfigureAwait(true);
            CompositionStartupFailure failure = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            failure.RuleId.ShouldBe("HXR026");
            failure.Resource.ShouldBe("selected");
            failure.AppId.ShouldBe("selected-app");
            failure.Status.ShouldBe(status + ";probe-timeout");
            failure.UnreadyResources.Select(resource => resource.Name).ShouldBe(["other-unready", "selected"]);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies unavailable model diagnostics retain the exact selected path.</summary>
    [Fact]
    public void MissingPlatformModelNamesItsPath()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string declaration = Path.Combine(root, "module.json");
            File.WriteAllText(declaration, "{}");
            string missing = Path.Combine(root, "missing", "Hexalith.Platform.Composition.dll");
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                PlatformDeclarationPaths = [declaration],
                PlatformDeclarationHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(declaration))),
                PlatformModelAssemblyPath = missing,
            };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            });
            Should.Throw<FileNotFoundException>(() => RunTopology.Compose(builder, plan, CompositionSigningKey.Create())).Message.ShouldContain(missing);
            builder.Resources.ShouldBeEmpty();
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
