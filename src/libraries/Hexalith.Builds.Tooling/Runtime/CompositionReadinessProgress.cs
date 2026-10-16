// <copyright file="CompositionReadinessProgress.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The latest per-resource readiness observations written while startup is still in progress.
/// </summary>
/// <param name="Schema">The progress schema identity.</param>
/// <param name="RunId">The run identity.</param>
/// <param name="Resources">The latest result for every required resource.</param>
public sealed record CompositionReadinessProgress(
    string Schema,
    string RunId,
    IReadOnlyList<CompositionResourceReadiness> Resources)
{
    /// <summary>The supported progress schema identity.</summary>
    public const string SupportedSchema = "hexalith.g4-readiness-progress.v1";

    /// <summary>
    /// Gets the progress document path in a run workspace.
    /// </summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The absolute document path.</returns>
    public static string PathFor(string workspace) => Path.Combine(workspace, "readiness-progress.json");
}
