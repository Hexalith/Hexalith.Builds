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
    /// <summary>The Aspire configuration key for the local store root.</summary>
    private const string _aspireStorePathKey = "Aspire:Store:Path";

    /// <summary>
    /// Composes, starts, reports readiness for, and gracefully stops one run.
    /// </summary>
    /// <param name="args">The AppHost arguments.</param>
    /// <returns>Zero after a graceful stop; three when a resource could not become healthy.</returns>
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            CompositionRunPlan plan = await RunPlanSource.LoadAsync(CancellationToken.None).ConfigureAwait(false);
            string signingKey = RunPlanSource.TakeSigningKey();
            return await RunAsync(args, plan, signingKey, RunTopology.Compose).ConfigureAwait(false);
        }
        catch (Exception exception) when (args.Contains("--validate-platform", StringComparer.Ordinal)
            && exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 3;
        }
    }

    /// <summary>Executes the runner protocol with an explicit composition boundary.</summary>
    /// <param name="args">The AppHost arguments.</param>
    /// <param name="plan">The selected run plan.</param>
    /// <param name="signingKey">The per-run signing key.</param>
    /// <param name="compose">The topology composer.</param>
    /// <param name="standardInput">The input owned by the listener for an isolated run, or actual standard input.</param>
    /// <param name="activate">The writer cutover operation, or null for the normal cutover.</param>
    /// <returns>The validation or run exit code.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The retained console owns its synchronized writer for the child process lifetime, including failure retention.")]
    internal static async Task<int> RunAsync(
        string[] args,
        CompositionRunPlan plan,
        string signingKey,
        Action<IDistributedApplicationBuilder, CompositionRunPlan, string> compose,
        Stream? standardInput = null,
        Func<DistributedApplication, CompositionRunPlan, string, CancellationToken, Task>? activate = null)
    {
        if (plan.RetainOnFailure && !args.Contains("--validate-platform", StringComparer.Ordinal))
        {
            // A retained child must not depend on pipes owned by a parent that has exited.
            StreamWriter retainedLog = new(Path.Combine(plan.Workspace, "apphost-retained.log"), append: true) { AutoFlush = true };
            TextWriter synchronizedLog = TextWriter.Synchronized(new RetainedDiagnosticWriter(retainedLog));
            Console.SetOut(synchronizedLog);
            Console.SetError(synchronizedLog);
        }

        IDistributedApplicationBuilder builder = CreateBuilder(args, Environment.GetEnvironmentVariable);
        _ = builder.AddDapr(options =>
        {
            options.DaprPath = RunPlanSource.DaprCliPath(plan);
            options.EnableTelemetry = false;
        });
        try
        {
            compose(builder, plan, signingKey);
        }
        catch (Exception exception) when (args.Contains("--validate-platform", StringComparer.Ordinal)
            && exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 3;
        }

        if (args.Contains("--validate-platform", StringComparer.Ordinal))
        {
            return 0;
        }

        DistributedApplication app = builder.Build();
        if (CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow) == TimeSpan.Zero)
        {
            await using (app.ConfigureAwait(false))
            {
                await RunReadiness.ReportStartupStallAsync(app, plan).ConfigureAwait(false);
                return 3;
            }
        }

        StopSignal stop = StopSignal.Listen(plan, standardInput);
        await using (stop.ConfigureAwait(false))
        await using (app.ConfigureAwait(false))
        {
            int exitCode = 0;
            using CancellationTokenSource readinessBound = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            readinessBound.CancelAfter(CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow));
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
                cutover = (activate ?? WriterProtocolCutover.ActivateAsync)(app, plan, signingKey, readinessBound.Token);
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
                await RunReadiness.ReportFailureAsync(plan, "HXR027", RunTopology.EventStoreName, RunTopology.EventStoreName, status, RunReadiness.CurrentUnready(app), null).ConfigureAwait(false);
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

                await RetainedApplicationLifecycle.StopAsync(plan, exitCode, stop.IsStopRequested, app.WaitForShutdownAsync, app.StopAsync, stop.Token).ConfigureAwait(false);
            }

            return exitCode;
        }
    }

    /// <summary>
    /// Creates the distributed application builder, replacing the AppHost directory and Aspire store path baked
    /// into this assembly's metadata with the run-private directories named by the runner.
    /// </summary>
    /// <param name="args">The AppHost arguments.</param>
    /// <param name="environment">The environment lookup.</param>
    /// <returns>The builder.</returns>
    /// <remarks>
    /// Aspire adds <c>AppHost:Directory</c> and <c>Aspire:Store:Path</c> after the environment configuration source,
    /// so the runner's values are applied here rather than through Aspire configuration keys. The DCP and dashboard
    /// paths are ordinary <c>DcpPublisher</c> configuration that Aspire reads from the environment.
    /// </remarks>
    internal static IDistributedApplicationBuilder CreateBuilder(string[] args, Func<string, string?> environment)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(environment);
        DistributedApplicationOptions options = new()
        {
            Args = [.. args.Where(argument => argument != "--validate-platform")],
            DisableDashboard = true,
            AllowUnsecuredTransport = true,
            EnableResourceLogging = string.Equals(environment(CompositionEnvironment.ResourceLogs), "1", StringComparison.Ordinal),
        };
        string? appHostDirectory = environment(CompositionEnvironment.AppHostDirectory);
        if (!string.IsNullOrWhiteSpace(appHostDirectory))
        {
            options.ProjectDirectory = appHostDirectory;
        }

        IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(options);
        string? storePath = environment(CompositionEnvironment.AspireStorePath);
        if (!string.IsNullOrWhiteSpace(storePath))
        {
            builder.Configuration[_aspireStorePathKey] = storePath;
        }

        return builder;
    }
}
