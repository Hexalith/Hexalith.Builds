// <copyright file="ToolExitCode.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Defines the stable process exit codes exposed by the Builds tools.
/// </summary>
public enum ToolExitCode
{
    /// <summary>The command completed successfully.</summary>
    Success = 0,

    /// <summary>Usage or manifest validation failed.</summary>
    UsageOrManifest = 1,

    /// <summary>A required prerequisite was unavailable.</summary>
    PrerequisiteUnavailable = 2,

    /// <summary>Topology or lifecycle execution failed.</summary>
    TopologyOrLifecycle = 3,

    /// <summary>A product assertion or test step failed.</summary>
    ProductOrTest = 4,

    /// <summary>A persisted-state assertion failed.</summary>
    PersistedState = 5,

    /// <summary>Evidence parsing, schema, or policy validation failed.</summary>
    EvidenceSchemaOrPolicy = 6,

    /// <summary>The command was cancelled.</summary>
    Cancelled = 130,
}