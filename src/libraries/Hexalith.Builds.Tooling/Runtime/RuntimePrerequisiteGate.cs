// <copyright file="RuntimePrerequisiteGate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Prevents a live platform run until its independently owned compatibility disposition is recorded.
/// </summary>
/// <remarks>
/// The manifest pin validates the accepted P1 package selection. It cannot itself resolve G-6,
/// the Dapr runtime-to-SDK support disposition. Keeping this gate in the Builds runner makes an affected
/// invocation explicitly unavailable instead of treating an absent approval as a skipped or passing lane.
/// </remarks>
public static class RuntimePrerequisiteGate
{
    /// <summary>
    /// Checks whether a validated manifest may enter the live Aspire composition phase.
    /// </summary>
    /// <param name="manifest">The already validated module manifest.</param>
    /// <returns>A fail-closed prerequisite decision.</returns>
    public static RuntimePrerequisiteCheck Check(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        ToolDiagnostic diagnostic = new(
            "HXR002",
            ToolPhase.Prerequisite,
            ToolFailureCategory.PrerequisiteUnavailable,
            "The Dapr runtime-to-SDK compatibility disposition required by G-6 is not approved.",
            "platform.daprRuntimeVersion",
            "Record the G-6 disposition before running a live persisted platform lane.");
        return new RuntimePrerequisiteCheck(false, diagnostic);
    }
}