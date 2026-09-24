// <copyright file="CompositionRunPlanFactory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Creates run identities and deterministic, metadata-only run plans.
/// </summary>
public static class CompositionRunPlanFactory
{
    /// <summary>
    /// Creates a fresh run identity: 32 lowercase hexadecimal characters.
    /// </summary>
    /// <returns>The run identity.</returns>
    public static string NewRunId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Determines whether a value is a well-formed run identity.
    /// </summary>
    /// <param name="runId">The candidate.</param>
    /// <returns>True for 32 lowercase hexadecimal characters.</returns>
    public static bool IsRunId(string? runId) =>
        runId is { Length: 32 } && runId.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    /// <summary>
    /// Binds validated manifest modules to their validated executable descriptors.
    /// </summary>
    /// <param name="manifest">The validated manifest.</param>
    /// <param name="descriptors">The validated descriptor load result.</param>
    /// <returns>The deterministically ordered modules and UI markers.</returns>
    /// <exception cref="ArgumentException">A manifest module has no validated descriptor.</exception>
    public static (IReadOnlyList<CompositionRunModule> Modules, IReadOnlyList<CompositionRunUiMarker> UiMarkers) Bind(
        ModuleManifest manifest,
        ExecutableDescriptorLoadResult descriptors)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(descriptors);

        Dictionary<string, ExecutableModuleDescriptor> byId = descriptors.Modules
            .ToDictionary(module => module.ModuleId, StringComparer.Ordinal);
        List<CompositionRunModule> modules = [];
        foreach (ModuleDescriptor module in manifest.Modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            if (!byId.TryGetValue(module.Id, out ExecutableModuleDescriptor? descriptor))
            {
                throw new ArgumentException("Every manifest module requires a validated executable descriptor.", nameof(descriptors));
            }

            modules.Add(new CompositionRunModule(module.Id, module.Domain, module.ApplicationId, descriptor.DomainServiceProject));
        }

        IReadOnlyList<CompositionRunUiMarker> markers = descriptors.Ui is null
            ? []
            : [.. descriptors.Ui.ModuleUiMarkers
                .OrderBy(marker => marker.ModuleId, StringComparer.Ordinal)
                .Select(marker => new CompositionRunUiMarker(marker.ModuleId, marker.UiAssembly, marker.UiMarkerType))];
        return (modules, markers);
    }

    /// <summary>
    /// Creates the run plan for one invocation. The plan names only runner-owned paths, ports, and identities.
    /// </summary>
    /// <param name="runId">The run identity.</param>
    /// <param name="workspace">The absolute runner-owned run workspace.</param>
    /// <param name="daprHome">The absolute verified Dapr home.</param>
    /// <param name="ports">The allocated ports.</param>
    /// <param name="modules">The validated modules.</param>
    /// <param name="uiMarkers">The validated UI markers.</param>
    /// <returns>The run plan.</returns>
    /// <exception cref="ArgumentException">An identity or path is malformed.</exception>
    public static CompositionRunPlan Create(
        string runId,
        string workspace,
        string daprHome,
        CompositionRunPorts ports,
        IReadOnlyList<CompositionRunModule> modules,
        IReadOnlyList<CompositionRunUiMarker> uiMarkers)
    {
        ArgumentNullException.ThrowIfNull(ports);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(uiMarkers);
        if (!IsRunId(runId))
        {
            throw new ArgumentException("The run identity must be 32 lowercase hexadecimal characters.", nameof(runId));
        }

        if (!Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(daprHome))
        {
            throw new ArgumentException("The workspace and Dapr home must be absolute paths.", nameof(workspace));
        }

        if (modules.Count == 0 || ports.ModuleHttp.Count != modules.Count || ports.ModuleDapr.Count != modules.Count || ports.ToList().Distinct().Count() != ports.ToList().Count)
        {
            throw new ArgumentException("A run requires one HTTP and four sidecar ports per module and distinct ports.", nameof(modules));
        }

        string scope = runId[..12];
        string dapr = Path.Combine(workspace, "dapr");
        return new CompositionRunPlan(
            CompositionRunPlan.SupportedSchema,
            runId,
            $"g4t-{scope}",
            $"g4f-{scope}",
            $"g4r-{scope}",
            workspace,
            daprHome,
            Path.Combine(workspace, "readiness.json"),
            Path.Combine(dapr, "config.yaml"),
            Path.Combine(dapr, "components", "statestore.yaml"),
            Path.Combine(dapr, "components", "pubsub.yaml"),
            Path.Combine(dapr, "isolated"),
            CompositionToolchainPins.RedisImageReference,
            ports,
            [.. modules.OrderBy(module => module.ModuleId, StringComparer.Ordinal)],
            [.. uiMarkers.OrderBy(marker => marker.ModuleId, StringComparer.Ordinal)]);
    }
}
