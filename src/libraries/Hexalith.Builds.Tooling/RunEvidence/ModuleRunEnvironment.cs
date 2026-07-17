// <copyright file="ModuleRunEnvironment.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Captures non-secret environment metadata for a run artifact.
/// </summary>
/// <param name="RepositoryRevision">The resolved source revision or <c>unavailable</c>.</param>
/// <param name="RepositoryDirtyMarker">The dirty-state marker without a raw source-control transcript.</param>
/// <param name="SdkVersion">The executing .NET SDK/runtime version.</param>
/// <param name="OperatingSystem">The operating-system descriptor.</param>
/// <param name="ToolVersion">The module tool assembly version.</param>
public sealed record ModuleRunEnvironment(
    string RepositoryRevision,
    string RepositoryDirtyMarker,
    string SdkVersion,
    string OperatingSystem,
    string ToolVersion);