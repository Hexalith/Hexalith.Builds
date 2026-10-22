// <copyright file="RunReadiness.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Net;

using CommunityToolkit.Aspire.Hosting.Dapr;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Waits until every composed resource is healthy and writes the metadata-only readiness document.
/// </summary>
internal static class RunReadiness
{
    /// <summary>
    /// Waits for every runnable resource and writes readiness, or reports a resource that cannot become healthy.
    /// </summary>
    /// <param name="app">The started distributed application.</param>
    /// <param name="plan">The run plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True when readiness was written; false when a resource reached a terminal state.</returns>
    /// <exception cref="InvalidOperationException">A required endpoint was not allocated.</exception>
    public static Task<bool> WaitAndWriteAsync(DistributedApplication app, CompositionRunPlan plan, CancellationToken cancellationToken) =>
        WaitAndWriteCoreAsync(app, plan, null, cancellationToken);

    /// <summary>
    /// Reports every stalled resource when the AppHost startup bound expires.
    /// </summary>
    /// <param name="app">The distributed application.</param>
    /// <param name="plan">The run plan.</param>
    /// <returns>A task that completes after the metadata-only failure is written.</returns>
    public static Task ReportStartupStallAsync(DistributedApplication app, CompositionRunPlan plan)
    {
        ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();
        IResource[] runnable = RunnableResources(model);
        return ReportObservedFailureAsync(plan, model, runnable, notifications, null, "HXR021");
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
    public static Task ReportFailureAsync(CompositionRunPlan plan, string ruleId, string resource, string appId, string status) =>
        ReportFailureAsync(plan, ruleId, resource, appId, status, null, null);

    /// <summary>
    /// Writes a startup failure that names every unready resource and, when present, the failing startup task.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="ruleId">The stable rule identity.</param>
    /// <param name="resource">The stalled resource or probe.</param>
    /// <param name="appId">The relevant Dapr app ID, or a dash.</param>
    /// <param name="status">The bounded probe status.</param>
    /// <param name="unready">Every unready resource, when more than the primary resource is known.</param>
    /// <param name="failingTask">The startup task that failed, when one did.</param>
    /// <returns>A task that completes after the failure document is written.</returns>
    public static async Task ReportFailureAsync(
        CompositionRunPlan plan,
        string ruleId,
        string resource,
        string appId,
        string status,
        IReadOnlyList<CompositionResourceReadiness>? unready,
        string? failingTask)
    {
        string safeStatus = SafeStatus(status);
        await Console.Error.WriteLineAsync($"g4: {ruleId} resource='{resource}' appId='{appId}' status='{safeStatus}'.").ConfigureAwait(false);
        await CompositionDocumentStore.WriteAsync(
            CompositionStartupFailure.PathFor(plan.Workspace),
            new CompositionStartupFailure(CompositionStartupFailure.SupportedSchema, plan.RunId, ruleId, resource, appId, safeStatus)
            {
                UnreadyResources = unready ?? [],
                FailingTask = failingTask,
            },
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Proves selected server readiness through the existing bounded Dapr invocation boundary.</summary>
    /// <param name="plan">The signed run plan.</param>
    /// <param name="model">The actual selected resource model.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="notifications">The resource notifications used to name every unready resource, when available.</param>
    /// <returns>True when every selected server can be invoked.</returns>
    internal static async Task<bool> WaitForModuleInvocationAsync(CompositionRunPlan plan, DistributedApplicationModel model, CancellationToken cancellationToken, ResourceNotificationService? notifications = null)
    {
        if (plan.IsPlatformComposition)
        {
            return await WaitForPlatformSidecarsAsync(plan, model, cancellationToken, notifications: notifications).ConfigureAwait(false);
        }

        using HttpClient sidecar = new()
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttp, IPAddress.Loopback.ToString(), plan.Ports.EventStoreDaprHttp).Uri,
            Timeout = TimeSpan.FromSeconds(10),
        };
        foreach ((string resource, string appId, string endpoint) in InvocationProbes(plan, model))
        {
            Console.WriteLine($"g4: waiting for Dapr invocation of '{appId}'.");
            using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            probe.CancelAfter(CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow));
            string status = "unreported";
            while (!probe.IsCancellationRequested)
            {
                try
                {
                    using HttpResponseMessage response = await sidecar
                        .GetAsync(new Uri($"/v1.0/invoke/{appId}/method{endpoint}", UriKind.Relative), probe.Token)
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
                await ReportFailureAsync(plan, "HXR026", resource, appId, status + ";probe-timeout", CurrentUnready(model, notifications), null).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    /// <summary>Derives v2 probes from composed bindings while retaining v1 alive endpoints.</summary>
    /// <param name="plan">The signed run plan.</param>
    /// <param name="model">The actual selected resource model.</param>
    /// <returns>The resource, sidecar identity, and declared endpoint.</returns>
    /// <exception cref="InvalidDataException">The model has no annotated gateway.</exception>
    internal static IReadOnlyList<(string Resource, string AppId, string Endpoint)> InvocationProbes(CompositionRunPlan plan, DistributedApplicationModel model)
    {
        if (!plan.IsPlatformComposition)
        {
            return [.. plan.Modules.Select(module => (module.ModuleId, module.AppId, "/alive"))];
        }

        List<(string Resource, string AppId, string Endpoint)> probes = [];
        if (!model.Resources.Any(resource => resource.Name == RunTopology.EventStoreName
            && resource.Annotations.Any(annotation => annotation.GetType().Name == "PlatformServerAnnotation")))
        {
            throw new InvalidDataException("Platform v2 model has no annotated eventstore gateway; invocation readiness cannot be verified.");
        }

        foreach (IResource resource in model.Resources.Where(resource => resource.Name != RunTopology.EventStoreName))
        {
            IResourceAnnotation? selected = resource.Annotations.FirstOrDefault(annotation => annotation.GetType().Name == "PlatformServerAnnotation");
            if (selected is null)
            {
                continue;
            }

            object binding = selected.GetType().GetProperty("Binding")!.GetValue(selected)!;
            string endpoint = (string)binding.GetType().GetProperty("ReadinessEndpoint")!.GetValue(binding)!;
            DaprSidecarOptions sidecar = resource.Annotations.OfType<DaprSidecarAnnotation>().Single().Sidecar.Annotations.OfType<DaprSidecarOptionsAnnotation>().Single().Options;
            probes.Add((resource.Name, sidecar.AppId!, endpoint));
        }

        return probes;
    }

    /// <summary>Checks authenticated sidecar health and retains structured refusal evidence on timeout.</summary>
    /// <param name="plan">The run owning the failure document.</param>
    /// <param name="model">The selected resource model.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <param name="probeBound">An optional bounded probe duration for focused verification.</param>
    /// <param name="notifications">The resource notifications used to name every unready resource, when available.</param>
    /// <returns>Whether every selected sidecar became ready.</returns>
    /// <exception cref="InvalidDataException">A selected sidecar has no HTTP listener.</exception>
    internal static async Task<bool> WaitForPlatformSidecarsAsync(CompositionRunPlan plan, DistributedApplicationModel model, CancellationToken cancellationToken, TimeSpan? probeBound = null, ResourceNotificationService? notifications = null)
    {
        foreach (IResource resource in model.Resources.Where(resource => resource.Annotations.Any(annotation => annotation.GetType().Name == "PlatformServerAnnotation")))
        {
            IDaprSidecarResource sidecar = resource.Annotations.OfType<DaprSidecarAnnotation>().Single().Sidecar;
            DaprSidecarOptions options = sidecar.Annotations.OfType<DaprSidecarOptionsAnnotation>().Single().Options;
            int port = options.DaprHttpPort ?? throw new InvalidDataException($"{resource.Name}: Dapr HTTP listener is missing.");
            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(5) };
            IResourceAnnotation annotation = resource.Annotations.Single(annotation => annotation.GetType().Name == "PlatformServerAnnotation");
            object? binding = annotation.GetType().GetProperty("Binding")?.GetValue(annotation);
            if (binding?.GetType().GetProperty("Environment")?.GetValue(binding) is IReadOnlyDictionary<string, string> environment
                && environment.TryGetValue("DAPR_API_TOKEN", out string? token))
            {
                client.DefaultRequestHeaders.Add("dapr-api-token", token);
            }

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            TimeSpan remaining = CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow);
            timeout.CancelAfter(probeBound is { } bound && bound < remaining ? bound : remaining);
            string status = "unreported";
            while (!timeout.IsCancellationRequested)
            {
                try
                {
                    using HttpResponseMessage response = await client.GetAsync(new Uri($"http://127.0.0.1:{port}/v1.0/healthz/outbound"), timeout.Token).ConfigureAwait(false);
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
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    if (!timeout.IsCancellationRequested || status == "unreported")
                    {
                        status = "request-timeout";
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested)
            {
                await ReportFailureAsync(plan, "HXR026", resource.Name, options.AppId!, status + ";probe-timeout", CurrentUnready(model, notifications), null).ConfigureAwait(false);
                return false;
            }
        }

        return true;
    }

    /// <summary>Reads every runnable resource that is not currently healthy.</summary>
    /// <param name="app">The distributed application.</param>
    /// <returns>The unready resources, or an empty list when every runnable resource is healthy.</returns>
    internal static IReadOnlyList<CompositionResourceReadiness> CurrentUnready(DistributedApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return CurrentUnready(
            app.Services.GetRequiredService<DistributedApplicationModel>(),
            app.Services.GetRequiredService<ResourceNotificationService>()) ?? [];
    }

    /// <summary>Evaluates module-defined health again after successful startup tasks.</summary>
    /// <param name="app">The composed application.</param>
    /// <param name="plan">The owning run plan.</param>
    /// <param name="refreshId">The owning refresh request.</param>
    /// <param name="cancellationToken">The original startup deadline token.</param>
    /// <returns>Whether a fresh readiness document was written.</returns>
    internal static Task<bool> RefreshAsync(DistributedApplication app, CompositionRunPlan plan, string refreshId, CancellationToken cancellationToken) =>
        WaitAndWriteCoreAsync(app, plan, refreshId, cancellationToken);

    private static async Task<bool> WaitAndWriteCoreAsync(DistributedApplication app, CompositionRunPlan plan, string? refreshId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);

        ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();

        // Resources that start only on explicit request (such as Aspire project rebuilders) are not part of the run.
        IResource[] runnable = RunnableResources(model);
        while (true)
        {
            if (refreshId is not null)
            {
                await RunReadinessHealth.WaitAsync(app, plan, runnable, cancellationToken).ConfigureAwait(false);
            }

            List<CompositionResourceReadiness> resources = [];
            foreach (IResource resource in runnable)
            {
                await WriteProgressAsync(plan, Observe(runnable, notifications)).ConfigureAwait(false);
                Console.WriteLine($"g4: waiting for resource '{resource.Name}' to become healthy.");
                using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                probe.CancelAfter(CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow));
                try
                {
                    _ = await notifications.WaitForResourceHealthyAsync(
                        resource.Name,
                        WaitBehavior.StopOnResourceUnavailable,
                        probe.Token).ConfigureAwait(false);
                }
                catch (DistributedApplicationException)
                {
                    await ReportObservedFailureAsync(plan, model, runnable, notifications, resource, "HXR025").ConfigureAwait(false);
                    return false;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await ReportObservedFailureAsync(plan, model, runnable, notifications, resource, "HXR025").ConfigureAwait(false);
                    return false;
                }

                resources.Add(new CompositionResourceReadiness(resource.Name, Kind(resource), "Healthy")
                {
                    Detail = "module-ready",
                    ProcessId = TryProcessId(notifications, resource.Name),
                });
            }

            await WriteProgressAsync(plan, Observe(runnable, notifications)).ConfigureAwait(false);

            // Resource health does not prove the Dapr invocation boundary: a module sidecar reports Running before it
            // is registered in the run's name resolution. Probe EventStore-to-module invocation through the sidecars.
            if (!await WaitForModuleInvocationAsync(plan, model, cancellationToken, notifications).ConfigureAwait(false))
            {
                return false;
            }

            await WriteProgressAsync(plan, Observe(runnable, notifications)).ConfigureAwait(false);

            CompositionTestObservation? testObservation = null;
            if (app.Services.GetService<ICompositionTestObservationProvider>() is { } observer)
            {
                testObservation = await observer.ObserveAsync(app.Services, cancellationToken).ConfigureAwait(false);
                if (!testObservation.IsComplete || testObservation.RunId != plan.RunId || testObservation.ModuleId != plan.SelectedTest?.ModuleId)
                {
                    await ReportFailureAsync(plan, "HXR026", "selected-test-observations", plan.SelectedTest?.ModuleId ?? "-", "identity-or-access-unavailable", resources, null).ConfigureAwait(false);
                    return false;
                }
            }

            resources = Observe(runnable, notifications);
            if (resources.Any(resource => resource.State != "Healthy"))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
                continue;
            }

            resources = [.. resources.Select(resource => resource with { Detail = "module-ready" })];

            Console.WriteLine("g4: every resource is healthy.");

            Uri eventStore = RunEndpoints.TryGet(app, RunTopology.EventStoreName)
                ?? throw new InvalidOperationException("The EventStore endpoint was not allocated.");
            Uri? ui = plan.UiMarkers.Count > 0
                ? RunEndpoints.TryGet(app, RunTopology.UiName) ?? throw new InvalidOperationException("The UI endpoint was not allocated.")
                : null;
            await CompositionDocumentStore.WriteAsync(
                plan.ReadinessPath,
                new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, eventStore, ui, [.. resources.OrderBy(resource => resource.Name, StringComparer.Ordinal)])
                {
                    RefreshId = refreshId,
                    SelectedTest = plan.SelectedTest,
                    TestObservation = testObservation,
                    EffectiveDeadlineSeconds = plan.EffectiveDeadline?.TimeoutSeconds,
                    EffectiveDeadlineIsOverride = plan.EffectiveDeadline?.IsOverride ?? false,
                    EffectiveDeadlineJustification = plan.EffectiveDeadline?.Justification,
                    EffectiveDeadlineModuleId = plan.EffectiveDeadline?.ModuleId,
                    ObservedAt = DateTimeOffset.UtcNow,
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
    }

    private static IReadOnlyList<CompositionResourceReadiness>? CurrentUnready(DistributedApplicationModel model, ResourceNotificationService? notifications) =>
        notifications is null
            ? null
            : [.. Observe(RunnableResources(model), notifications).Where(resource => !string.Equals(resource.State, "Healthy", StringComparison.Ordinal))];

    private static Task ReportObservedFailureAsync(
        CompositionRunPlan plan,
        DistributedApplicationModel model,
        IResource[] runnable,
        ResourceNotificationService notifications,
        IResource? primary,
        string ruleId)
    {
        IReadOnlyList<CompositionResourceReadiness> observed = Observe(runnable, notifications);
        IReadOnlyList<CompositionResourceReadiness> unready = [.. observed.Where(resource => !string.Equals(resource.State, "Healthy", StringComparison.Ordinal))];
        string name = primary?.Name
            ?? TerminalFailure(runnable, notifications)?.Name
            ?? (unready.Count == 0 ? null : unready[0].Name)
            ?? "apphost-start";
        return ReportFailureAsync(plan, ruleId, name, AppId(plan, model, name), Status(notifications, name), unready.Count > 0 ? unready : null, null);
    }

    private static async Task WriteProgressAsync(CompositionRunPlan plan, IReadOnlyList<CompositionResourceReadiness> resources)
    {
        try
        {
            await CompositionDocumentStore.WriteAsync(
                CompositionReadinessProgress.PathFor(plan.Workspace),
                new CompositionReadinessProgress(CompositionReadinessProgress.SupportedSchema, plan.RunId, resources),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A missed progress snapshot cannot hide the later readiness or failure document.
        }
    }

    private static List<CompositionResourceReadiness> Observe(
        IEnumerable<IResource> runnable,
        ResourceNotificationService notifications)
    {
        List<CompositionResourceReadiness> resources = [];
        foreach (IResource resource in runnable)
        {
            bool healthy = notifications.TryGetCurrentState(resource.Name, out ResourceEvent? current)
                && current.Snapshot.HealthStatus == HealthStatus.Healthy;
            resources.Add(new CompositionResourceReadiness(resource.Name, Kind(resource), healthy ? "Healthy" : "Unhealthy")
            {
                Detail = SafeStatus(Status(notifications, resource.Name)),
                ProcessId = ProcessId(current),
                ModuleId = OwningModule(resource, runnable),
                Endpoints = healthy ? RunEndpoints.Observe(resource) : [],
                ObservedAt = DateTimeOffset.UtcNow,
            });
        }

        return resources;
    }

    private static string OwningModule(IResource resource, IEnumerable<IResource> resources)
    {
        string name = resource.Name.EndsWith("-dapr-cli", StringComparison.Ordinal) ? resource.Name[..^"-dapr-cli".Length] : resource.Name;
        IResource owner = resources.FirstOrDefault(candidate => candidate.Name == name) ?? resource;
        IResourceAnnotation? annotation = owner.Annotations.FirstOrDefault(candidate => candidate.GetType().Name == "PlatformServerAnnotation");
        object? binding = annotation?.GetType().GetProperty("Binding")?.GetValue(annotation);
        object? module = binding?.GetType().GetProperty("Module")?.GetValue(binding);
        return module?.GetType().GetProperty("Declaration")?.GetValue(module) is System.Text.Json.JsonElement declaration
            ? declaration.GetProperty("identity").GetProperty("moduleId").GetString()!
            : "platform";
    }

    private static int? TryProcessId(ResourceNotificationService notifications, string resourceName) =>
        notifications.TryGetCurrentState(resourceName, out ResourceEvent? current) ? ProcessId(current) : null;

    private static int? ProcessId(ResourceEvent? current)
    {
        if (current is null)
        {
            return null;
        }

        foreach (ResourcePropertySnapshot property in current.Snapshot.Properties)
        {
            if (!string.Equals(property.Name, "executable.pid", StringComparison.Ordinal))
            {
                continue;
            }

            return property.Value switch
            {
                int value when value > 0 => value,
                long value when value is > 0 and <= int.MaxValue => (int)value,
                string value when int.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out int parsed) && parsed > 0 => parsed,
                _ => null,
            };
        }

        return null;
    }

    private static string AppId(CompositionRunPlan plan, DistributedApplicationModel model, string resourceName)
    {
        if (string.Equals(resourceName, RunTopology.EventStoreName, StringComparison.Ordinal)
            || string.Equals(resourceName, RunTopology.EventStoreName + "-dapr-cli", StringComparison.Ordinal))
        {
            return RunTopology.EventStoreName;
        }

        if (plan.IsPlatformComposition)
        {
            string serverName = resourceName.EndsWith("-dapr-cli", StringComparison.Ordinal) ? resourceName[..^"-dapr-cli".Length] : resourceName;
            IResource? server = model.Resources.FirstOrDefault(resource => resource.Name == serverName);
            string? selected = server?.Annotations.OfType<DaprSidecarAnnotation>().SingleOrDefault()?.Sidecar.Annotations
                .OfType<DaprSidecarOptionsAnnotation>().SingleOrDefault()?.Options.AppId;
            if (!string.IsNullOrWhiteSpace(selected))
            {
                return selected;
            }
        }

        return plan.Modules.FirstOrDefault(module => string.Equals(resourceName, module.ModuleId, StringComparison.Ordinal)
            || string.Equals(resourceName, module.ModuleId + "-dapr-cli", StringComparison.Ordinal))?.AppId ?? "-";
    }

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
            .ThenBy(resource => resource.Name, StringComparer.Ordinal),
        ];

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
        .Take(120),
    ]);

    private static string Status(ResourceNotificationService notifications, string resourceName) =>
        notifications.TryGetCurrentState(resourceName, out ResourceEvent? current)
            ? $"state={current.Snapshot.State?.Text ?? "unknown"};health={current.Snapshot.HealthStatus?.ToString() ?? "unknown"}"
            : "state=unreported;health=unreported";

    private static string Kind(IResource resource) => resource switch
    {
        ProjectResource => "project",
        ContainerResource => "container",
        _ => "executable",
    };
}
