// <copyright file="ModuleRunTopology.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Captures the validated module descriptors and platform pins without runtime endpoint data.
/// </summary>
/// <param name="Modules">The validated modules in deterministic manifest order.</param>
/// <param name="Platform">The authorization-time platform pins.</param>
public sealed record ModuleRunTopology(
    IReadOnlyList<ModuleRunModule> Modules,
    PlatformPins? Platform);