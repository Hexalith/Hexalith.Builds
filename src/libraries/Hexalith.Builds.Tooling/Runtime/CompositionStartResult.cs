// <copyright file="CompositionStartResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// The outcome of starting one run.
/// </summary>
/// <param name="Result">The stable status, outcome, and diagnostics.</param>
/// <param name="RunId">The run identity, when one was allocated.</param>
/// <param name="Session">The ready session, only when the run started.</param>
public sealed record CompositionStartResult(ToolCommandResult Result, string? RunId, CompositionRunSession? Session)
{
    /// <summary>
    /// Gets a value indicating whether the run is ready.
    /// </summary>
    public bool IsReady => Session is not null && Result.Outcome.ExitCode == ToolExitCode.Success;
}