// <copyright file="CompositionLaunchConfiguration.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Selects the project build configuration for a composed run.
/// </summary>
public static class CompositionLaunchConfiguration
{
    /// <summary>
    /// Gets the <c>dotnet run</c> configuration for source or debug launch.
    /// </summary>
    /// <param name="mode">The tool-selected workspace mode.</param>
    /// <param name="debugAttach">True when the command is <c>debug</c>.</param>
    /// <returns><c>Debug</c> for source mode and for debug attach; otherwise <c>Release</c>.</returns>
    public static string ProjectConfiguration(WorkspaceMode mode, bool debugAttach) =>
        debugAttach || mode == WorkspaceMode.Source ? "Debug" : "Release";
}
