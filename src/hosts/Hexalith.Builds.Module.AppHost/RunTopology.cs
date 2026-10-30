// <copyright file="RunTopology.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Globalization;
using System.Security.Cryptography;

using Aspire.Hosting.Lifecycle;

using CommunityToolkit.Aspire.Hosting.Dapr;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;
using Hexalith.EventStore.Aspire;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Composes the run-scoped topology described by one run plan.
/// </summary>
internal static class RunTopology
{
    /// <summary>The EventStore resource name.</summary>
    public const string EventStoreName = "eventstore";

    /// <summary>The optional second EventStore resource name.</summary>
    public const string SecondEventStoreName = "eventstore-2";

    /// <summary>The FrontComposer UI host resource name.</summary>
    public const string UiName = "ui";

    private const string _redisHealthCheck = "g4-redis";

    /// <summary>
    /// Adds every run resource to the builder.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="plan">The run plan.</param>
    /// <param name="signingKey">The per-run signing key.</param>
    /// <exception cref="InvalidOperationException">The optional peer has incomplete ports.</exception>
    public static void Compose(IDistributedApplicationBuilder builder, CompositionRunPlan plan, string signingKey)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);

        if (plan.IsPlatformComposition)
        {
            ComposePlatform(builder, plan, signingKey);
            return;
        }

        IResourceBuilder<ParameterResource> key = builder.AddParameter("g4-signing-key", () => signingKey, secret: true);
        string placementAddress = Loopback(plan.Ports.Placement);
        string schedulerAddress = Loopback(plan.Ports.Scheduler);

        _ = builder.Services.AddHealthChecks().AddCheck(_redisHealthCheck, new RedisPingHealthCheck(plan.Ports.Redis));

        // The plan pins Redis by immutable digest; the tag is kept only for readability.
        string[] reference = plan.RedisImage.Split('@', 2);
        string[] image = reference[0].Split(':', 2);
        IResourceBuilder<ContainerResource> redis = builder.AddContainer("redis", image[0], image[1])
            .WithImageSHA256(reference[1]["sha256:".Length..])
            .WithContainerName(plan.RedisContainerName)
            .WithContainerRuntimeArgs("--label", $"{CompositionEnvironment.ContainerRunLabel}={plan.RunId}")
            .WithEndpoint(port: plan.Ports.Redis, targetPort: 6379, scheme: "tcp", name: "tcp", isProxied: false)
            .WithHealthCheck(_redisHealthCheck);

        string controlPlane = Path.Combine(plan.Workspace, "dapr", "control-plane");
        _ = Directory.CreateDirectory(controlPlane);
        string[] placementArguments =
        [
            "--port", Text(plan.Ports.Placement),
            "--listen-address", "127.0.0.1",
            "--healthz-port", Text(plan.Ports.PlacementHealth),
            "--healthz-listen-address", "127.0.0.1",
            "--metrics-port", Text(plan.Ports.PlacementMetrics),
            "--metrics-listen-address", "127.0.0.1",
            "--initial-cluster", "dapr-placement-0=127.0.0.1:" + Text(plan.Ports.PlacementRaft),
        ];
        IResourceBuilder<ExecutableResource> placement = builder.AddExecutable("placement", RunPlanSource.DaprBinaryPath(plan, "placement"), controlPlane, placementArguments)
            .WithEnvironment(CompositionEnvironment.RunId, plan.RunId)
            .WithHttpEndpoint(port: plan.Ports.PlacementHealth, name: "healthz", isProxied: false)
            .WithHttpHealthCheck("/healthz", endpointName: "healthz");
        string[] schedulerArguments =
        [
            "--port", Text(plan.Ports.Scheduler),
            "--listen-address", "127.0.0.1",
            "--override-broadcast-host-port", schedulerAddress,
            "--healthz-port", Text(plan.Ports.SchedulerHealth),
            "--healthz-listen-address", "127.0.0.1",
            "--metrics-port", Text(plan.Ports.SchedulerMetrics),
            "--metrics-listen-address", "127.0.0.1",
            "--etcd-data-dir", Path.Combine(controlPlane, "scheduler-data"),
            "--etcd-client-port", Text(plan.Ports.SchedulerEtcdClient),
            "--etcd-client-listen-address", "127.0.0.1",
            "--etcd-initial-cluster", "dapr-scheduler-server-0=http://127.0.0.1:" + Text(plan.Ports.SchedulerEtcdPeer),
        ];
        IResourceBuilder<ExecutableResource> scheduler = builder.AddExecutable("scheduler", RunPlanSource.DaprBinaryPath(plan, "scheduler"), controlPlane, schedulerArguments)
            .WithEnvironment(CompositionEnvironment.RunId, plan.RunId)
            .WithHttpEndpoint(port: plan.Ports.SchedulerHealth, name: "healthz", isProxied: false)
            .WithHttpHealthCheck("/healthz", endpointName: "healthz");

        IResourceBuilder<ProjectResource> eventStore = ConfigureEventStore(
            AddEventStoreProject(builder, plan, EventStoreName),
            plan,
            plan.Ports.EventStoreHttp,
            key,
            redis,
            placement,
            scheduler);

        HexalithEventStoreResources platform = builder.AddHexalithEventStore(
            eventStore,
            adminServer: null,
            adminUI: null,
            eventStoreDaprConfigPath: plan.DaprConfigPath,
            adminServerDaprConfigPath: null,
            resiliencyConfigPath: null,
            stateStoreComponentPath: plan.StateStoreComponentPath,
            eventStoreDaprHttpPort: plan.Ports.EventStoreDaprHttp,
            daprPlacementHostAddress: placementAddress,
            daprSchedulerHostAddress: schedulerAddress,
            pubSubComponentPath: plan.PubSubComponentPath);
        ConfigureSidecar(eventStore.Resource, new CompositionDaprSidecarPorts(
            plan.Ports.EventStoreDaprHttp,
            plan.Ports.EventStoreDaprGrpc,
            plan.Ports.EventStoreDaprInternalGrpc,
            plan.Ports.EventStoreDaprMetrics));

        if (plan.Ports.SecondEventStoreHttp is int secondHttp && plan.Ports.SecondEventStoreDapr is { } secondPorts)
        {
            IResourceBuilder<ProjectResource> second = ConfigureEventStore(
                AddEventStoreProject(builder, plan, SecondEventStoreName),
                plan,
                secondHttp,
                key,
                redis,
                placement,
                scheduler);
            _ = second.WithDaprSidecar(sidecar =>
                sidecar.WithOptions(new DaprSidecarOptions
                {
                    AppId = EventStoreName,
                    DaprHttpPort = secondPorts.Http,
                    Config = plan.DaprConfigPath,
                    PlacementHostAddress = placementAddress,
                    SchedulerHostAddress = schedulerAddress,
                })
                .WithReference(platform.StateStore)
                .WithReference(platform.PubSub));
            ConfigureSidecar(second.Resource, secondPorts);
        }
        else if (plan.Ports.SecondEventStoreHttp is not null || plan.Ports.SecondEventStoreDapr is not null)
        {
            throw new InvalidOperationException("A second EventStore requires its HTTP and sidecar ports together.");
        }

        for (int index = 0; index < plan.Modules.Count; index++)
        {
            CompositionRunModule module = plan.Modules[index];
            if (plan.SourceMapping?.ContainsProject(module.ProjectPath) == false)
            {
                throw new InvalidOperationException($"HXW006: module project '{module.ProjectPath}' is outside active module '{plan.SourceMapping?.ActiveModule}' and mapped source roots.");
            }

            IResourceBuilder<ProjectResource> moduleProject = Tag(builder.AddProject(module.ModuleId, module.ProjectPath), plan, plan.Ports.ModuleHttp[index])
                .WithHttpHealthCheck("/alive")
                .AddEventStoreDomainModule(
                    platform,
                    module.AppId,
                    plan.DaprConfigPath,
                    plan.IsolatedResourcesPath,
                    placementAddress,
                    schedulerAddress);
            if (plan.SourceMapping is not null)
            {
                // Aspire otherwise passes the AppHost assembly configuration as a
                // global dotnet-run option, which can disagree with this mapping.
                _ = moduleProject
                    .WithAnnotation(new ExecutableAnnotation
                    {
                        Command = "dotnet",
                        WorkingDirectory = Path.GetDirectoryName(module.ProjectPath)!,
                    })
                    .WithArgs("run", "--project", module.ProjectPath, "--configuration", CompositionLaunchConfiguration.ProjectConfiguration(plan.SourceMapping.Mode, plan.DebugAttach), "--no-launch-profile");
                foreach (KeyValuePair<string, string> pair in Hexalith.Builds.Tooling.Workspace.SourceMappingMaterializer.Environment(plan.SourceMapping, plan.Workspace))
                {
                    _ = moduleProject.WithEnvironment(pair.Key, pair.Value);
                }
            }

            ConfigureSidecar(moduleProject.Resource, plan.Ports.ModuleDapr[index]);
        }

        if (plan.UiMarkers.Count > 0)
        {
            IResourceBuilder<ProjectResource> ui = Tag(AddUiProject(builder, plan), plan, plan.Ports.UiHttp)
                .WithHttpHealthCheck("/health");
            for (int index = 0; index < plan.UiMarkers.Count; index++)
            {
                string prefix = $"Hexalith__G4__UiMarkers__{Text(index)}__";
                _ = ui
                    .WithEnvironment(prefix + "Assembly", plan.UiMarkers[index].AssemblyPath)
                    .WithEnvironment(prefix + "Type", plan.UiMarkers[index].MarkerType);
            }
        }

        _ = builder.Services.AddSingleton<IDistributedApplicationEventingSubscriber>(new DaprDirectEndpointSubscriber());
    }

    /// <summary>Resolves the sole package-mode EventStore host seed from the packaged layout or Platform checkout.</summary>
    /// <param name="plan">The signed Platform run plan.</param>
    /// <param name="baseDirectory">The AppHost package directory.</param>
    /// <returns>The EventStore host seed project.</returns>
    /// <exception cref="InvalidDataException">The package-mode Platform checkout path is missing.</exception>
    internal static string ResolvePackageEventStoreSeed(CompositionRunPlan plan, string baseDirectory)
    {
        if (PackagedHostProject.TryMaterialize(baseDirectory, "EventStore", "EventStoreHost", HostDirectory(plan, EventStoreName), out string? packagedProject))
        {
            return packagedProject!;
        }

        string platform = plan.SourceMapping?.PlatformIdentity?.PlatformPath ?? throw new InvalidDataException("Package-mode Platform identity has no checkout path.");
        return Path.Combine(platform, "references", "Hexalith.Builds", "src", "hosts", "Hexalith.Builds.Module.EventStoreHost", "Hexalith.Builds.Module.EventStoreHost.csproj");
    }

    /// <summary>Resolves the runner-owned source host only from the command-selected Builds checkout.</summary>
    /// <param name="plan">The signed source run plan.</param>
    /// <returns>The physically contained source host project.</returns>
    /// <exception cref="InvalidDataException">The selected source host is unavailable.</exception>
    internal static string ResolveSourceEventStoreSeed(CompositionRunPlan plan)
    {
        SourceMapping mapping = plan.SourceMapping ?? throw new InvalidDataException("Source EventStore host requires the command-selected mapping.");
        SourceMappingEntry? builds = mapping.Entries.SingleOrDefault(entry => entry.Identity == "Hexalith.Builds");
        if (mapping.Mode != WorkspaceMode.Source || builds is not { Origin: "source", Path: not null })
        {
            throw new InvalidDataException("Source EventStore host requires the selected Builds source checkout.");
        }

        string project = Path.Combine(builds.Path, "src", "hosts", "Hexalith.Builds.Module.EventStoreHost", "Hexalith.Builds.Module.EventStoreHost.csproj");
        return (mapping with { Entries = [builds], PlatformIdentity = null }).ContainsProject(project)
            ? project
            : throw new InvalidDataException("The selected Builds EventStore host is missing or outside its owner checkout.");
    }

    /// <summary>Refuses an edited declaration before the Platform model can add resources.</summary>
    /// <param name="plan">The signed run plan.</param>
    /// <exception cref="InvalidDataException">The selected declaration changed or is unavailable.</exception>
    internal static void VerifyPlatformDeclaration(CompositionRunPlan plan)
    {
        if (plan.PlatformDeclarationPaths.Count != 1 || string.IsNullOrWhiteSpace(plan.PlatformDeclarationHash))
        {
            throw new InvalidDataException("Platform v2 plan has no selected declaration hash.");
        }

        string path = plan.PlatformDeclarationPaths[0];
        if (!File.Exists(path) || !string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), plan.PlatformDeclarationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Platform v2 declaration '{path}' changed after the signed run plan was created.");
        }
    }

    /// <summary>
    /// Adds an EventStore host. From a package, each resource builds its own private shim copy, so the installed
    /// tool stays read-only and parallel resources never share an obj/bin directory.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="plan">The run plan.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The project resource.</returns>
    private static IResourceBuilder<ProjectResource> AddEventStoreProject(IDistributedApplicationBuilder builder, CompositionRunPlan plan, string name) =>
        PackagedHostProject.TryMaterialize(AppContext.BaseDirectory, "EventStore", "EventStoreHost", HostDirectory(plan, name), out string? packagedProject)
            ? builder.AddProject(name, packagedProject!)
            : builder.AddProject<Projects.Hexalith_Builds_Module_EventStoreHost>(name);

    /// <summary>
    /// Adds the FrontComposer UI host, from a private shim copy when the AppHost runs from a package.
    /// </summary>
    /// <param name="builder">The distributed application builder.</param>
    /// <param name="plan">The run plan.</param>
    /// <returns>The project resource.</returns>
    private static IResourceBuilder<ProjectResource> AddUiProject(IDistributedApplicationBuilder builder, CompositionRunPlan plan) =>
        PackagedHostProject.TryMaterialize(AppContext.BaseDirectory, "Ui", "UiHost", HostDirectory(plan, UiName), out string? packagedProject)
            ? builder.AddProject(UiName, packagedProject!)
            : builder.AddProject<Projects.Hexalith_Builds_Module_UiHost>(UiName);

    private static void ComposePlatform(IDistributedApplicationBuilder builder, CompositionRunPlan plan, string signingKey)
    {
        VerifyPlatformDeclaration(plan);
        string modelPath = plan.PlatformModelAssemblyPath
            ?? throw new InvalidOperationException("Platform v2 plan has no model assembly path.");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Platform declaration model '{modelPath}' is unavailable.", modelPath);
        }

        string? seed = null;
        if (plan.SourceMapping?.Mode == WorkspaceMode.Package)
        {
            seed = ResolvePackageEventStoreSeed(plan, AppContext.BaseDirectory);
        }
        else if (plan.SelectedTest?.ModuleId == "parties")
        {
            seed = ResolveSourceEventStoreSeed(plan);
        }

        string modelDirectory = Path.GetDirectoryName(modelPath)!;
        System.Runtime.Loader.AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string dependency = Path.Combine(modelDirectory, name.Name + ".dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        };

        System.Reflection.Assembly model = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyPath(modelPath);
        System.Reflection.MethodInfo compose = model.GetType("Hexalith.Platform.Composition.PlatformAppModel", throwOnError: true)!
            .GetMethod("ComposeRun", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)!;
        try
        {
            _ = compose.Invoke(null, [builder, plan, signingKey, seed]);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
        }

        if (!builder.Resources.Any(resource => resource.Name == EventStoreName
            && resource.Annotations.Any(annotation => annotation.GetType().Name == "PlatformServerAnnotation")))
        {
            throw new InvalidDataException("Platform v2 model did not annotate its eventstore gateway; invocation readiness cannot be verified.");
        }

        if (plan.UiMarkers.Count > 0)
        {
            IResourceBuilder<ProjectResource> ui = Tag(AddUiProject(builder, plan), plan, plan.Ports.UiHttp).WithHttpHealthCheck("/health");
            for (int index = 0; index < plan.UiMarkers.Count; index++)
            {
                string prefix = $"Hexalith__G4__UiMarkers__{Text(index)}__";
                _ = ui.WithEnvironment(prefix + "Assembly", plan.UiMarkers[index].AssemblyPath).WithEnvironment(prefix + "Type", plan.UiMarkers[index].MarkerType);
            }
        }
    }

    private static string HostDirectory(CompositionRunPlan plan, string resourceName) => Path.Combine(plan.Workspace, "hosts", resourceName);

    private static IResourceBuilder<ProjectResource> ConfigureEventStore(
        IResourceBuilder<ProjectResource> project,
        CompositionRunPlan plan,
        int httpPort,
        IResourceBuilder<ParameterResource> key,
        IResourceBuilder<ContainerResource> redis,
        IResourceBuilder<ExecutableResource> placement,
        IResourceBuilder<ExecutableResource> scheduler)
    {
        IResourceBuilder<ProjectResource> eventStore = Tag(project, plan, httpPort)
            .WithHttpHealthCheck("/health")
            .WithEnvironment("Authentication__JwtBearer__Authority", string.Empty)
            .WithEnvironment("Authentication__JwtBearer__Issuer", CompositionEnvironment.TokenIssuer)
            .WithEnvironment("Authentication__JwtBearer__Audience", CompositionEnvironment.TokenAudience)
            .WithEnvironment("Authentication__JwtBearer__ValidAudiences__0", CompositionEnvironment.TokenAudience)
            .WithEnvironment("Authentication__JwtBearer__AllowedAlgorithms__0", "HS256")
            .WithEnvironment("Authentication__JwtBearer__SigningKey", key)
            .WithEnvironment("Authentication__JwtBearer__RequireHttpsMetadata", "false")
            .WaitFor(redis)
            .WaitFor(placement)
            .WaitFor(scheduler);
        foreach (CompositionRunModule module in plan.Modules)
        {
            string registration = $"EventStore__DomainServices__Registrations__wildcard_{module.Domain}_v1__";
            _ = eventStore
                .WithEnvironment(registration + "AppId", module.AppId)
                .WithEnvironment(registration + "MethodName", "process")
                .WithEnvironment(registration + "TenantId", "*")
                .WithEnvironment(registration + "Domain", module.Domain)
                .WithEnvironment(registration + "Version", "v1");
        }

        return eventStore;
    }

    private static void ConfigureSidecar(ProjectResource project, CompositionDaprSidecarPorts ports)
    {
        IDaprSidecarResource sidecar = project.Annotations.OfType<DaprSidecarAnnotation>().Single().Sidecar;
        DaprSidecarOptionsAnnotation current = sidecar.Annotations.OfType<DaprSidecarOptionsAnnotation>().Single();
        int index = sidecar.Annotations.IndexOf(current);
        sidecar.Annotations[index] = new DaprSidecarOptionsAnnotation(current.Options with
        {
            DaprHttpPort = ports.Http,
            DaprGrpcPort = ports.Grpc,
            DaprInternalGrpcPort = ports.InternalGrpc,
            MetricsPort = ports.Metrics,
        });
    }

    private static IResourceBuilder<ProjectResource> Tag(IResourceBuilder<ProjectResource> project, CompositionRunPlan plan, int httpPort)
    {
        return project
            .WithHttpEndpoint(httpPort, httpPort, name: "http", isProxied: false)
            .WithEnvironment(CompositionEnvironment.RunId, plan.RunId)
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development");
    }

    private static string Loopback(int port) => "127.0.0.1:" + Text(port);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}
