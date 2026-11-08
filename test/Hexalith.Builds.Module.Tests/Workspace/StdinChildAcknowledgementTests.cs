// <copyright file="StdinChildAcknowledgementTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>Checks explicit parent-bound readiness independently of private telemetry and console framing.</summary>
public sealed class StdinChildAcknowledgementTests
{
    /// <summary>Gets a value indicating whether the symbolic-link control applies.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Rejects early telemetry and partial publication until the child explicitly publishes.</summary>
    /// <returns>The actual pending and exact acknowledgement assertions.</returns>
    [Fact]
    public async Task WaitRequiresExplicitPublicationAsync()
    {
        string directory = CompositionTestFiles.CreateDirectory();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        Task ready = Task.CompletedTask;
        try
        {
            string token = Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(Path.Combine(directory, "child-progress.json"), token, deadline.Token).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, "stdin-ready.tmp"), token, deadline.Token).ConfigureAwait(true);
            ready = StdinChildAcknowledgement.WaitAsync(directory, token, deadline.Token);
            await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token).ConfigureAwait(true);
            ready.IsCompleted.ShouldBeFalse();
            await StdinChildAcknowledgement.PublishAsync(directory, token, deadline.Token).ConfigureAwait(true);
            await ready.ConfigureAwait(true);
            (await File.ReadAllTextAsync(Path.Combine(directory, "stdin-ready.token"), deadline.Token).ConfigureAwait(true)).ShouldBe(token);
        }
        finally
        {
            await deadline.CancelAsync().ConfigureAwait(true);
            try
            {
                await ready.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Exhaust the pending private poll before deleting its owned directory.
            }
            finally
            {
                CompositionTestFiles.Delete(directory);
            }
        }
    }

    /// <summary>Stops a missing acknowledgement wait on its actual bounded cancellation.</summary>
    /// <returns>The cancellation and absent-record assertions.</returns>
    [Fact]
    public async Task WaitHonorsCancellationWithoutAcknowledgementAsync()
    {
        string directory = CompositionTestFiles.CreateDirectory();
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        try
        {
            Task ready = StdinChildAcknowledgement.WaitAsync(directory, Guid.NewGuid().ToString("N"), deadline.Token);
            ready.IsCompleted.ShouldBeFalse();
            await deadline.CancelAsync().ConfigureAwait(true);
            try
            {
                await ready.ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                // Await the exact private poll in its creating context.
            }

            ready.IsCanceled.ShouldBeTrue();
            File.Exists(Path.Combine(directory, "stdin-ready.token")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(directory);
        }
    }

    /// <summary>Refuses malformed, foreign and linked acknowledgements without accepting their existence.</summary>
    /// <param name="mode">The controlled invalid record.</param>
    /// <returns>The exact refusal and unchanged foreign-file assertions.</returns>
    [Theory(Skip = "The linked-file control uses Linux symbolic links.", SkipUnless = nameof(IsLinux))]
    [InlineData("empty")]
    [InlineData("oversized")]
    [InlineData("foreign")]
    [InlineData("linked")]
    public async Task WaitRefusesInvalidRecordAsync(string mode)
    {
        string directory = CompositionTestFiles.CreateDirectory();
        try
        {
            string token = Guid.NewGuid().ToString("N");
            string foreign = Path.Combine(directory, "foreign.token");
            await File.WriteAllTextAsync(foreign, token, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string path = Path.Combine(directory, "stdin-ready.token");
            if (mode == "linked")
            {
                _ = File.CreateSymbolicLink(path, foreign);
            }
            else
            {
                string invalid = mode switch { "empty" => string.Empty, "oversized" => new string('x', 2048), _ => new string('x', token.Length) };
                await File.WriteAllTextAsync(path, invalid, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            _ = await Should.ThrowAsync<InvalidDataException>(() => StdinChildAcknowledgement.WaitAsync(directory, token, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            (await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(token);
        }
        finally
        {
            CompositionTestFiles.Delete(directory);
        }
    }

    /// <summary>Uses the explicit acknowledgement with managed output captured and raw stdout framed without a newline.</summary>
    /// <returns>The harmless child-only framing and real command handshake assertions.</returns>
    [Fact]
    public async Task AcknowledgementIgnoresCapturedAndFramedConsoleAsync()
    {
        const string childVariable = "HEXALITH_TEST_STDIN_ACK_CHILD";
        if (Environment.GetEnvironmentVariable(childVariable) == "1")
        {
            await RunFramedChildAsync().ConfigureAwait(true);
            return;
        }

        string directory = CompositionTestFiles.CreateDirectory();
        try
        {
            await RunFramedOwnerAsync(childVariable, directory).ConfigureAwait(true);
        }
        finally
        {
            CompositionTestFiles.Delete(directory);
        }
    }

    private static async Task RunFramedChildAsync()
    {
        TextWriter original = Console.Out;
        Console.SetOut(TextWriter.Null);
        try
        {
            StreamWriter output = new(Console.OpenStandardOutput(), new System.Text.UTF8Encoding(false), leaveOpen: false);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownedOutput = output.ConfigureAwait(true);
            await output.WriteAsync("\u001b[32mframed-without-newline\u001b[0m").ConfigureAwait(true);
            await output.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await StdinChildAcknowledgement.PublishAsync(
                Environment.GetEnvironmentVariable(StdinChildProgress.DirectoryVariable).ShouldNotBeNull(),
                Environment.GetEnvironmentVariable(StdinChildAcknowledgement.TokenVariable).ShouldNotBeNull(),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            (await Console.In.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("acknowledgement received");
        }
        finally
        {
            Console.SetOut(original);
        }
    }

    private static async Task RunFramedOwnerAsync(string childVariable, string directory)
    {
        string token = Guid.NewGuid().ToString("N");
        ProcessStartInfo start = new("dotnet") { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(StdinChildAcknowledgementTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(StdinChildAcknowledgementTests).FullName}.{nameof(AcknowledgementIgnoresCapturedAndFramedConsoleAsync)}");
        start.Environment[childVariable] = "1";
        start.Environment[StdinChildProgress.DirectoryVariable] = directory;
        start.Environment[StdinChildAcknowledgement.TokenVariable] = token;
        using Process child = new() { StartInfo = start };
        child.Start().ShouldBeTrue();
        Task<string> output = Task.FromResult(string.Empty);
        Task<string> error = Task.FromResult(string.Empty);
        try
        {
            output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            await StdinChildAcknowledgement.WaitAsync(directory, token, deadline.Token).ConfigureAwait(true);
            await child.StandardInput.WriteLineAsync("acknowledgement received").ConfigureAwait(true);
            await child.StandardInput.FlushAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
            child.ExitCode.ShouldBe(0);
            (await output.ConfigureAwait(true)).ShouldContain("\u001b[32mframed-without-newline\u001b[0m");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None).ConfigureAwait(true);
            }

            _ = await Task.WhenAll(output, error).ConfigureAwait(true);
        }
    }
}
