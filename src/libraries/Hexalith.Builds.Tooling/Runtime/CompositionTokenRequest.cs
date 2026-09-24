// <copyright file="CompositionTokenRequest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Describes one per-run development identity token.
/// </summary>
/// <param name="Subject">The subject identity.</param>
/// <param name="Tenants">The granted tenants.</param>
/// <param name="Domains">The granted domains.</param>
/// <param name="Permissions">The granted permissions.</param>
/// <param name="GlobalAdministrator">A value indicating whether the identity is a global administrator.</param>
/// <param name="Lifetime">The token lifetime.</param>
public sealed record CompositionTokenRequest(
    string Subject,
    IReadOnlyList<string> Tenants,
    IReadOnlyList<string> Domains,
    IReadOnlyList<string> Permissions,
    bool GlobalAdministrator,
    TimeSpan Lifetime);