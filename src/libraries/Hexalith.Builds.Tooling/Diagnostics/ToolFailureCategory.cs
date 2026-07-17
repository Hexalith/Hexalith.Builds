// <copyright file="ToolFailureCategory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Defines stable machine-readable failure categories.
/// </summary>
public enum ToolFailureCategory
{
    /// <summary>No failure occurred.</summary>
    None = 0,

    /// <summary>Command usage was invalid.</summary>
    Usage = 1,

    /// <summary>The module manifest was invalid.</summary>
    Manifest = 2,

    /// <summary>A required prerequisite was unavailable.</summary>
    PrerequisiteUnavailable = 3,

    /// <summary>Topology or lifecycle management failed.</summary>
    TopologyOrLifecycle = 4,

    /// <summary>A product or test assertion failed.</summary>
    ProductOrTest = 5,

    /// <summary>A persisted-state assertion failed.</summary>
    PersistedState = 6,

    /// <summary>Evidence syntax or schema was invalid.</summary>
    EvidenceSchema = 7,

    /// <summary>Evidence policy validation failed.</summary>
    EvidencePolicy = 8,

    /// <summary>The invocation was cancelled.</summary>
    Cancelled = 9,
}