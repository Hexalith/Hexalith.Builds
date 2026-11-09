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
    /// <summary>Gets a value indicating whether the redirected Linux input control applies.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Disposes a real redirected standard-input read while its owner still holds the writer open.</summary>
    /// <returns>The isolated input disposal assertions; no topology is composed.</returns>
    [Fact(Skip = "The cancellable redirected-input binding is Linux-specific.", SkipUnless = nameof(IsLinux))]
    public async Task StandardInputDisposalDrainsWhileOwnerWriterRemainsOpenAsync()
    {
        const string childVariable = "HEXALITH_TEST_STDIN_DISPOSAL_CHILD";
        if (Environment.GetEnvironmentVariable(childVariable) == "1")
        {
            StdinChildProgress.Mark("child-fact");
            await RunPendingReadDisposalChildAsync().ConfigureAwait(true);
            return;
        }

        await RunPendingReadDisposalOwnerAsync(childVariable).ConfigureAwait(true);
    }

    /// <summary>Preserves redirected file input and retained end-of-input behavior.</summary>
    /// <returns>The two nonpipe fallback assertions without composing a topology.</returns>
    [Fact(Skip = "These redirected file descriptors are Linux-specific.", SkipUnless = nameof(IsLinux))]
    public async Task NonPipeStandardInputPreservesStopAndRetainedEofAsync()
    {
        const string childVariable = "HEXALITH_TEST_STDIN_NONPIPE_CHILD";
        string? mode = Environment.GetEnvironmentVariable(childVariable);
        if (mode is not null)
        {
            await RunNonPipeInputChildAsync(mode).ConfigureAwait(true);
            return;
        }

        await RunNonPipeInputOwnersAsync(childVariable).ConfigureAwait(true);
    }

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

    /// <summary>Verifies a broken input pipe retains only runs explicitly configured for retention.</summary>
    /// <param name="retain">Whether failure retention is enabled.</param>
    /// <returns>The input failure assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InputIoFailureHonorsRetentionAsync(bool retain)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            ControlledStdinStream input = new(ignoreCancellation: false);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownedInput = input.ConfigureAwait(true);
            StopSignal signal = StopSignal.Listen(CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = retain }, input);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownedSignal = signal.ConfigureAwait(true);
            await input.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
            input.Fail();
            if (retain)
            {
                _ = await Should.ThrowAsync<TimeoutException>(() => Task.Delay(Timeout.InfiniteTimeSpan, signal.Token)
                    .WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                signal.IsStopRequested.ShouldBeFalse();
            }
            else
            {
                _ = await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, signal.Token)
                    .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                signal.IsStopRequested.ShouldBeTrue();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
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

    /// <summary>Drains both real reads while managed console output is captured, then consumes the real stop command.</summary>
    /// <returns>The focused control operation.</returns>
    private static async Task RunPendingReadDisposalChildAsync()
    {
        StdinChildProgress.Mark("workspace-create");
        string root = CompositionTestFiles.CreateDirectory();
        StdinChildProgress.Mark("workspace-created");
        TextWriter originalOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            foreach (bool retain in new[] { false, true })
            {
                await DrainOwnedPendingReadAsync(root, retain).ConfigureAwait(true);
            }

            // A second listener must still be able to consume the owner's command from fd 0.
            StdinChildProgress.Mark("next-listener-create", retain: false);
            StopSignal next = StopSignal.Listen(CompositionTestFiles.CreatePlan(root));
            await using (next.ConfigureAwait(true))
            {
                StdinChildProgress.Mark("next-listener-created");
                StdinChildProgress.Mark("readiness-write");
                await WriteReadinessToOwnedOutputAsync().ConfigureAwait(true);
                StdinChildProgress.Mark("readiness-written");
                await StdinChildAcknowledgement.PublishAsync(
                    Environment.GetEnvironmentVariable(StdinChildProgress.DirectoryVariable).ShouldNotBeNull(),
                    Environment.GetEnvironmentVariable(StdinChildAcknowledgement.TokenVariable).ShouldNotBeNull(),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                StdinChildProgress.Mark("await-stop-command");
                _ = await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, next.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                next.IsStopRequested.ShouldBeTrue();
                StdinChildProgress.Mark("stop-command-received");
                StdinChildProgress.Mark("next-listener-dispose");
            }

            StdinChildProgress.Mark("next-listener-disposed");
        }
        finally
        {
            StdinChildProgress.Mark("workspace-cleanup");
            CompositionTestFiles.Delete(root);
            Console.SetOut(originalOutput);
            StdinChildProgress.Mark("child-complete");
        }
    }

    /// <summary>Owns the redirected child and keeps stdin open through the exact readiness handshake.</summary>
    /// <param name="childVariable">The owned control childVariable.</param>
    /// <returns>The focused control operation.</returns>
    private static async Task RunPendingReadDisposalOwnerAsync(string childVariable)
    {
        string directory = CompositionTestFiles.CreateDirectory();
        try
        {
            await RunStagedPendingReadOwnerAsync(childVariable, directory).ConfigureAwait(true);
        }
        finally
        {
            CompositionTestFiles.Delete(directory);
        }
    }

    /// <summary>Waits once for the exact private acknowledgement within the original readiness bound.</summary>
    /// <param name="child">The owned control child.</param>
    /// <param name="directory">The actual child directory.</param>
    /// <param name="token">The exact parent-bound ready token.</param>
    /// <exception cref="TimeoutException">The explicit acknowledgement missed the original bound.</exception>
    /// <returns>The focused control operation.</returns>
    private static async Task ReadReadinessFromChildAsync(Process child, string directory, string token)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await StdinChildAcknowledgement.WaitAsync(directory, token, deadline.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(StdinChildAcknowledgement.TimeoutDiagnostic(directory, child.HasExited));
        }
    }

    /// <summary>Writes the control marker directly to owned stdout, independently of managed console capture.</summary>
    /// <returns>The focused control operation.</returns>
    private static async Task WriteReadinessToOwnedOutputAsync()
    {
        Stream output = Console.OpenStandardOutput();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownedOutput = output.ConfigureAwait(true);
        StreamWriter writer = new(output, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: 1024, leaveOpen: true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownedWriter = writer.ConfigureAwait(true);
        await writer.WriteLineAsync("stdin-control ready-for-command").ConfigureAwait(true);
        await writer.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    /// <summary>Checks the original file stop or retained null-input behavior in its private child.</summary>
    /// <param name="mode">The owned control mode.</param>
    /// <returns>The focused control operation.</returns>
    private static async Task RunNonPipeInputChildAsync(string mode)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            Stream input = StopSignalInput.Open();
            await using (input.ConfigureAwait(true))
            {
                input.GetType().FullName.ShouldBe("System.ConsolePal+UnixConsoleStream");
                CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = true };
                StopSignal signal = StopSignal.Listen(plan, input);
                await using (signal.ConfigureAwait(true))
                {
                    if (mode == "file")
                    {
                        _ = await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, signal.Token)
                            .WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                        signal.IsStopRequested.ShouldBeTrue();
                    }
                    else
                    {
                        mode.ShouldBe("null");
                        _ = await Should.ThrowAsync<TimeoutException>(() => Task.Delay(Timeout.InfiniteTimeSpan, signal.Token)
                            .WaitAsync(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                        signal.IsStopRequested.ShouldBeFalse();
                    }
                }

                File.Exists(CompositionStartupFailure.PathFor(plan.Workspace)).ShouldBeFalse();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Exhausts both original nonpipe children and their exact cleanup paths.</summary>
    /// <param name="childVariable">The owned control childVariable.</param>
    /// <returns>The focused control operation.</returns>
    private static async Task RunNonPipeInputOwnersAsync(string childVariable)
    {
        string fixture = CompositionTestFiles.CreateDirectory();
        try
        {
            string file = Path.Combine(fixture, "stdin.txt");
            await File.WriteAllTextAsync(file, CompositionEnvironment.StopCommand + "\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
            foreach (string source in new[] { file, "/dev/null" })
            {
                ProcessStartInfo start = new("/bin/sh")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add("exec dotnet \"$1\" -method \"$2\" < \"$3\"");
                start.ArgumentList.Add("stdin-file-control");
                start.ArgumentList.Add(typeof(StopSignalTests).Assembly.Location);
                start.ArgumentList.Add($"{typeof(StopSignalTests).FullName}.{nameof(NonPipeStandardInputPreservesStopAndRetainedEofAsync)}");
                start.ArgumentList.Add(source);
                start.Environment[childVariable] = source == file ? "file" : "null";
                using Process child = new() { StartInfo = start };
                child.Start().ShouldBeTrue();
                try
                {
                    Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
                    Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
                    await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
                    child.ExitCode.ShouldBe(0, await output.ConfigureAwait(true) + await error.ConfigureAwait(true));
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
        }
        finally
        {
            CompositionTestFiles.Delete(fixture);
        }
    }

    /// <summary>Checks one real pending read and records its actual disposal boundaries.</summary>
    /// <param name="root">The private plan directory.</param>
    /// <param name="retain">The unchanged retention setting.</param>
    /// <returns>The original pending-read assertions.</returns>
    private static async Task DrainOwnedPendingReadAsync(string root, bool retain)
    {
        StdinChildProgress.Mark("input-open", retain: retain);
        Stream actual = StopSignalInput.Open();
        await using (actual.ConfigureAwait(true))
        {
            StdinChildProgress.Mark("input-opened");
            actual.GetType().FullName.ShouldBe("System.IO.Pipes.AnonymousPipeClientStream");
            ObservedStdinStream input = new(actual);
            await using (input.ConfigureAwait(true))
            {
                CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = retain };
                StdinChildProgress.Mark("listener-create");
                StopSignal signal = StopSignal.Listen(plan, input);
                StdinChildProgress.Mark("listener-created");
                StdinChildProgress.Mark("await-read");
                await input.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                StdinChildProgress.Mark("read-requested");
                input.WasReadPending.ShouldBeTrue();
                StdinChildProgress.Mark("signal-dispose");
                await signal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
                StdinChildProgress.Mark("signal-disposed");
                File.Exists(CompositionStartupFailure.PathFor(plan.Workspace)).ShouldBeFalse();
                StdinChildProgress.Mark("wrapper-dispose");
            }

            StdinChildProgress.Mark("wrapper-disposed");
            StdinChildProgress.Mark("raw-dispose");
        }

        StdinChildProgress.Mark("raw-disposed");
    }

    /// <summary>Owns only this child's stage fixture and the original real stdin process.</summary>
    /// <param name="childVariable">The existing private child mode.</param>
    /// <param name="directory">The fresh parent-owned stage directory.</param>
    /// <returns>The original real-child handshake and cleanup.</returns>
    private static async Task RunStagedPendingReadOwnerAsync(string childVariable, string directory)
    {
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(StopSignalTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(StopSignalTests).FullName}.{nameof(StandardInputDisposalDrainsWhileOwnerWriterRemainsOpenAsync)}");
        start.Environment[childVariable] = "1";
        start.Environment[StdinChildProgress.DirectoryVariable] = directory;
        string token = Guid.NewGuid().ToString("N");
        start.Environment[StdinChildAcknowledgement.TokenVariable] = token;
        using Process child = new() { StartInfo = start };
        child.Start().ShouldBeTrue();
        Task<string> output = Task.FromResult(string.Empty);
        Task<string> error = Task.FromResult(string.Empty);
        try
        {
            output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await ReadReadinessFromChildAsync(child, directory, token).ConfigureAwait(true);

            // Neither a stop command nor EOF was sent until both pending reads actually drained.
            await child.StandardInput.WriteLineAsync(CompositionEnvironment.StopCommand).ConfigureAwait(true);
            await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
            child.ExitCode.ShouldBe(0, await output.ConfigureAwait(true) + await error.ConfigureAwait(true));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }

            StdinChildProgress.Report(directory, child.HasExited);
            _ = await Task.WhenAll(output, error).ConfigureAwait(true);
        }
    }
}
