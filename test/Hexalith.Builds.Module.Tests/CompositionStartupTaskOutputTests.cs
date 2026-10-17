// <copyright file="CompositionStartupTaskOutputTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Globalization;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks startup task output is discarded without retaining unterminated lines.</summary>
public sealed class CompositionStartupTaskOutputTests
{
    private const string _childVariable = "HEXALITH_TEST_STARTUP_OUTPUT_CHILD";

    /// <summary>Verifies both output streams remain bounded while preserving success and failure results.</summary>
    /// <returns>The isolated allocation and task-result assertions.</returns>
    [Fact]
    public async Task LargeUnterminatedOutputUsesBoundedAllocationsAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The output generator uses POSIX head and /dev/zero.");
        }

        if (Environment.GetEnvironmentVariable(_childVariable) == "1")
        {
            await VerifyOutputAllocationsAsync().ConfigureAwait(true);
            return;
        }

        // Isolate process-wide allocation accounting from parallel tests and their child processes.
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(typeof(CompositionStartupTaskOutputTests).Assembly.Location);
        start.ArgumentList.Add("-method");
        start.ArgumentList.Add($"{typeof(CompositionStartupTaskOutputTests).FullName}.{nameof(LargeUnterminatedOutputUsesBoundedAllocationsAsync)}");
        start.Environment[_childVariable] = "1";
        using Process child = new() { StartInfo = start };
        child.Start().ShouldBeTrue();
        try
        {
            Task<string> output = child.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            Task<string> error = child.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
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

    private static async Task VerifyOutputAllocationsAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string executable = Path.Combine(root, "unterminated-output.sh");
            CompositionTestFiles.WriteScript(executable, "head -c 33554432 /dev/zero\nhead -c 33554432 /dev/zero >&2\nexit \"$1\"\n");
            foreach (int exitCode in new[] { 0, 7 })
            {
                long before = GC.GetTotalAllocatedBytes(precise: true);
                (string? failingTask, bool deadlineElapsed) = await CompositionStartupTasks.RunAsync(
                    [new CompositionStartupTask("sample", "output", executable, [exitCode.ToString(CultureInfo.InvariantCulture)])],
                    DateTimeOffset.UtcNow.AddSeconds(10),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

                deadlineElapsed.ShouldBeFalse();
                failingTask.ShouldBe(exitCode == 0 ? null : "sample/output");
                allocated.ShouldBeLessThan(16L * 1024 * 1024, "Discarding 32 MiB without newlines on each stream must not allocate memory proportional to output.");
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
