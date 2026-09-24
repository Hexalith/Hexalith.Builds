// <copyright file="CompositionProcessTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies helper processes start with a scrubbed environment.
/// </summary>
public sealed class CompositionProcessTests
{
    /// <summary>
    /// Verifies a variable of the runner process never reaches a helper while explicit values do.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task CreateStartInfoScrubsInheritedEnvironmentAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The environment probe uses a POSIX shell.");
        }

        const string sentinel = "HEXALITH_G4_SCRUB_SENTINEL_7f3a";
        Environment.SetEnvironmentVariable(sentinel, "runner-secret-must-not-leak");
        try
        {
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(
                    "/bin/sh",
                    ["-c", "echo \"sentinel=${" + sentinel + ":-absent} explicit=${HEXALITH_G4_EXPLICIT:-absent}\""],
                    null,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["HEXALITH_G4_EXPLICIT"] = "passed" }),
                TimeSpan.FromSeconds(20),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.ExitCode.ShouldBe(0);
            result.Output.Trim().ShouldBe("sentinel=absent explicit=passed");
            result.Output.ShouldNotContain("runner-secret-must-not-leak");
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
        }
    }
}