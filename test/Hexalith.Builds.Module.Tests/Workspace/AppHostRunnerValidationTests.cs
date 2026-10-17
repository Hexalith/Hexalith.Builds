// <copyright file="AppHostRunnerValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

using Shouldly;

using Xunit;

/// <summary>Checks the real validation branch without starting a distributed application.</summary>
[Collection("AppHost validation")]
public sealed class AppHostRunnerValidationTests
{
    /// <summary>Verifies all ordinary composition failures become a message-only exit 3.</summary>
    /// <param name="kind">The composition failure kind.</param>
    /// <returns>The validation assertion.</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("provenance")]
    [InlineData("io")]
    [InlineData("format")]
    public async Task ValidationReportsEveryNonFatalCompositionFailureAsync(string kind)
    {
        string root = CompositionTestFiles.CreateDirectory();
        TextWriter original = Console.Error;
        StringWriter errors = new();
        try
        {
            Console.SetError(errors);
            Exception refusal = kind switch
            {
                "json" => new JsonException("local-profile.json: malformed profile"),
                "provenance" => new KeyNotFoundException("provenance.json: missing owner"),
                "io" => new IOException("components.yaml: unreadable file"),
                _ => new FormatException("components.yaml: invalid YAML value"),
            };
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            int exit = await AppHostRunner.RunAsync(
                ["--validate-platform"],
                plan,
                CompositionSigningKey.Create(),
                (_, _, _) => throw refusal).ConfigureAwait(true);
            exit.ShouldBe(3);
            errors.ToString().ShouldBe(refusal.Message + Environment.NewLine);
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            Console.SetError(original);
            await errors.DisposeAsync().ConfigureAwait(true);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies successful validation returns before building or starting the application.</summary>
    /// <returns>The validation assertion.</returns>
    [Fact]
    public async Task ValidPreflightReturnsWithoutStartingResourcesAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            PreflightStartTrap startup = new();
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            bool composed = false;
            int exit = await AppHostRunner.RunAsync(
                ["--validate-platform"],
                plan,
                CompositionSigningKey.Create(),
                (builder, _, _) =>
                {
                    composed = true;
                    builder.Resources.ShouldBeEmpty();
                    _ = builder.Services.AddSingleton<IHostedService>(startup);
                }).ConfigureAwait(true);
            exit.ShouldBe(0);
            composed.ShouldBeTrue();
            startup.Starts.ShouldBe(0);
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies an already elapsed startup deadline reports HXR021 without the former five-minute wait.</summary>
    /// <returns>The deadline assertion.</returns>
    [Fact]
    public async Task ElapsedStartupDeadlineReportsStallWithoutWaitingAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        string? previous = Environment.GetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit);
        Environment.SetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit, "1");
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            int exit = await AppHostRunner.RunAsync([], plan, CompositionSigningKey.Create(), (_, _, _) => { }, standardInput: Stream.Null).ConfigureAwait(true);
            stopwatch.Stop();
            exit.ShouldBe(3);
            stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
            string failure = await File.ReadAllTextAsync(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true);
            failure.ShouldContain("HXR021");
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit, previous);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies an expired deadline returns even when the release runner keeps standard input open.</summary>
    /// <returns>The redirected-input assertion.</returns>
    [Fact]
    public async Task ElapsedStartupDeadlineReturnsWithStandardInputOpenAsync()
    {
        const string childVariable = "HEXALITH_TEST_ELAPSED_DEADLINE_CHILD";
        if (Environment.GetEnvironmentVariable(childVariable) == "1")
        {
            string root = CompositionTestFiles.CreateDirectory();
            try
            {
                CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
                int exit = await AppHostRunner.RunAsync([], plan, CompositionSigningKey.Create(), (_, _, _) => { }).ConfigureAwait(true);
                exit.ShouldBe(3);
                string failure = await File.ReadAllTextAsync(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true);
                failure.ShouldContain("HXR021");
            }
            finally
            {
                CompositionTestFiles.Delete(root);
            }

            return;
        }

        ProcessStartInfo start = new("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in new[]
        {
            typeof(AppHostRunnerValidationTests).Assembly.Location,
            "-method",
            $"{typeof(AppHostRunnerValidationTests).FullName}.{nameof(ElapsedStartupDeadlineReturnsWithStandardInputOpenAsync)}",
            "-noLogo",
        })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[childVariable] = "1";
        using Process process = new() { StartInfo = start };
        process.Start().ShouldBeTrue();
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }

            _ = await Task.WhenAll(output, error).ConfigureAwait(true);
        }

        process.ExitCode.ShouldBe(0, await output.ConfigureAwait(true) + await error.ConfigureAwait(true));
    }

    /// <summary>Verifies readiness progress and a stall document carry the unready resource and its process id.</summary>
    /// <returns>The observation assertion.</returns>
    [Fact]
    public async Task ReadinessProgressAndStallRecordProcessIdAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { Modules = [], UiMarkers = [] };
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            });
            _ = builder.AddExecutable("worker", "/bin/true", root);
            DistributedApplication app = builder.Build();
            try
            {
                ExecutableResource resource = app.Services.GetRequiredService<DistributedApplicationModel>().Resources.OfType<ExecutableResource>().Single();
                ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
                await notifications.PublishUpdateAsync(
                    resource,
                    snapshot => (snapshot with
                    {
                        State = new ResourceStateSnapshot(KnownResourceStates.Running, string.Empty),
                        Properties = [new ResourcePropertySnapshot("executable.pid", 4242)],
                    }).WithHealthReports([new HealthReportSnapshot("ready", HealthStatus.Unhealthy, "connection-unavailable", string.Empty)])).ConfigureAwait(true);

                await RunReadiness.ReportStartupStallAsync(app, plan).ConfigureAwait(true);
                CompositionStartupFailure? failure = await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(
                    CompositionStartupFailure.PathFor(plan.Workspace),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = failure.ShouldNotBeNull();
                failure.RuleId.ShouldBe("HXR021");
                failure.UnreadyResources.ShouldContain(item => item.Name == "worker" && item.State == "Unhealthy" && item.ProcessId == 4242);
                failure.UnreadyResources.ShouldNotContain(item => item.State == "Healthy");

                using CancellationTokenSource cancel = new();
#pragma warning disable CA2025 // The readiness task is cancelled and drained in the finally before this source is disposed.
                Task<bool> waiting = RunReadiness.WaitAndWriteAsync(app, plan, cancel.Token);
#pragma warning restore CA2025
                try
                {
                    DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(5);
                    string progressPath = CompositionReadinessProgress.PathFor(plan.Workspace);
                    while (!File.Exists(progressPath) && DateTimeOffset.UtcNow < until)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).ConfigureAwait(true);
                    }

                    CompositionReadinessProgress? progress = await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(progressPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    _ = progress.ShouldNotBeNull();
                    progress.Resources.ShouldContain(item => item.Name == "worker" && item.ProcessId == 4242);
                }
                finally
                {
                    await cancel.CancelAsync().ConfigureAwait(true);
                    try
                    {
                        _ = await waiting.ConfigureAwait(true);
                    }
                    catch (OperationCanceledException)
                    {
                        // The caller cancelled the resource wait after the progress file was written.
                    }
                }
            }
            finally
            {
                await app.DisposeAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies successful real readiness records launcher ids, results, deadline and the final progress snapshot.</summary>
    /// <param name="processId">The Aspire process property representation.</param>
    /// <returns>The readiness document assertions.</returns>
    [Theory]
    [InlineData(4242)]
    [InlineData(4242L)]
    [InlineData("4242")]
    public async Task HealthyReadinessWritesMetadataAndFinalProgressAsync(object processId)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [],
                UiMarkers = [],
                EffectiveDeadline = new CompositionEffectiveDeadline(1200, true, "replay required", "sample"),
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(1200),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            ExecutableResource resource = builder.AddExecutable("eventstore", "unused", root).WithHttpEndpoint(port: 32123).Resource;
            EndpointAnnotation endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 32123);
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, resource, HealthStatus.Healthy, processId).ConfigureAwait(true);

            using CancellationTokenSource readyBound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            readyBound.CancelAfter(TimeSpan.FromSeconds(5));
            (await RunReadiness.WaitAndWriteAsync(app, plan, readyBound.Token).ConfigureAwait(true)).ShouldBeTrue();

            CompositionReadiness readiness = (await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(plan.ReadinessPath, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            CompositionResourceReadiness ready = readiness.Resources.Single();
            ready.ProcessId.ShouldBe(4242);
            ready.Detail.ShouldBe("module-ready");
            readiness.EffectiveDeadlineSeconds.ShouldBe(1200);
            readiness.EffectiveDeadlineIsOverride.ShouldBeTrue();
            readiness.EffectiveDeadlineJustification.ShouldBe("replay required");
            readiness.EffectiveDeadlineModuleId.ShouldBe("sample");
            CompositionReadinessProgress progress = (await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            progress.Resources.Single().State.ShouldBe("Healthy");
            progress.Resources.Single().ProcessId.ShouldBe(4242);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies initial readiness waits when an earlier resource regresses while a later module initializes.</summary>
    /// <returns>The simultaneous health and recovery assertions.</returns>
    [Fact]
    public async Task InitialReadinessRechecksEarlierHealthBeforePublishingAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [new CompositionRunModule("worker", "worker", "worker-app", Path.Combine(root, "worker.csproj"))],
                UiMarkers = [],
                Ports = CompositionPortAllocator.Allocate(1),
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            ExecutableResource first = builder.AddExecutable("eventstore", "unused", root).WithHttpEndpoint(port: 32123).Resource;
            EndpointAnnotation endpoint = first.Annotations.OfType<EndpointAnnotation>().Single();
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 32123);
            ExecutableResource last = builder.AddExecutable("worker", "unused", root).Resource;
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, first, HealthStatus.Healthy, 123).ConfigureAwait(true);
            await PublishAsync(notifications, last, HealthStatus.Healthy, 456).ConfigureAwait(true);
            using HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{plan.Ports.EventStoreDaprHttp}/");
            listener.Start();
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(8));
#pragma warning disable CA2025 // The pending readiness task is cancelled and drained before the bound and application are disposed.
            Task<bool> waiting = RunReadiness.WaitAndWriteAsync(app, plan, bound.Token);
