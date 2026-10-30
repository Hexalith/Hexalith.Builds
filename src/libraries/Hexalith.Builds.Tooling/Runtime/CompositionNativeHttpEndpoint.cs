// <copyright file="CompositionNativeHttpEndpoint.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Metadata for an owner-observed invocation URI backed by one actually allocated sidecar.</summary>
/// <param name="ResourceId">The owning application resource.</param>
/// <param name="ModuleId">The exact declaring module.</param>
/// <param name="EndpointName">The explicit resource-local invocation endpoint name.</param>
/// <param name="Uri">The actual allocated Dapr HTTP origin and declared application invocation prefix.</param>
/// <param name="SidecarResourceId">The actual sidecar resource whose HTTP allocation supplies the origin.</param>
public sealed record CompositionNativeHttpEndpoint(string ResourceId, string ModuleId, string EndpointName, Uri Uri, string SidecarResourceId);
