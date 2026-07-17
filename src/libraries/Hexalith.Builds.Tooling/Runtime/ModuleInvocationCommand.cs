// <copyright file="ModuleInvocationCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Defines supported module runner commands.
/// </summary>
public enum ModuleInvocationCommand
{
    /// <summary>Starts a supported runtime composition.</summary>
    Run = 0,

    /// <summary>Tears down a supported runtime composition.</summary>
    Down = 1,

    /// <summary>Runs a named qualification profile.</summary>
    Test = 2,
}