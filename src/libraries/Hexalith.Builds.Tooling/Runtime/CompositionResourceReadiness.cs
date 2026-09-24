// <copyright file="CompositionResourceReadiness.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The observed readiness of one composed resource.
/// </summary>
/// <param name="Name">The Aspire resource name.</param>
/// <param name="Kind">The resource kind (project, container, or executable).</param>
/// <param name="State">The observed health state.</param>
public sealed record CompositionResourceReadiness(string Name, string Kind, string State);