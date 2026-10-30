// <copyright file="CompositionNativeTestScope.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Nonsecret native consumer scope derived by the owner from the actually committed routing catalog.</summary>
/// <param name="RunId">The exact owning run.</param>
/// <param name="ModuleId">The selected native consumer module.</param>
/// <param name="RoutingCatalogRoot">The committed routing root, separate from the module package catalog commit.</param>
/// <param name="Domains">The exact consumed owner domains.</param>
/// <param name="Tenants">The exact consumed partition tenants.</param>
public sealed record CompositionNativeTestScope(string RunId, string ModuleId, string RoutingCatalogRoot, IReadOnlyList<string> Domains, IReadOnlyList<string> Tenants);
