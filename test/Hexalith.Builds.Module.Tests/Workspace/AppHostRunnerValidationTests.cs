// <copyright file="AppHostRunnerValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

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
}
