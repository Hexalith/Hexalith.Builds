// <copyright file="CompositionTestFilesTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Verifies fixture publication owns its writer lifetime before concurrent executable launches.</summary>
public sealed class CompositionTestFilesTests
{
    /// <summary>Checks every newly published fake Docker under a finite concurrent launch campaign.</summary>
    /// <returns>The exact completion assertions for all 512 controlled publications.</returns>
    [Fact]
    public async Task ConcurrentFreshScriptsAreImmediatelyExecutableAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The executable fixture uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            const int count = 512;
            using CancellationTokenSource lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            lifetime.CancelAfter(TimeSpan.FromSeconds(30));
            ConcurrentDictionary<int, CompositionProcessResult> observed = new();

            await Parallel.ForEachAsync(
                Enumerable.Range(0, count),
                new ParallelOptions { MaxDegreeOfParallelism = 12, CancellationToken = lifetime.Token },
                async (number, cancellationToken) =>
                {
                    string docker = CompositionTestFiles.CreateDocker(Path.Combine(root, number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                    CompositionProcessResult result = await CompositionProcess.RunAsync(
                        CompositionProcess.CreateStartInfo(docker, ["version", "--format", "{{.Server.Version}}"], null, null),
                        TimeSpan.FromSeconds(5),
                        cancellationToken).ConfigureAwait(false);
                    observed.TryAdd(number, result).ShouldBeTrue();
                }).ConfigureAwait(true);

            observed.Count.ShouldBe(count);
            foreach ((int number, CompositionProcessResult result) in observed.OrderBy(item => item.Key))
            {
                result.Started.ShouldBeTrue($"Publication {number}: {JsonSerializer.Serialize(result)}");
                result.ExitCode.ShouldBe(0);
                result.TimedOut.ShouldBeFalse();
                result.StartFailure.ShouldBeNull();
                result.Output.ShouldBe("29.0.0\n");
                result.ErrorOutput.ShouldBeEmpty();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Preserves literal UTF-8 script bytes and private executable mode without evaluating the body.</summary>
    [Fact]
    public void PublicationPreservesLiteralUtf8BytesAndPrivateMode()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The executable fixture uses POSIX shell scripts.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string sentinel = Path.Combine(root, "body-must-not-run");
            string script = Path.Combine(root, "quoted script's name");
            string body = "printf '%s' \"$HOME\"; # café\r\n$(touch '" + sentinel + "')";

            CompositionTestFiles.WriteScript(script, body);

            File.ReadAllBytes(script).ShouldBe(Encoding.UTF8.GetBytes("#!/bin/sh\n" + body + "\n"));
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(script).ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            File.Exists(sentinel).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}