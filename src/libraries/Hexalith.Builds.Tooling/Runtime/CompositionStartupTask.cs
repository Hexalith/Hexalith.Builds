// <copyright file="CompositionStartupTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// One per-start verification task that must succeed before a run is ready.
/// </summary>
/// <param name="ModuleId">The declaring module.</param>
/// <param name="Name">The task name.</param>
/// <param name="ExecutablePath">The absolute executable path, or empty when it could not be resolved.</param>
/// <param name="Arguments">The validated argument list.</param>
public sealed record CompositionStartupTask(
    string ModuleId,
    string Name,
    string ExecutablePath,
    IReadOnlyList<string> Arguments)
{
    /// <summary>Gets the safe executable-path validation cause when resolution failed.</summary>
    public string? ExecutableError { get; init; }
}
