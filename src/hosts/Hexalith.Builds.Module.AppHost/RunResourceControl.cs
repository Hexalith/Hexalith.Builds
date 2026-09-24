// <copyright file="RunResourceControl.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using CommunityToolkit.Aspire.Hosting.Dapr;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Handles the private resource-control protocol for an active qualification run.
/// </summary>
internal static class RunResourceControl
{
    /// <summary>
    /// Processes only EventStore stop and start requests for the owning run.
    /// </summary>
    /// <param name="app">The running Aspire application.</param>
    /// <param name="plan">The owning run plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that runs until cancellation.</returns>
    public static async Task RunAsync(DistributedApplication app, CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        string requestPath = CompositionControlRequest.PathFor(plan.Workspace);
        string responsePath = CompositionControlResponse.PathFor(plan.Workspace);
        while (!cancellationToken.IsCancellationRequested)
        {
            CompositionControlRequest? request = await CompositionDocumentStore
                .TryReadAsync<CompositionControlRequest>(requestPath, cancellationToken).ConfigureAwait(false);
            if (request is not null)
            {
                File.Delete(requestPath);
                bool valid = string.Equals(request.Schema, CompositionControlRequest.SupportedSchema, StringComparison.Ordinal)
                    && string.Equals(request.RunId, plan.RunId, StringComparison.Ordinal)
                    && CompositionRunPlanFactory.IsRunId(request.Nonce)
                    && request.Command is "stop-eventstore" or "start-eventstore";
                string? failure = valid
                    ? await ExecuteAsync(app, request.Command, cancellationToken).ConfigureAwait(false)
                    : "invalid-request";
                await CompositionDocumentStore.WriteAsync(
                    responsePath,
                    new CompositionControlResponse(CompositionControlRequest.SupportedSchema, plan.RunId, request.Nonce, request.Command, failure is null, failure),
                    cancellationToken).ConfigureAwait(false);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ExecuteAsync(DistributedApplication app, string command, CancellationToken cancellationToken)
    {
        using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();
            ProjectResource primary = model.Resources.OfType<ProjectResource>()
                .Single(resource => string.Equals(resource.Name, RunTopology.EventStoreName, StringComparison.Ordinal));
            string sidecar = primary.Annotations.OfType<DaprSidecarAnnotation>().Single().Sidecar.Name + "-cli";
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            if (command == "stop-eventstore")
            {
                if (!await CommandAsync(app, RunTopology.EventStoreName, KnownResourceCommands.StopCommand, bound.Token).ConfigureAwait(false))
                {
                    return "project-stop";
                }

                bool sidecarCommand = IsStoppedNow(notifications, sidecar)
                    || await CommandAsync(app, sidecar, KnownResourceCommands.StopCommand, bound.Token).ConfigureAwait(false);
                bool stopped = sidecarCommand
                    && await IsStoppedAsync(notifications, RunTopology.EventStoreName, bound.Token).ConfigureAwait(false)
                    && await IsStoppedAsync(notifications, sidecar, bound.Token).ConfigureAwait(false);
                return (sidecarCommand, stopped) switch
                {
                    (false, _) => "sidecar-stop",
                    (true, false) => "stopped-state",
                    _ => null,
                };
            }

            if (!await CommandAsync(app, RunTopology.EventStoreName, KnownResourceCommands.StartCommand, bound.Token).ConfigureAwait(false))
            {
                return "project-start";
            }

            if (!await CommandAsync(app, sidecar, KnownResourceCommands.StartCommand, bound.Token).ConfigureAwait(false))
            {
                return "sidecar-start";
            }

            _ = await notifications.WaitForResourceHealthyAsync(RunTopology.EventStoreName, bound.Token).ConfigureAwait(false);
            _ = await notifications.WaitForResourceHealthyAsync(sidecar, bound.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is OperationCanceledException or DistributedApplicationException or InvalidOperationException)
        {
            // A failed or timed-out resource command must remain nonpassing.
        }

        return "resource-command";
    }

    private static async Task<bool> CommandAsync(DistributedApplication app, string resource, string command, CancellationToken cancellationToken)
    {
        ExecuteCommandResult result = await app.ResourceCommands.ExecuteCommandAsync(resource, command, cancellationToken).ConfigureAwait(false);
        return result.Success && !result.Canceled;
    }

    private static async Task<bool> IsStoppedAsync(ResourceNotificationService notifications, string resource, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsStoppedNow(notifications, resource))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    private static bool IsStoppedNow(ResourceNotificationService notifications, string resource) =>
        notifications.TryGetCurrentState(resource, out ResourceEvent? current)
        && (string.Equals(current.Snapshot.State?.Text, KnownResourceStates.Exited, StringComparison.Ordinal)
            || string.Equals(current.Snapshot.State?.Text, KnownResourceStates.Finished, StringComparison.Ordinal)
            || string.Equals(current.Snapshot.State?.Text, KnownResourceStates.NotStarted, StringComparison.Ordinal));
}