#pragma warning restore CA2025
            try
            {
                // The later module's invocation handshake proves the earlier resource already passed its wait.
                HttpListenerContext invocation = await listener.GetContextAsync().WaitAsync(bound.Token).ConfigureAwait(true);
                try
                {
                    await PublishAsync(notifications, first, HealthStatus.Unhealthy, 123).ConfigureAwait(true);
                }
                finally
                {
                    invocation.Response.StatusCode = 200;
                    invocation.Response.Close();
                }

                string progressPath = CompositionReadinessProgress.PathFor(plan.Workspace);
                CompositionReadinessProgress? progress;
                do
                {
                    progress = await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(progressPath, bound.Token).ConfigureAwait(true);
                    if (progress?.Resources.Any(resource => resource.Name == "eventstore" && resource.State == "Unhealthy") == true)
                    {
                        break;
                    }

                    await Task.Delay(10, bound.Token).ConfigureAwait(true);
                }
                while (true);
                Task winner = await Task.WhenAny(waiting, Task.Delay(TimeSpan.FromMilliseconds(300), bound.Token)).ConfigureAwait(true);
                winner.ShouldNotBe(waiting);
                File.Exists(plan.ReadinessPath).ShouldBeFalse();

                await PublishAsync(notifications, first, HealthStatus.Healthy, 789).ConfigureAwait(true);
                HttpListenerContext retry = await listener.GetContextAsync().WaitAsync(bound.Token).ConfigureAwait(true);
                retry.Response.StatusCode = 200;
                retry.Response.Close();
                (await waiting.ConfigureAwait(true)).ShouldBeTrue();
                CompositionReadiness ready = (await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(plan.ReadinessPath, bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                ready.Resources.ShouldAllBe(resource => resource.State == "Healthy");
                ready.Resources.Single(resource => resource.Name == "eventstore").ProcessId.ShouldBe(789);
            }
            finally
            {
                await bound.CancelAsync().ConfigureAwait(true);
                try
                {
                    _ = await waiting.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Assertion failures still drain the bounded startup before disposing the application.
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies real resource-probe expiry uses the remaining deadline and includes every unready resource.</summary>
    /// <returns>The timeout document assertions.</returns>
    [Fact]
    public async Task ResourceProbeUsesDeadlineAndWritesEveryUnreadyResourceAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { Modules = [], UiMarkers = [], StartupDeadlineUtc = DateTimeOffset.UtcNow.AddMilliseconds(250) };
            IDistributedApplicationBuilder builder = CreateBuilder();
            ExecutableResource first = builder.AddExecutable("first", "unused", root).Resource;
            ExecutableResource second = builder.AddExecutable("second", "unused", root).Resource;
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, first, HealthStatus.Unhealthy, 123).ConfigureAwait(true);
            await PublishAsync(notifications, second, HealthStatus.Unhealthy, null).ConfigureAwait(true);
            using CancellationTokenSource caller = new(TimeSpan.FromSeconds(5));
            (await RunReadiness.WaitAndWriteAsync(app, plan, caller.Token).ConfigureAwait(true)).ShouldBeFalse();
            CompositionStartupFailure failure = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            failure.RuleId.ShouldBe("HXR025");
            failure.UnreadyResources.Select(resource => resource.Name).ShouldBe(["first", "second"]);
            failure.UnreadyResources.ShouldAllBe(resource => resource.State == "Unhealthy");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a real runner cutover failure reports the current unready list.</summary>
    /// <returns>The HXR027 document assertions.</returns>
    [Fact]
    public async Task CutoverFailureWritesEveryUnreadyResourceAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        string? previous = Environment.GetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit);
        Environment.SetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit, "1");
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { Modules = [], UiMarkers = [], StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10) };
            int exit = await AppHostRunner.RunAsync(
                [],
                plan,
                CompositionSigningKey.Create(),
                (builder, _, _) =>
                {
                    _ = builder.AddExecutable("first", "unused", root);
                    _ = builder.AddExecutable("second", "unused", root);
                },
                standardInput: Stream.Null,
                activate: (app, _, _, _) =>
                {
                    RunReadiness.CurrentUnready(app).Count.ShouldBe(2);
                    return Task.FromException(new InvalidOperationException("cutover-activation-HTTP-403"));
                }).ConfigureAwait(true);
            exit.ShouldBe(3);
            CompositionStartupFailure failure = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            failure.RuleId.ShouldBe("HXR027");
            failure.UnreadyResources.Select(resource => resource.Name).ShouldBe(["first", "second"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(CompositionEnvironment.PersistAfterParentExit, previous);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the last resource's new health is persisted before an invocation probe can stall.</summary>
    /// <returns>The progress assertions.</returns>
    [Fact]
    public async Task ReadinessRefreshesProgressBeforeInvocationAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [new CompositionRunModule("worker", "worker", "worker-app", Path.Combine(root, "worker.csproj"))],
                UiMarkers = [],
                Ports = CompositionPortAllocator.Allocate(1),
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            ExecutableResource first = builder.AddExecutable("eventstore", "unused", root).Resource;
            ExecutableResource last = builder.AddExecutable("worker", "unused", root).Resource;
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, first, HealthStatus.Healthy, 123).ConfigureAwait(true);
            await PublishAsync(notifications, last, HealthStatus.Unhealthy, 456).ConfigureAwait(true);
            using HttpListener listener = new();
            listener.Prefixes.Add($"http://127.0.0.1:{plan.Ports.EventStoreDaprHttp}/");
            listener.Start();
            using CancellationTokenSource caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
#pragma warning disable CA2025 // The readiness task is cancelled and drained before the source and app are disposed.
            Task<bool> waiting = RunReadiness.WaitAndWriteAsync(app, plan, caller.Token);
#pragma warning restore CA2025
            try
            {
                string path = CompositionReadinessProgress.PathFor(plan.Workspace);
                DateTimeOffset until = DateTimeOffset.UtcNow.AddSeconds(5);
                CompositionReadinessProgress? progress;
                do
                {
                    progress = await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    if (progress?.Resources.Any(resource => resource.Name == "worker" && resource.State == "Unhealthy") == true)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
                }
                while (DateTimeOffset.UtcNow < until);
                _ = progress.ShouldNotBeNull();
                progress.Resources.ShouldContain(resource => resource.Name == "worker" && resource.State == "Unhealthy");
                await PublishAsync(notifications, last, HealthStatus.Healthy, 456).ConfigureAwait(true);
                HttpListenerContext invocation = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                try
                {
                    progress = await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    progress.ShouldNotBeNull().Resources.ShouldAllBe(resource => resource.State == "Healthy");
                }
                finally
                {
                    invocation.Response.StatusCode = 503;
                    invocation.Response.Close();
                }
            }
            finally
            {
                await caller.CancelAsync().ConfigureAwait(true);
                try
                {
                    _ = await waiting.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // End the pending invocation after checking its progress snapshot.
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a post-task refresh evaluates module health even when Aspire's cached snapshot is healthy.</summary>
    /// <returns>The fresh health and nonce-correlation assertions.</returns>
    [Fact]
    public async Task ReadinessRefreshRechecksModuleHealthBeforeAcknowledgingAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [],
                UiMarkers = [],
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            int healthy = 0;
            TaskCompletionSource evaluated = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = builder.Services.AddHealthChecks().AddCheck("module-defined", () =>
            {
                _ = evaluated.TrySetResult();
                return Volatile.Read(ref healthy) == 1 ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
            });
            ExecutableResource resource = builder.AddExecutable("eventstore", "unused", root).WithHttpEndpoint(port: 32123).WithHealthCheck("module-defined").Resource;
            EndpointAnnotation endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 32123);
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, resource, HealthStatus.Healthy, 123).ConfigureAwait(true);
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(8));
#pragma warning disable CA2025 // The pending refresh is cancelled and drained in finally before the bound and app are disposed.
            Task<bool> refresh = RunReadiness.RefreshAsync(app, plan, CompositionTestFiles.RunId, bound.Token);
