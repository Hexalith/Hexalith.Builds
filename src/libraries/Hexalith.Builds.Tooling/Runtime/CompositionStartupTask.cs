// <copyright file="CompositionStartupTask.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json.Serialization;

/// <summary>
/// One declared startup task that must succeed before a run is ready.
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
    /// <summary>Gets the declared task scope.</summary>
    public string Scope { get; init; } = CompositionStartupTasks.PerStartVerifyScope;

    /// <summary>Gets the declaring repository used as the task working directory.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>Gets the safe executable-path validation cause when resolution failed.</summary>
    public string? ExecutableError { get; init; }

    /// <summary>
    /// Gets the private run-workspace path of the invocation document the composition model writes for an
    /// environment-creation task, or null for a task that takes no invocation.
    /// </summary>
    /// <remarks>The runner passes this path, never the document, as the task's invocation variable.</remarks>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InvocationPath { get; init; }
}
