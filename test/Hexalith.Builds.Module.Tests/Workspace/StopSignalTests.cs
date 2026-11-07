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
        string root = CompositionTestFiles.CreateDirectory();
        TextWriter originalOutput = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            foreach (bool retain in new[] { false, true })
            {
                Stream actual = StopSignalInput.Open();
                await using (actual.ConfigureAwait(true))
                {
                    actual.GetType().FullName.ShouldBe("System.IO.Pipes.AnonymousPipeClientStream");
                    ObservedStdinStream input = new(actual);
                    await using (input.ConfigureAwait(true))
                    {
                        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { RetainOnFailure = retain };
                        StopSignal signal = StopSignal.Listen(plan, input);
                        await input.ReadStarted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
                        await signal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
                        File.Exists(CompositionStartupFailure.PathFor(plan.Workspace)).ShouldBeFalse();
                    }
                }
            }

            // A second listener must still be able to consume the owner's command from fd 0.
            StopSignal next = StopSignal.Listen(CompositionTestFiles.CreatePlan(root));
            await using (next.ConfigureAwait(true))
            {
                await WriteReadinessToOwnedOutputAsync().ConfigureAwait(true);
                _ = await Should.ThrowAsync<OperationCanceledException>(() => Task.Delay(Timeout.InfiniteTimeSpan, next.Token)
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).ConfigureAwait(true);
                next.IsStopRequested.ShouldBeTrue();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
            Console.SetOut(originalOutput);
        }
    }

    /// <summary>Owns the redirected child and keeps stdin open through the exact readiness handshake.</summary>
    /// <param name="childVariable">The owned control childVariable.</param>
    /// <returns>The focused control operation.</returns>
    private static async Task RunPendingReadDisposalOwnerAsync(string childVariable)
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
        using Process child = new() { StartInfo = start };
        child.Start().ShouldBeTrue();
        try
        {
            Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            List<string> output = [];
            await ReadReadinessFromChildAsync(child, output).ConfigureAwait(true);

            // Neither a stop command nor EOF was sent until both pending reads actually drained.
            await child.StandardInput.WriteLineAsync(CompositionEnvironment.StopCommand).ConfigureAwait(true);
            await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            Task<string> tail = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
            child.ExitCode.ShouldBe(0, string.Join(Environment.NewLine, output) + await tail.ConfigureAwait(true) + await error.ConfigureAwait(true));
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

    /// <summary>Waits for the actual marker with the existing bound and a fixed child-status diagnostic.</summary>
    /// <param name="child">The owned control child.</param>
    /// <param name="output">The owned control output.</param>
    /// <exception cref="TimeoutException">The actual marker did not arrive within the original bound.</exception>
    /// <returns>The focused control operation.</returns>
    private static async Task ReadReadinessFromChildAsync(Process child, List<string> output)
    {
        try
        {
            string? line;
            do
            {
                line = await child.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                    .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
                line = line.ShouldNotBeNull(string.Join(Environment.NewLine, output));
                output.Add(line);
            }
            while (line != "stdin-control ready-for-command");
        }
        catch (TimeoutException failure)
        {
            throw new TimeoutException($"stdin-control readiness timed out; childExited={child.HasExited}.", failure);
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
}
