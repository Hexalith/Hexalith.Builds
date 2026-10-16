// <copyright file="RetainedLifecycleReviewTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Exercises the actual retention orchestration through inert delegates, without starting an application.</summary>
[Collection("AppHost validation")]
public sealed class RetainedLifecycleReviewTests
{
    /// <summary>Retained failures wait for explicit stop, then call the application stop exactly once.</summary>
    /// <returns>The lifecycle assertion.</returns>
    [Fact]
    public async Task RetentionWaitsForExplicitStopAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = true };
            using CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
            int stops = 0;
#pragma warning disable CA2025 // The task is drained below before the stop source is disposed.
            Task lifecycle = RetainedApplicationLifecycle.StopAsync(
                plan,
                3,
                false,
                async token =>
                {
                    waiting.SetResult();
                    await Task.Delay(Timeout.Infinite, token).ConfigureAwait(true);
                },
                _ =>
                {
                    stops++;
                    return Task.CompletedTask;
                },
                stop.Token);
#pragma warning restore CA2025
            await waiting.Task.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            lifecycle.IsCompleted.ShouldBeFalse();
            stops.ShouldBe(0);
            await stop.CancelAsync().ConfigureAwait(true);
            await lifecycle.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            stops.ShouldBe(1);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>A stop callback cannot extend the declared stop allowance by ignoring its token.</summary>
    /// <returns>The bounded stop assertion.</returns>
    [Fact]
    public async Task IgnoredStopTokenRemainsBoundedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        ModuleTestSelection selected = new("sample", "verify", "python3", "runner.py", new string('A', 64), new string('B', 64), 30, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddSeconds(30), DateTimeOffset.UtcNow.AddSeconds(31), "source");
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selected, RetainOnFailure = true };
        Stopwatch elapsed = Stopwatch.StartNew();
        _ = await Should.ThrowAsync<OperationCanceledException>(() => RetainedApplicationLifecycle.StopAsync(plan, 3, true, _ => throw new InvalidOperationException("An explicit stop cannot retain again."), _ => Task.Delay(Timeout.Infinite, CancellationToken.None), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(3));
    }

    /// <summary>Metadata output has a fixed cap and never retains captured values.</summary>
    [Fact]
    public void RetainedMetadataOutputIsBounded()
    {
        using StringWriter output = new();
        using RetainedDiagnosticWriter sink = new(output);
        for (int index = 0; index < 10_000; index++)
        {
            sink.WriteLine("password=never-retain-this");
        }

        output.ToString().ShouldBe(string.Concat(Enumerable.Repeat("apphost.output.observed" + Environment.NewLine, RetainedDiagnosticWriter.MaximumObservations)));
    }

    /// <summary>The retained policy leaves safe validation errors visible to the caller.</summary>
    /// <returns>The preflight diagnostic assertion.</returns>
    [Fact]
    public async Task RetainedPreflightErrorsRemainObservableAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        TextWriter original = Console.Error;
        StringWriter errors = new();
        try
        {
            Console.SetError(errors);
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = true };
            int exit = await AppHostRunner.RunAsync(["--validate-platform"], plan, CompositionSigningKey.Create(), (_, _, _) => throw new IOException("safe preflight refusal")).ConfigureAwait(true);
            exit.ShouldBe(3);
            errors.ToString().ShouldBe("safe preflight refusal" + Environment.NewLine);
            File.Exists(Path.Combine(root, "apphost-retained.log")).ShouldBeFalse();
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            Console.SetError(original);
            await errors.DisposeAsync().ConfigureAwait(true);
            CompositionTestFiles.Delete(root);
        }
    }
}
