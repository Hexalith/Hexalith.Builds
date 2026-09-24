// <copyright file="CompositionRunPlanFactoryTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Globalization;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies run identities and metadata-only run-plan rendering.
/// </summary>
public sealed class CompositionRunPlanFactoryTests
{
    /// <summary>
    /// Verifies fresh run identities are unique and well-formed.
    /// </summary>
    [Fact]
    public void NewRunIdIsUniqueLowercaseHexadecimal()
    {
        string first = CompositionRunPlanFactory.NewRunId();
        string second = CompositionRunPlanFactory.NewRunId();

        CompositionRunPlanFactory.IsRunId(first).ShouldBeTrue();
        CompositionRunPlanFactory.IsRunId(second).ShouldBeTrue();
        first.ShouldNotBe(second);
        CompositionRunPlanFactory.IsRunId("0123456789ABCDEF0123456789ABCDEF").ShouldBeFalse();
        CompositionRunPlanFactory.IsRunId("../../etc/passwd").ShouldBeFalse();
        CompositionRunPlanFactory.IsRunId(null).ShouldBeFalse();
    }

    /// <summary>
    /// Verifies the plan derives every run-unique namespace and path from the run identity and orders modules deterministically.
    /// </summary>
    [Fact]
    public void CreateDerivesRunUniqueNamespacesAndDeterministicOrder()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);

            plan.Schema.ShouldBe(CompositionRunPlan.SupportedSchema);
            plan.TenantNamespace.ShouldBe("g4t-0123456789ab");
            plan.ForeignTenantNamespace.ShouldBe("g4f-0123456789ab");
            plan.ResourceNamespace.ShouldBe("g4r-0123456789ab");
            plan.TenantNamespace.ShouldNotBe(plan.ForeignTenantNamespace);
            plan.RedisContainerName.ShouldBe("hexalith-g4-" + CompositionTestFiles.RunId + "-redis");
            plan.Modules.Select(module => module.ModuleId).ShouldBe(["p0-inventory", "p0-orders"]);
            plan.UiMarkers.Select(marker => marker.ModuleId).ShouldBe(["p0-inventory", "p0-orders"]);
            plan.ReadinessPath.ShouldStartWith(plan.Workspace);
            plan.DaprConfigPath.ShouldStartWith(plan.Workspace);
            plan.StateStoreComponentPath.ShouldStartWith(plan.Workspace);
            plan.PubSubComponentPath.ShouldStartWith(plan.Workspace);
            plan.IsolatedResourcesPath.ShouldStartWith(plan.Workspace);
            plan.RedisImage.ShouldBe("docker.io/library/redis:7.4-alpine@sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies malformed identities, relative paths, empty module sets, and duplicate ports are rejected.
    /// </summary>
    [Fact]
    public void CreateRejectsMalformedInputs()
    {
        CompositionRunPorts ports = new(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, [14], 15, 16, 17, [new(18, 19, 20, 21)]);
        CompositionRunPorts duplicate = new(1, 1, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, [14], 15, 16, 17, [new(18, 19, 20, 21)]);
        CompositionRunModule module = new("m", "m", "m-app", "/m/M.csproj");
        string workspace = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "w"));

        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create("bad", workspace, workspace, ports, [module], []));
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create(CompositionTestFiles.RunId, "relative", workspace, ports, [module], []));
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create(CompositionTestFiles.RunId, workspace, workspace, ports, [], []));
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create(CompositionTestFiles.RunId, workspace, workspace, duplicate, [module], []));
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create(CompositionTestFiles.RunId, workspace, workspace, ports with { ModuleHttp = [] }, [module], []));
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Create(CompositionTestFiles.RunId, workspace, workspace, ports with { ModuleDapr = [] }, [module], []));
    }

    /// <summary>
    /// Verifies manifest modules bind to validated descriptors with the manifest-declared domain and application identity.
    /// </summary>
    [Fact]
    public void BindUsesManifestIdentitiesAndDescriptorProjects()
    {
        ModuleManifest manifest = new(
            "hexalith.module-manifest.v1",
            "sample",
            [
                new ModuleDescriptor("module-b", "b.dll", [], "domain-b", "app-b", "resource-b"),
                new ModuleDescriptor("module-a", "a.dll", [], "domain-a", "app-a", "resource-a"),
            ],
            new PlatformPins("3.106.0", "1.18.2", "1.18.8", "4.5.0"),
            new UiDescriptor("ui.dll"),
            new Dictionary<string, ModuleProfile>());
        ExecutableDescriptorLoadResult descriptors = new(
            [
                new ExecutableModuleDescriptor("module-a", "/repo/a/A.csproj", null, "/repo/ui/Ui.dll", "Acme.A"),
                new ExecutableModuleDescriptor("module-b", "/repo/b/B.csproj", null, "/repo/ui/Ui.dll", "Acme.B"),
            ],
            new ExecutableUiDescriptor(
                "/repo/ui/Ui.csproj",
                [new ExecutableUiMarker("module-b", "/repo/ui/Ui.dll", "Acme.B"), new ExecutableUiMarker("module-a", "/repo/ui/Ui.dll", "Acme.A")]),
            []);

        (IReadOnlyList<CompositionRunModule> modules, IReadOnlyList<CompositionRunUiMarker> markers) = CompositionRunPlanFactory.Bind(manifest, descriptors);

        modules.ShouldBe(
        [
            new CompositionRunModule("module-a", "domain-a", "app-a", "/repo/a/A.csproj"),
            new CompositionRunModule("module-b", "domain-b", "app-b", "/repo/b/B.csproj"),
        ]);
        markers.Select(marker => marker.MarkerType).ShouldBe(["Acme.A", "Acme.B"]);
        _ = Should.Throw<ArgumentException>(() => CompositionRunPlanFactory.Bind(
            manifest,
            descriptors with { Modules = [descriptors.Modules[0]] }));
    }

    /// <summary>
    /// Verifies distinct allocated ports for one run stay outside Linux's outbound ephemeral range.
    /// </summary>
    [Fact]
    public void PortAllocatorReturnsDistinctLoopbackPorts()
    {
        IReadOnlyList<int> ports = CompositionPortAllocator.Allocate(2).ToList();

        ports.Count.ShouldBe(26);
        ports.Distinct().Count().ShouldBe(26);
        ports.ShouldAllBe(port => port > 0 && port <= 65535);
        if (OperatingSystem.IsLinux())
        {
            string[] ephemeralRange = File.ReadAllText("/proc/sys/net/ipv4/ip_local_port_range")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            int start = int.Parse(ephemeralRange[0], NumberStyles.None, CultureInfo.InvariantCulture);
            int end = int.Parse(ephemeralRange[1], NumberStyles.None, CultureInfo.InvariantCulture);
            ports.ShouldAllBe(port => port < start || port > end);
        }
    }

    /// <summary>
    /// Verifies the optional second EventStore receives five distinct ports while ordinary runs keep their topology.
    /// </summary>
    [Fact]
    public void SecondEventStoreAllocationAddsDistinctPortsOnlyWhenRequested()
    {
        CompositionRunPorts ordinary = CompositionPortAllocator.Allocate(2);
        CompositionRunPorts twoInstance = CompositionPortAllocator.Allocate(2, includeSecondEventStore: true);

        ordinary.SecondEventStoreHttp.ShouldBeNull();
        ordinary.SecondEventStoreDapr.ShouldBeNull();
        ordinary.ToList().Count.ShouldBe(26);
        _ = twoInstance.SecondEventStoreHttp.ShouldNotBeNull();
        _ = twoInstance.SecondEventStoreDapr.ShouldNotBeNull();
        twoInstance.ToList().Count.ShouldBe(31);
        twoInstance.ToList().Distinct().Count().ShouldBe(31);
        twoInstance.ToList().Intersect(ordinary.ToList()).ShouldBeEmpty();
    }

    /// <summary>
    /// Verifies concurrent allocations in one runner never hand out the same port twice.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task ConcurrentAllocationsAreDisjointAsync()
    {
        CompositionRunPorts[] allocations = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => Task.Run(() => CompositionPortAllocator.Allocate(2), TestContext.Current.CancellationToken))).ConfigureAwait(true);

        int[] all = [.. allocations.SelectMany(allocation => allocation.ToList())];
        all.Distinct().Count().ShouldBe(all.Length);
    }
}
