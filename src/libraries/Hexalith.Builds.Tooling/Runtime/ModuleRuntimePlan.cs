// <copyright file="ModuleRuntimePlan.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines a run-scoped topology plan that a supported Aspire composition must own.
/// </summary>
/// <param name="RunId">The unique runner invocation identity.</param>
/// <param name="TenantNamespace">The run-unique tenant namespace.</param>
/// <param name="ResourceNamespace">The run-unique resource namespace.</param>
/// <param name="Profile">The optional qualification profile.</param>
/// <param name="Modules">The deterministically ordered module identities.</param>
public sealed record ModuleRuntimePlan(
    string RunId,
    string TenantNamespace,
    string ResourceNamespace,
    string? Profile,
    IReadOnlyList<ModuleRuntimeModule> Modules)
{
    /// <summary>
    /// Creates a bounded plan that never derives identity from an existing persisted resource.
    /// </summary>
    /// <param name="manifest">The validated manifest.</param>
    /// <param name="profile">The optional qualification profile.</param>
    /// <returns>A fresh, run-scoped plan.</returns>
    public static ModuleRuntimePlan Create(ModuleManifest manifest, string? profile)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        string runId = Guid.NewGuid().ToString("N");
        string scope = runId[..12];
        IReadOnlyList<ModuleRuntimeModule> modules = [.. manifest.Modules
            .OrderBy(module => module.Id, StringComparer.Ordinal)
            .Select(module => new ModuleRuntimeModule(module.Id, module.Domain, module.ApplicationId, module.ResourceId))];
        return new ModuleRuntimePlan(
            runId,
            $"g4-{scope}",
            $"g4-{scope}",
            profile,
            modules);
    }
}