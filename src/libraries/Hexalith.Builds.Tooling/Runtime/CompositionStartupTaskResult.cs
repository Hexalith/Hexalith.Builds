// <copyright file="CompositionStartupTaskResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The observed outcome of one required per-start verification task.</summary>
/// <param name="TaskId">The owner-qualified task identity.</param>
/// <param name="Status">The observed pending, running, passed, failed or cancelled status.</param>
/// <param name="StartedAt">The actual start time, when started.</param>
/// <param name="FinishedAt">The actual finish time, when finished.</param>
public sealed record CompositionStartupTaskResult(string TaskId, string Status, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt);
