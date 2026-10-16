// <copyright file="RunReadinessHealth.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using CommunityToolkit.Aspire.Hosting.Dapr;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>Requires a new evaluation of the declared health checks after startup tasks finish.</summary>
internal static class RunReadinessHealth
{
    /// <summary>Waits for all resource health checks to pass using the original finite deadline.</summary>
    /// <param name="app">The running distributed application.</param>
    /// <param name="plan">The owning startup plan.</param>
    /// <param name="resources">The required runnable resources.</param>
    /// <param name="cancellationToken">The startup deadline token.</param>
    /// <returns>A task that completes only after a fresh healthy evaluation.</returns>
    internal static async Task WaitAsync(DistributedApplication app, CompositionRunPlan plan, IReadOnlyList<IResource> resources, CancellationToken cancellationToken)
    {
        HealthCheckService checks = app.Services.GetRequiredService<HealthCheckService>();
        Dictionary<string, string[]> resourceChecks = resources.ToDictionary(
            resource => resource.Name,
            resource => resource.Annotations.OfType<HealthCheckAnnotation>().Select(annotation => annotation.Key).ToArray(),
            StringComparer.Ordinal);
        HashSet<string> keys = resourceChecks.Values.SelectMany(value => value).ToHashSet(StringComparer.Ordinal);

        // A toolkit sidecar is accepted only alongside its associated module's fresh health checks.
        // The caller also re-probes Dapr readiness before writing the correlated snapshot.
        HashSet<string> invocationSidecars = resources
            .Where(resource => resourceChecks[resource.Name].Length > 0)
            .SelectMany(resource => resource.Annotations.OfType<DaprSidecarAnnotation>())
            .Select(annotation => annotation.Sidecar.Name + "-cli")
            .ToHashSet(StringComparer.Ordinal);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            HealthReport report = await checks.CheckHealthAsync(registration => keys.Contains(registration.Name), cancellationToken).ConfigureAwait(false);
            CompositionResourceReadiness[] unready =
            [
                .. resources
                .Where(resource => (resourceChecks[resource.Name].Length == 0 && !invocationSidecars.Contains(resource.Name))
                    || resourceChecks[resource.Name].Any(key => !report.Entries.TryGetValue(key, out HealthReportEntry entry) || entry.Status != HealthStatus.Healthy))
                .Select(resource => new CompositionResourceReadiness(resource.Name, Kind(resource), "Unhealthy")
                {
                    Detail = resourceChecks[resource.Name].Length == 0 ? "module-health-unavailable" : "module-health-unhealthy",
                }),
            ];
            if (unready.Length == 0)
            {
                return;
            }

            await CompositionDocumentStore.WriteAsync(
                CompositionReadinessProgress.PathFor(plan.Workspace),
                new CompositionReadinessProgress(CompositionReadinessProgress.SupportedSchema, plan.RunId, unready),
                cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Kind(IResource resource) => resource switch
    {
        ProjectResource => "project",
        ContainerResource => "container",
        _ => "executable",
    };
}
