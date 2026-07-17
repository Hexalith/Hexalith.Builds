// <copyright file="ToolPhase.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Identifies the stable phase that produced a diagnostic or outcome.
/// </summary>
public enum ToolPhase
{
    /// <summary>No phase has started.</summary>
    None = 0,

    /// <summary>Command-line usage parsing.</summary>
    Usage = 1,

    /// <summary>Manifest parsing and validation.</summary>
    Manifest = 2,

    /// <summary>Runtime prerequisite discovery.</summary>
    Prerequisite = 3,

    /// <summary>Distributed topology composition.</summary>
    Topology = 4,

    /// <summary>Product or test execution.</summary>
    Test = 5,

    /// <summary>Persisted-state assertion.</summary>
    PersistedState = 6,

    /// <summary>Evidence construction or validation.</summary>
    Evidence = 7,

    /// <summary>Invocation cleanup.</summary>
    Cleanup = 8,
}