#pragma warning restore CA2025
            try
            {
                await evaluated.Task.WaitAsync(bound.Token).ConfigureAwait(true);
                refresh.IsCompleted.ShouldBeFalse();
                while (!File.Exists(CompositionReadinessProgress.PathFor(plan.Workspace)))
                {
                    await Task.Delay(10, bound.Token).ConfigureAwait(true);
                }

                CompositionReadinessProgress progress = (await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(plan.Workspace), bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                progress.Resources.Single().State.ShouldBe("Unhealthy");
                File.Exists(plan.ReadinessPath).ShouldBeFalse();
                await PublishAsync(notifications, resource, HealthStatus.Healthy, 456).ConfigureAwait(true);
                _ = Interlocked.Exchange(ref healthy, 1);
                (await refresh.ConfigureAwait(true)).ShouldBeTrue();
                CompositionReadiness ready = (await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(plan.ReadinessPath, bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                ready.RefreshId.ShouldBe(CompositionTestFiles.RunId);
                ready.Resources.Single().ProcessId.ShouldBe(456);
                ready.Resources.Single().Detail.ShouldBe("module-ready");
            }
            finally
            {
                await bound.CancelAsync().ConfigureAwait(true);
                try
                {
                    _ = await refresh.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Assertion failures still drain the bounded refresh before disposing the application.
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies running state alone cannot satisfy a refresh when the module has no health check.</summary>
    /// <param name="name">The unannotated resource name, including a sidecar-like name.</param>
    /// <returns>The missing-health assertions.</returns>
    [Theory]
    [InlineData("module")]
    [InlineData("module-dapr-cli")]
    public async Task ReadinessRefreshRefusesMissingModuleHealthAsync(string name)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            IDistributedApplicationBuilder builder = CreateBuilder();
            ExecutableResource resource = builder.AddExecutable(name, "unused", root).Resource;
            await using DistributedApplication app = builder.Build();
            await PublishAsync(app.Services.GetRequiredService<ResourceNotificationService>(), resource, HealthStatus.Healthy, 123).ConfigureAwait(true);
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromMilliseconds(400));
            _ = await Should.ThrowAsync<OperationCanceledException>(() => RunReadinessHealth.WaitAsync(app, plan, [resource], bound.Token)).ConfigureAwait(true);
            CompositionReadinessProgress progress = (await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(CompositionReadinessProgress.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            progress.Resources.Single().Detail.ShouldBe("module-health-unavailable");
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a post-task refresh requires an actual infrastructure protocol response alongside module health.</summary>
    /// <param name="infrastructureHealthy">Whether Redis answers PING successfully.</param>
    /// <returns>The fresh composed readiness assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReadinessRefreshRequiresInfrastructureProtocolHealthAsync(bool infrastructureHealthy)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [],
                UiMarkers = [],
                StartupDeadlineUtc = DateTimeOffset.UtcNow.AddSeconds(10),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            _ = builder.Services.AddHealthChecks()
                .AddCheck("module-defined", () => HealthCheckResult.Healthy())
                .AddCheck("infrastructure-redis", new RedisPingHealthCheck(port));
            ExecutableResource module = builder.AddExecutable("eventstore", "unused", root).WithHttpEndpoint(port: 32123).WithHealthCheck("module-defined").Resource;
            EndpointAnnotation endpoint = module.Annotations.OfType<EndpointAnnotation>().Single();
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 32123);
            ContainerResource infrastructure = builder.AddContainer("redis", "unused").WithHealthCheck("infrastructure-redis").Resource;
            await using DistributedApplication app = builder.Build();
            ResourceNotificationService notifications = app.Services.GetRequiredService<ResourceNotificationService>();
            await PublishAsync(notifications, module, HealthStatus.Healthy, 123).ConfigureAwait(true);
            await PublishAsync(notifications, infrastructure, HealthStatus.Healthy, null).ConfigureAwait(true);
            (string? failure, bool elapsed) = await CompositionStartupTasks.RunAsync(
                [new CompositionStartupTask("sample", "verify", OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : "/bin/sh", OperatingSystem.IsWindows() ? ["/c", "exit 0"] : ["-c", "exit 0"])],
                plan.StartupDeadlineUtc.Value,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            failure.ShouldBeNull();
            elapsed.ShouldBeFalse();
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(5));
#pragma warning disable CA2025 // The refresh is cancelled and drained before the listener, token source and app are disposed.
            Task<bool> refresh = RunReadiness.RefreshAsync(app, plan, CompositionTestFiles.RunId, bound.Token);
#pragma warning restore CA2025
            try
            {
                using TcpClient client = await listener.AcceptTcpClientAsync(bound.Token).ConfigureAwait(true);
                NetworkStream stream = client.GetStream();
                byte[] request = new byte[6];
                await stream.ReadExactlyAsync(request, bound.Token).ConfigureAwait(true);
                request.ShouldBe("PING\r\n"u8.ToArray());
                await stream.WriteAsync(infrastructureHealthy ? "+PONG\r\n"u8.ToArray() : "-ERR unavailable\r\n"u8.ToArray(), bound.Token).ConfigureAwait(true);
                if (infrastructureHealthy)
                {
                    (await refresh.ConfigureAwait(true)).ShouldBeTrue();
                    CompositionReadiness ready = (await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(plan.ReadinessPath, bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                    ready.RefreshId.ShouldBe(CompositionTestFiles.RunId);
                    ready.Resources.Select(resource => resource.Name).ShouldBe(["eventstore", "redis"]);
                    ready.Resources.ShouldAllBe(resource => resource.State == "Healthy");
                }
                else
                {
                    string path = CompositionReadinessProgress.PathFor(plan.Workspace);
                    while (!File.Exists(path))
                    {
                        await Task.Delay(10, bound.Token).ConfigureAwait(true);
                    }

                    CompositionReadinessProgress progress = (await CompositionDocumentStore.TryReadAsync<CompositionReadinessProgress>(path, bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                    progress.Resources.Single().Name.ShouldBe("redis");
                    progress.Resources.Single().Detail.ShouldBe("module-health-unhealthy");
                    refresh.IsCompleted.ShouldBeFalse();
                    File.Exists(plan.ReadinessPath).ShouldBeFalse();
                }
            }
            finally
            {
                await bound.CancelAsync().ConfigureAwait(true);
                try
                {
                    _ = await refresh.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // A failed infrastructure check remains unready until the original bound cancels it.
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Drives the real private refresh handler through success, refusal, and deadline expiry.</summary>
    /// <param name="outcome">The expected refresh outcome.</param>
    /// <returns>The correlated response and continued-service assertions.</returns>
    [Theory]
    [InlineData("ok")]
    [InlineData("module-unready")]
    [InlineData("expiry")]
    public async Task ResourceControlRefreshReturnsCorrelatedResponseAndContinuesAsync(string outcome)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                Modules = [],
                UiMarkers = [],
                StartupDeadlineUtc = now.AddSeconds(outcome == "expiry" ? 1 : 10),
                SelectedTest = new("orders", "smoke", "python3", "verify.py", "hash", "hash", 10, 5, now, now.AddSeconds(10), now.AddSeconds(10), "source"),
            };
            IDistributedApplicationBuilder builder = CreateBuilder();
            _ = builder.Services.AddHealthChecks().AddCheck("module-defined", () => outcome == "expiry" ? HealthCheckResult.Unhealthy() : HealthCheckResult.Healthy());
            if (outcome == "module-unready")
            {
                _ = builder.Services.AddSingleton<ICompositionTestObservationProvider>(new IncompleteTestObservationProvider(plan.RunId, "orders"));
            }

            ExecutableResource resource = builder.AddExecutable("eventstore", "unused", root).WithHttpEndpoint(port: 32123).WithHealthCheck("module-defined").Resource;
            EndpointAnnotation endpoint = resource.Annotations.OfType<EndpointAnnotation>().Single();
            endpoint.AllocatedEndpoint = new AllocatedEndpoint(endpoint, "127.0.0.1", 32123);
            await using DistributedApplication app = builder.Build();
            await PublishAsync(app.Services.GetRequiredService<ResourceNotificationService>(), resource, HealthStatus.Healthy, 4242).ConfigureAwait(true);
            using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            bound.CancelAfter(TimeSpan.FromSeconds(12));
#pragma warning disable CA2025 // The owned handler is cancelled and drained in finally before its app and token are disposed.
            Task control = RunResourceControl.RunAsync(app, plan, bound.Token);
#pragma warning restore CA2025
            try
            {
                string nonce = CompositionRunPlanFactory.NewRunId();
                await CompositionDocumentStore.WriteAsync(CompositionControlRequest.PathFor(plan.Workspace), new CompositionControlRequest(CompositionControlRequest.SupportedSchema, plan.RunId, nonce, "refresh-readiness"), bound.Token).ConfigureAwait(true);
                CompositionControlResponse response = await ReadControlResponseAsync(plan, nonce, bound.Token).ConfigureAwait(true);
                response.RunId.ShouldBe(plan.RunId);
                response.Command.ShouldBe("refresh-readiness");
                response.Success.ShouldBe(outcome == "ok");
                response.FailureStep.ShouldBe(outcome == "ok" ? null : "module-unready");
                if (outcome == "ok")
                {
                    CompositionReadiness readiness = (await CompositionDocumentStore.TryReadAsync<CompositionReadiness>(plan.ReadinessPath, bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                    readiness.RefreshId.ShouldBe(nonce);
                }
                else if (outcome == "module-unready")
                {
                    CompositionStartupFailure failure = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(plan.Workspace), bound.Token).ConfigureAwait(true)).ShouldNotBeNull();
                    failure.RuleId.ShouldBe("HXR026");
                    failure.AppId.ShouldBe("orders");
                }

                string followingNonce = CompositionRunPlanFactory.NewRunId();
                await CompositionDocumentStore.WriteAsync(CompositionControlRequest.PathFor(plan.Workspace), new CompositionControlRequest(CompositionControlRequest.SupportedSchema, plan.RunId, followingNonce, "unknown"), bound.Token).ConfigureAwait(true);
                (await ReadControlResponseAsync(plan, followingNonce, bound.Token).ConfigureAwait(true)).FailureStep.ShouldBe("invalid-request");
            }
            finally
            {
                await bound.CancelAsync().ConfigureAwait(true);
                try
                {
                    await control.ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    // Caller cancellation stops the handler after its final response.
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static async Task<CompositionControlResponse> ReadControlResponseAsync(CompositionRunPlan plan, string nonce, CancellationToken cancellationToken)
    {
        while (true)
        {
            CompositionControlResponse? response = await CompositionDocumentStore.TryReadAsync<CompositionControlResponse>(CompositionControlResponse.PathFor(plan.Workspace), cancellationToken).ConfigureAwait(false);
            if (response?.Nonce == nonce)
            {
                return response;
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IDistributedApplicationBuilder CreateBuilder() => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
    {
        Args = [],
        AssemblyName = typeof(RunTopology).Assembly.GetName().Name,
        DisableDashboard = true,
        AllowUnsecuredTransport = true,
    });

    private static Task PublishAsync(ResourceNotificationService notifications, IResource resource, HealthStatus health, object? processId) =>
        notifications.PublishUpdateAsync(resource, snapshot =>
        {
            CustomResourceSnapshot observed = (snapshot with
            {
                State = new ResourceStateSnapshot(KnownResourceStates.Running, string.Empty),
                Properties = processId is null ? [] : [new ResourcePropertySnapshot("executable.pid", processId)],
            }).WithHealthReports([new HealthReportSnapshot("ready", health, "module-ready", string.Empty)]);
            if (health == HealthStatus.Healthy)
            {
                // Aspire also waits for its internal resource-ready event after the module health check succeeds.
                PropertyInfo ready = typeof(CustomResourceSnapshot).GetProperty("ResourceReadyEvent", BindingFlags.Instance | BindingFlags.NonPublic)!;
                ready.SetValue(observed, Activator.CreateInstance(ready.PropertyType, Task.CompletedTask));
            }

            return observed;
        });
}
