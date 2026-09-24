// <copyright file="AppHostRunner.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.Hosting;

/// <summary>
/// Runs one composed G-4 run until the runner asks it to stop.
/// </summary>
internal static class AppHostRunner
{
    /// <summary>
    /// Composes, starts, reports readiness for, and gracefully stops one run.
    /// </summary>
    /// <param name="args">The AppHost arguments.</param>
    /// <returns>Zero after a graceful stop; three when a resource could not become healthy.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        CompositionRunPlan plan = await RunPlanSource.LoadAsync(CancellationToken.None).ConfigureAwait(false);
        string signingKey = RunPlanSource.TakeSigningKey();

        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            Args = args,
            DisableDashboard = true,
            AllowUnsecuredTransport = true,
            EnableResourceLogging = string.Equals(
                Environment.GetEnvironmentVariable(CompositionEnvironment.ResourceLogs),
                "1",
                StringComparison.Ordinal),
        });
        _ = builder.AddDapr(options =>
        {
            options.DaprPath = RunPlanSource.DaprCliPath(plan);
            options.EnableTelemetry = false;
        });
        RunTopology.Compose(builder, plan, signingKey);

        DistributedApplication app = builder.Build();
        StopSignal stop = StopSignal.Listen(plan);
        await using (stop.ConfigureAwait(false))
        await using (app.ConfigureAwait(false))
        {
            int exitCode = 0;
            using CancellationTokenSource readinessBound = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            readinessBound.CancelAfter(TimeSpan.FromMinutes(5));
            using CancellationTokenSource controlBound = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            Task? cutover = null;
            Task? starting = null;
            Task<bool>? readiness = null;
            Task? resourceControl = null;
            try
            {
                // Start blocks while dependents wait for a healthy EventStore, and EventStore becomes healthy
                // only after the runner-owned writer-protocol cutover, so the cutover runs concurrently.
#pragma warning disable CA2025 // The cutover task is awaited below, before the application is disposed.
                cutover = WriterProtocolCutover.ActivateAsync(app, plan, signingKey, readinessBound.Token);
#pragma warning restore CA2025
                starting = app.StartAsync(readinessBound.Token);
#pragma warning disable CA2025 // The readiness task is cancelled and drained below before the application is disposed.
                readiness = RunReadiness.WaitAndWriteAsync(app, plan, readinessBound.Token);
#pragma warning restore CA2025
                List<Task> pending = [starting, cutover, readiness];
                bool healthy = false;
                while (pending.Count > 0)
                {
                    // Resource waits begin with Redis and run alongside AppHost startup, so an unhealthy
                    // prerequisite reports its own bounded failure before a dependent cutover times out.
                    Task completed = await Task.WhenAny(pending).ConfigureAwait(false);
                    _ = pending.Remove(completed);
                    if (ReferenceEquals(completed, readiness))
                    {
                        healthy = await readiness.ConfigureAwait(false);
                        if (!healthy)
                        {
                            break;
                        }
                    }
                    else
                    {
                        await completed.ConfigureAwait(false);
                    }
                }

                if (healthy)
                {
#pragma warning disable CA2025 // The control task is cancelled and drained before the application is disposed.
                    resourceControl = RunResourceControl.RunAsync(app, plan, controlBound.Token);
#pragma warning restore CA2025
                    await app.WaitForShutdownAsync(stop.Token).ConfigureAwait(false);
                }
                else
                {
                    exitCode = 3;
                }
            }
            catch (OperationCanceledException) when (stop.IsStopRequested)
            {
                // The runner asked the run to stop; fall through to the graceful stop.
            }
            catch (Exception exception) when (!stop.IsStopRequested && cutover?.IsFaulted == true && exception is not OutOfMemoryException)
            {
                string status = exception is TimeoutException or InvalidOperationException
                    && exception.Message.StartsWith("cutover-", StringComparison.Ordinal)
                    ? exception.Message
                    : "fault-" + exception.GetType().Name;
                await RunReadiness.ReportFailureAsync(plan, "HXR027", RunTopology.EventStoreName, RunTopology.EventStoreName, status).ConfigureAwait(false);
                exitCode = 3;
            }
            catch (Exception exception) when (!stop.IsStopRequested && exception is not OutOfMemoryException)
            {
                await Console.Error.WriteLineAsync($"g4: AppHost startup stopped with {exception.GetType().Name}.").ConfigureAwait(false);
                await RunReadiness.ReportStartupStallAsync(app, plan).ConfigureAwait(false);
                exitCode = 3;
            }
            finally
            {
                await controlBound.CancelAsync().ConfigureAwait(false);
                await readinessBound.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(new[] { starting, cutover, readiness, resourceControl }.OfType<Task>())
                        .WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Startup failures are reported above; a task that ignores cancellation cannot delay teardown.
                }

                await app.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }

            return exitCode;
        }
    }
}
