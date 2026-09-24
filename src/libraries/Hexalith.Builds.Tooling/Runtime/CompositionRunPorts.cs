// <copyright file="CompositionRunPorts.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Run-scoped host ports allocated by the runner. No module or consumer chooses them.
/// </summary>
/// <param name="Redis">The run-scoped Redis endpoint port.</param>
/// <param name="Placement">The Dapr placement gRPC port.</param>
/// <param name="PlacementHealth">The Dapr placement health port.</param>
/// <param name="PlacementMetrics">The Dapr placement metrics port.</param>
/// <param name="PlacementRaft">The Dapr placement raft peer port.</param>
/// <param name="Scheduler">The Dapr scheduler gRPC port.</param>
/// <param name="SchedulerHealth">The Dapr scheduler health port.</param>
/// <param name="SchedulerMetrics">The Dapr scheduler metrics port.</param>
/// <param name="SchedulerEtcdClient">The Dapr scheduler embedded etcd client port.</param>
/// <param name="SchedulerEtcdPeer">The Dapr scheduler embedded etcd peer port.</param>
/// <param name="EventStoreDaprHttp">The EventStore sidecar Dapr HTTP port.</param>
/// <param name="EventStoreHttp">The EventStore host HTTP port.</param>
/// <param name="UiHttp">The UI host HTTP port.</param>
/// <param name="ModuleHttp">The module host HTTP ports, in module order.</param>
/// <param name="EventStoreDaprGrpc">The EventStore sidecar gRPC API port.</param>
/// <param name="EventStoreDaprInternalGrpc">The EventStore sidecar internal gRPC port.</param>
/// <param name="EventStoreDaprMetrics">The EventStore sidecar metrics port.</param>
/// <param name="ModuleDapr">The module sidecar ports, in module order.</param>
/// <param name="SecondEventStoreHttp">The optional second EventStore host HTTP port.</param>
/// <param name="SecondEventStoreDapr">The optional second EventStore sidecar ports.</param>
public sealed record CompositionRunPorts(
    int Redis,
    int Placement,
    int PlacementHealth,
    int PlacementMetrics,
    int PlacementRaft,
    int Scheduler,
    int SchedulerHealth,
    int SchedulerMetrics,
    int SchedulerEtcdClient,
    int SchedulerEtcdPeer,
    int EventStoreDaprHttp,
    int EventStoreHttp,
    int UiHttp,
    IReadOnlyList<int> ModuleHttp,
    int EventStoreDaprGrpc,
    int EventStoreDaprInternalGrpc,
    int EventStoreDaprMetrics,
    IReadOnlyList<CompositionDaprSidecarPorts> ModuleDapr,
    int? SecondEventStoreHttp = null,
    CompositionDaprSidecarPorts? SecondEventStoreDapr = null)
{
    /// <summary>
    /// Gets every allocated port in declaration order.
    /// </summary>
    /// <returns>The allocated ports.</returns>
    public IReadOnlyList<int> ToList()
    {
        List<int> ports =
        [
            Redis,
            Placement,
            PlacementHealth,
            PlacementMetrics,
            PlacementRaft,
            Scheduler,
            SchedulerHealth,
            SchedulerMetrics,
            SchedulerEtcdClient,
            SchedulerEtcdPeer,
            EventStoreDaprHttp,
            EventStoreHttp,
            UiHttp,
            .. ModuleHttp,
            EventStoreDaprGrpc,
            EventStoreDaprInternalGrpc,
            EventStoreDaprMetrics,
            .. ModuleDapr.SelectMany(sidecar => new[] { sidecar.Http, sidecar.Grpc, sidecar.InternalGrpc, sidecar.Metrics }),
        ];
        if (SecondEventStoreHttp is int http)
        {
            ports.Add(http);
        }

        if (SecondEventStoreDapr is { } sidecar)
        {
            ports.AddRange([sidecar.Http, sidecar.Grpc, sidecar.InternalGrpc, sidecar.Metrics]);
        }

        return ports;
    }
}
