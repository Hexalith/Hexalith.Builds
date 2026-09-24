// <copyright file="RunReadiness.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Net;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Waits until every composed resource is healthy and writes the metadata-only readiness document.
/// </summary>
internal static class RunReadiness
{
    private static readonly TimeSpan _resourceBound = TimeSpan.FromSeconds(90);

    private static readonly TimeSpan _invocationBound = TimeSpan.FromSeconds(45);

    /// <summary>
    /// Waits for every runnable resource and writes readiness, or reports a resource that cannot become healthy.
    /// </summary>
    /// <param name="app">The started distributed application.</param>
    /// <param name="plan">The run plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when readiness was written; false when a resource reached a terminal state.</returns>
    /// <exception cref="InvalidOperationException">A required endpoint was not allocated.</exception>
    public static async Task<bool> WaitAndWriteAsync(DistributedApplication app, CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);

        ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Resources that start only on explicit request (such as Aspire project rebuilders) are not part of the run.
        IResource[] runnable = RunnableResources(model);
        List<CompositionResourceReadiness> resources = [];
        foreach (IResource resource in runnable)
        {
            Console.WriteLine($"g4: waiting for resource '{resource.Name}' to become healthy.");
            using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(_resourceBound);
            try
            {
                _ = await notifications.WaitForResourceHealthyAsync(
                    resource.Name,
                    WaitBehavior.StopOnResourceUnavailable,
                    probe.Token).ConfigureAwait(false);
            }
            catch (DistributedApplicationException)
            {
                IResource stalled = TerminalFailure(runnable, notifications) ?? resource;
                await ReportFailureAsync(plan, "HXR025", stalled.Name, AppId(plan, stalled.Name), Status(notifications, stalled.Name)).ConfigureAwait(false);
                return false;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                IResource stalled = TerminalFailure(runnable, notifications) ?? resource;
                await ReportFailureAsync(plan, "HXR025", stalled.Name, AppId(plan, stalled.Name), Status(notifications, stalled.Name)).ConfigureAwait(false);
                return false;
            }

            resources.Add(new CompositionResourceReadiness(resource.Name, Kind(resource), "Healthy"));
        }

