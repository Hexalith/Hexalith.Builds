// <copyright file="CompositionDaprSidecarPorts.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Ports reserved for a run-scoped Dapr sidecar.
/// </summary>
/// <param name="Http">The Dapr HTTP API port.</param>
/// <param name="Grpc">The Dapr gRPC API port.</param>
/// <param name="InternalGrpc">The Dapr sidecar-to-sidecar gRPC port.</param>
/// <param name="Metrics">The Dapr metrics port.</param>
public sealed record CompositionDaprSidecarPorts(int Http, int Grpc, int InternalGrpc, int Metrics);
