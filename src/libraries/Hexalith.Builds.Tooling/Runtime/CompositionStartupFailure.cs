// <copyright file="CompositionStartupFailure.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// A metadata-only reason the AppHost could not write its readiness document.
/// </summary>
/// <param name="Schema">The document schema.</param>
/// <param name="RunId">The run that failed.</param>
/// <param name="RuleId">The stable diagnostic identity.</param>
/// <param name="Resource">The stalled resource or probe.</param>
/// <param name="AppId">The Dapr app ID, or a dash when the probe has none.</param>
/// <param name="Status">The last observed, bounded probe status.</param>
public sealed record CompositionStartupFailure(
    string Schema,
    string RunId,
    string RuleId,
    string Resource,
    string AppId,
    string Status)
{
    /// <summary>The supported startup failure schema.</summary>
    public const string SupportedSchema = "hexalith.g4-startup-failure.v1";

    /// <summary>
    /// Gets the failure document path in a run workspace.
    /// </summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The absolute document path.</returns>
    public static string PathFor(string workspace) => Path.Combine(workspace, "startup-failure.json");
}