        // Resource health does not prove the Dapr invocation boundary: a module sidecar reports Running before it
        // is registered in the run's name resolution. Probe EventStore-to-module invocation through the sidecars.
        if (!await WaitForModuleInvocationAsync(plan, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        Console.WriteLine("g4: every resource is healthy.");

        Uri eventStore = RunEndpoints.TryGet(app, RunTopology.EventStoreName)
            ?? throw new InvalidOperationException("The EventStore endpoint was not allocated.");
        Uri? ui = plan.UiMarkers.Count > 0
            ? RunEndpoints.TryGet(app, RunTopology.UiName) ?? throw new InvalidOperationException("The UI endpoint was not allocated.")
            : null;
        await CompositionDocumentStore.WriteAsync(
            plan.ReadinessPath,
            new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, eventStore, ui, [.. resources.OrderBy(resource => resource.Name, StringComparer.Ordinal)]),
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reports the first stalled resource when the AppHost startup bound expires.
    /// </summary>
    /// <param name="app">The distributed application.</param>
    /// <param name="plan">The run plan.</param>
    /// <returns>A task that completes after the metadata-only failure is written.</returns>
    public static Task ReportStartupStallAsync(DistributedApplication app, CompositionRunPlan plan)
    {
        ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();
        IResource[] runnable = RunnableResources(model);
        IResource? stalled = TerminalFailure(runnable, notifications) ?? runnable.FirstOrDefault(resource =>
            !notifications.TryGetCurrentState(resource.Name, out ResourceEvent? current)
            || current.Snapshot.HealthStatus != HealthStatus.Healthy);
        string name = stalled?.Name ?? "apphost-start";
        return ReportFailureAsync(plan, "HXR025", name, AppId(plan, name), Status(notifications, name));
    }

    /// <summary>
    /// Writes a specific, metadata-only startup failure for the runner to report.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="ruleId">The stable rule identity.</param>
    /// <param name="resource">The stalled resource or probe.</param>
    /// <param name="appId">The relevant Dapr app ID, or a dash.</param>
    /// <param name="status">The bounded probe status.</param>
    /// <returns>A task that completes after the failure document is written.</returns>
    public static async Task ReportFailureAsync(CompositionRunPlan plan, string ruleId, string resource, string appId, string status)
    {
        string safeStatus = SafeStatus(status);
        await Console.Error.WriteLineAsync($"g4: {ruleId} resource='{resource}' appId='{appId}' status='{safeStatus}'.").ConfigureAwait(false);
        await CompositionDocumentStore.WriteAsync(
            CompositionStartupFailure.PathFor(plan.Workspace),
            new CompositionStartupFailure(CompositionStartupFailure.SupportedSchema, plan.RunId, ruleId, resource, appId, safeStatus),
            CancellationToken.None).ConfigureAwait(false);
    }

    private static string AppId(CompositionRunPlan plan, string resourceName) =>
        string.Equals(resourceName, RunTopology.EventStoreName, StringComparison.Ordinal)
        || string.Equals(resourceName, RunTopology.EventStoreName + "-dapr-cli", StringComparison.Ordinal)
            ? RunTopology.EventStoreName
            : plan.Modules.FirstOrDefault(module => string.Equals(resourceName, module.ModuleId, StringComparison.Ordinal)
                || string.Equals(resourceName, module.ModuleId + "-dapr-cli", StringComparison.Ordinal))?.AppId ?? "-";

    private static IResource? TerminalFailure(IEnumerable<IResource> resources, ResourceNotificationService notifications) =>
        resources.FirstOrDefault(resource => notifications.TryGetCurrentState(resource.Name, out ResourceEvent? current)
            && (string.Equals(current.Snapshot.State?.Text, KnownResourceStates.FailedToStart, StringComparison.Ordinal)
                || string.Equals(current.Snapshot.State?.Text, KnownResourceStates.RuntimeUnhealthy, StringComparison.Ordinal)
                || string.Equals(current.Snapshot.State?.Text, KnownResourceStates.Exited, StringComparison.Ordinal)
                || string.Equals(current.Snapshot.State?.Text, KnownResourceStates.Finished, StringComparison.Ordinal)));

    private static IResource[] RunnableResources(DistributedApplicationModel model) =>
        [.. model.Resources
            .Where(resource => resource is ProjectResource or ContainerResource or ExecutableResource
                && !resource.Annotations.OfType<ExplicitStartupAnnotation>().Any()
                && !string.Equals(resource.GetType().Name, "ProjectRebuilderResource", StringComparison.Ordinal))
            .OrderBy(resource => StartupOrder(resource.Name))
            .ThenBy(resource => resource.Name, StringComparer.Ordinal)];

    private static int StartupOrder(string resourceName) => resourceName switch
    {
        "redis" => 0,
        "placement" => 1,
        "scheduler" => 2,
        RunTopology.EventStoreName => 3,
        _ => 4,
    };

    private static string SafeStatus(string value) => new([.. value
        .Where(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '/' or '.' or ':' or ';' or '=' or ' ')
        .Take(120)]);

    private static string Status(ResourceNotificationService notifications, string resourceName) =>
        notifications.TryGetCurrentState(resourceName, out ResourceEvent? current)
            ? $"state={current.Snapshot.State?.Text ?? "unknown"};health={current.Snapshot.HealthStatus?.ToString() ?? "unknown"}"
            : "state=unreported;health=unreported";

    private static async Task<bool> WaitForModuleInvocationAsync(CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        using HttpClient sidecar = new()
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), plan.Ports.EventStoreDaprHttp).Uri,
            Timeout = TimeSpan.FromSeconds(10),
        };
        foreach (CompositionRunModule module in plan.Modules)
        {
            Console.WriteLine($"g4: waiting for Dapr invocation of '{module.AppId}'.");
            using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(_invocationBound);
            string status = "unreported";
            while (!probe.IsCancellationRequested)
            {
                try
                {
                    using HttpResponseMessage response = await sidecar
                        .GetAsync(new Uri($"/v1.0/invoke/{module.AppId}/method/alive", UriKind.Relative), probe.Token)
                        .ConfigureAwait(false);
                    status = "HTTP " + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }
                }
                catch (HttpRequestException)
                {
                    status = "connection-unavailable";
                }
                catch (TaskCanceledException) when (!probe.IsCancellationRequested)
                {
                    status = "request-timeout";
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && probe.IsCancellationRequested)
                {
                    break;
                }

                if (probe.IsCancellationRequested)
                {
                    break;
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), probe.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && probe.IsCancellationRequested)
                {
                    break;
                }
            }

            if (probe.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                await ReportFailureAsync(plan, "HXR026", module.ModuleId, module.AppId, status + ";probe-timeout").ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    private static string Kind(IResource resource) => resource switch
    {
        ProjectResource => "project",
        ContainerResource => "container",
        _ => "executable",
    };
}
