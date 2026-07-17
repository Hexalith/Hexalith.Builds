// <copyright file="ModuleRunInvocation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Captures a normalized invocation without retaining raw user filters.
/// </summary>
/// <param name="Command">The normalized public command surface.</param>
/// <param name="ManifestPath">The repository-relative manifest path when available.</param>
/// <param name="ManifestHash">The SHA-256 hash of the manifest when readable.</param>
/// <param name="Profile">The optional profile identity.</param>
/// <param name="FixturePath">The repository-relative fixture path when declared.</param>
/// <param name="FixtureHash">The SHA-256 hash of the fixture when readable.</param>
/// <param name="FilterHash">The SHA-256 hash of an optional test filter.</param>
public sealed record ModuleRunInvocation(
    string Command,
    string? ManifestPath,
    string? ManifestHash,
    string? Profile,
    string? FixturePath,
    string? FixtureHash,
    string? FilterHash);