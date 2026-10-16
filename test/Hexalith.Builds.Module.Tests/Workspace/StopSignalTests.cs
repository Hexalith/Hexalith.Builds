// <copyright file="StopSignalTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks controlled listener cancellation and the bounded noncooperative refusal.</summary>
[Collection("AppHost validation")]
public sealed class StopSignalTests
{
    /// <summary>Verifies the omitted input consumes the real stop protocol in an isolated process.</summary>
    /// <returns>The default-input assertion.</returns>
    [Fact]
    public async Task DefaultInputConsumesStopCommandAsync()
    {
        const string childVariable = "HEXALITH_TEST_STOP_SIGNAL_CHILD";
        if (Environment.GetEnvironmentVariable(childVariable) == "1")
        {
            string root = CompositionTestFiles.CreateDirectory();
            try
            {
                StopSignal signal = StopSignal.Listen(CompositionTestFiles.CreatePlan(root));
                await using (signal.ConfigureAwait(true))
                {
                    _ = await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, signal.Token)
                        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                    signal.IsStopRequested.ShouldBeTrue();
                }
            }
            finally
            {
                CompositionTestFiles.Delete(root);
            }

            return;
        }

        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(StopSignalTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(StopSignalTests).FullName}.{nameof(DefaultInputConsumesStopCommandAsync)}");
        start.Environment[childVariable] = "1";
        start.Environment[CompositionEnvironment.PersistAfterParentExit] = "1";
        using Process child = new() { StartInfo = start };
        child.Start().ShouldBeTrue();
        try
        {
            Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await child.StandardInput.WriteLineAsync(CompositionEnvironment.StopCommand).ConfigureAwait(true);
            await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);

            // Retain the open pipe until exit: EOF must not make a broken default-input binding pass.
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
            string stdout = await output.ConfigureAwait(true);
            string stderr = await error.ConfigureAwait(true);
            child.ExitCode.ShouldBe(0, stdout + stderr);
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Verifies an active cancellable read drains during disposal.</summary>
    /// <returns>The cancellation assertion.</returns>
    [Fact]
    public async Task DisposalCancelsAnActiveReadAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        ControlledStdinStream input = new(ignoreCancellation: false);
        await using (input.ConfigureAwait(true))
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            StopSignal signal = StopSignal.Listen(plan, input);
            CancellationToken stop = signal.Token;
            bool disposed = false;
            try
            {
                await input.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                disposed = true;
                await signal.DisposeAsync().ConfigureAwait(true);
                stop.IsCancellationRequested.ShouldBeTrue();
                input.IsDisposed.ShouldBeTrue();
                File.Exists(CompositionStartupFailure.PathFor(plan.Workspace)).ShouldBeFalse();
            }
            finally
            {
                input.Complete();
                if (!disposed)
                {
                    await signal.DisposeAsync().ConfigureAwait(true);
                }

                CompositionTestFiles.Delete(root);
            }
        }
    }

    /// <summary>Verifies a noncooperative read still fails with the existing HXR028 bound.</summary>
    /// <returns>The bounded-failure assertion.</returns>
    [Fact]
    public async Task DisposalRetainsTheBlockedListenerRefusalAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        ControlledStdinStream input = new(ignoreCancellation: true);
        await using (input.ConfigureAwait(true))
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            StopSignal signal = StopSignal.Listen(plan, input);
            bool disposed = false;
            try
            {
                await input.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                disposed = true;
                Stopwatch elapsed = Stopwatch.StartNew();
                TimeoutException failure = await Should.ThrowAsync<TimeoutException>(() => signal.DisposeAsync().AsTask()
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                elapsed.Stop();
                elapsed.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(2.5));
                elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(6));
                failure.Message.ShouldBe("HXR028 stdin-listener blocked-after-stop");
                input.IsDisposed.ShouldBeTrue();
                CompositionStartupFailure report = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(
                    CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
                report.Schema.ShouldBe(CompositionStartupFailure.SupportedSchema);
                report.RunId.ShouldBe(plan.RunId);
                report.RuleId.ShouldBe("HXR028");
                report.Resource.ShouldBe("stdin-listener");
                report.AppId.ShouldBe("-");
                report.Status.ShouldBe("blocked-after-stop");
            }
            finally
            {
                input.Complete();
                if (!disposed)
                {
                    await signal.DisposeAsync().ConfigureAwait(true);
                }

                CompositionTestFiles.Delete(root);
            }
        }
    }
}
