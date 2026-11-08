// <copyright file="StdinChildProgressTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Shouldly;

using Xunit;

/// <summary>Checks that child diagnostics expose only actual fixed fields from bounded private files.</summary>
public sealed class StdinChildProgressTests
{
    /// <summary>Gets a value indicating whether the symbolic-link control applies.</summary>
    public static bool IsLinux => OperatingSystem.IsLinux();

    /// <summary>Checks fixed fields, unknown stages, oversize files and linked foreign metadata.</summary>
    /// <param name="mode">The controlled private-file condition.</param>
    /// <returns>The bounded-observation assertions.</returns>
    [Theory(Skip = "The linked-file control uses Linux symbolic links.", SkipUnless = nameof(IsLinux))]
    [InlineData("valid")]
    [InlineData("unknown")]
    [InlineData("oversized")]
    [InlineData("linked")]
    public async Task DescriptionContainsOnlyBoundedFixedObservationAsync(string mode)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            ThreadPool.GetMinThreads(out int minimum, out _);
            ThreadPool.GetMaxThreads(out int maximum, out _);
            string contents = CreateContents(mode, minimum, maximum);
            string foreign = Path.Combine(root, "foreign.json");
            string path = Path.Combine(root, "child-progress.json");
            await File.WriteAllTextAsync(foreign, contents, TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (mode == "linked")
            {
                _ = File.CreateSymbolicLink(path, foreign);
            }
            else
            {
                await File.WriteAllTextAsync(path, mode == "oversized" ? new string('x', 2048) : contents, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            string observation = StdinChildProgress.Describe(root);
            if (mode == "valid")
            {
                observation.ShouldContain("driver=child-complete;io=input-disposed;");
                observation.ShouldContain($"runtime={Environment.Version};processors={Environment.ProcessorCount};workers={minimum}/{maximum}");
            }
            else
            {
                observation.ShouldBe("stage=unobserved");
            }

            observation.ShouldNotContain("PRIVATE_SENTINEL");
            (await File.ReadAllTextAsync(foreign, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(contents);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string CreateContents(string mode, int minimum, int maximum) => JsonSerializer.Serialize(new
    {
        driverStage = mode == "unknown" ? "PRIVATE_SENTINEL" : "child-complete",
        ioStage = "input-disposed",
        retained = false,
        runtime = Environment.Version.ToString(),
        processors = Environment.ProcessorCount,
        minimumWorkers = minimum,
        maximumWorkers = maximum,
        arbitrary = "PRIVATE_SENTINEL",
    });
}
