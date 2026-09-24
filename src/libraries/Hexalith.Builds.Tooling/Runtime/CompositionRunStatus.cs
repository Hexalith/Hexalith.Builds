// <copyright file="CompositionRunStatus.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The lifecycle status recorded in runner-owned run state.
/// </summary>
public enum CompositionRunStatus
{
    /// <summary>The AppHost is starting.</summary>
    Starting = 0,

    /// <summary>Every resource reported healthy.</summary>
    Ready = 1,

    /// <summary>The run failed and bounded teardown ran.</summary>
    Failed = 2,

    /// <summary>The run was cancelled and bounded teardown ran.</summary>
    Cancelled = 3,
}