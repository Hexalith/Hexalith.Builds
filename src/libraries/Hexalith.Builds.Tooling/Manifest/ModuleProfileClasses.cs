// <copyright file="ModuleProfileClasses.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines the supported AD-25 orchestration profile classes.
/// </summary>
public static class ModuleProfileClasses
{
    /// <summary>
    /// Gets all supported profile class identities.
    /// </summary>
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "pure-domain",
        "host-contract",
        "persisted-boundary",
        "restart",
        "two-instance",
        "authenticated-browser",
        "authenticated-cli",
        "authenticated-mcp",
    };
}