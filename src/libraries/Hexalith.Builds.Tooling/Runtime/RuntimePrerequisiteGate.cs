// <copyright file="RuntimePrerequisiteGate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Enforces the independently owned G-6 compatibility disposition.
/// </summary>
/// <remarks>
/// G-6 approves only the exact Dapr runtime/package exception tuple. The separate descriptor-ABI
/// prerequisite remains enforced by <c>ModuleCommandExecutionService</c> after this check succeeds.
/// </remarks>
public static class RuntimePrerequisiteGate
{
    /// <summary>
    /// Checks whether a validated manifest may enter the live Aspire composition phase.
    /// </summary>
    /// <param name="manifest">The already validated module manifest.</param>
    /// <returns>A fail-closed prerequisite decision for the approved exception tuple.</returns>
    public static RuntimePrerequisiteCheck Check(ModuleManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        if (string.Equals(manifest.Platform.DaprRuntimeVersion, SupportedPlatformPins.DaprRuntimeVersion, StringComparison.Ordinal)
            && string.Equals(manifest.Platform.DaprSdkVersion, SupportedPlatformPins.DaprSdkVersion, StringComparison.Ordinal))
        {
            return new RuntimePrerequisiteCheck(true, null);
        }

        ToolDiagnostic diagnostic = new(
            "HXR002",
            ToolPhase.Prerequisite,
            ToolFailureCategory.PrerequisiteUnavailable,
            "The manifest does not select the owner-approved G-6 Dapr exception tuple.",
            "platform",
            "Use Dapr runtime 1.18.2 with Dapr .NET packages 1.18.8 before retrying.");
        return new RuntimePrerequisiteCheck(false, diagnostic);
    }
}
