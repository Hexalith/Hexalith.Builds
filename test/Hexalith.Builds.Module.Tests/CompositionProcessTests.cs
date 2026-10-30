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
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CompositionProcessTests
{
    /// <summary>Retains explicit nonsecret SDK prevention and cache controls only for dotnet children.</summary>
    [Fact]
    public void DotnetChildrenRetainSdkControlsWithoutBroadeningOtherHelpers()
    {
        string[] names = ["DOTNET_GENERATE_ASPNET_CERTIFICATE", "MSBUILDDISABLENODEREUSE", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "NUGET_SCRATCH"];
        Dictionary<string, string?> original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (string name in names)
            {
                Environment.SetEnvironmentVariable(name, name == "DOTNET_GENERATE_ASPNET_CERTIFICATE" ? "false" : "selected-private-control");
            }

            System.Diagnostics.ProcessStartInfo sdk = CompositionProcess.CreateStartInfo("dotnet", ["--version"], null, null);
            System.Diagnostics.ProcessStartInfo other = CompositionProcess.CreateStartInfo("other-helper", [], null, null);
            foreach (string name in names)
            {
                sdk.Environment[name].ShouldBe(Environment.GetEnvironmentVariable(name));
                other.Environment.ContainsKey(name).ShouldBeFalse(name);
            }

            sdk.ArgumentList.ShouldBe(["--version"]);
        }
        finally
        {
            foreach ((string name, string? value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Verifies callers can distinguish complete output from a bounded prefix.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task OutputBeyondCaptureBoundIsMarkedTruncatedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The output probe uses a POSIX shell.");
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("/bin/sh", ["-c", "printf '%070000d' 0"], null, null),
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.ExitCode.ShouldBe(0);
        result.Output.Length.ShouldBe(65_536);
        result.OutputTruncated.ShouldBeTrue();
    }

    /// <summary>Verifies standard error retains only the bounded tail.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ErrorBeyondCaptureBoundRetainsLastMarkerAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The output probe uses a POSIX shell.");
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("/bin/sh", ["-c", "printf '%05000dEND' 0 >&2"], null, null),
            TimeSpan.FromSeconds(20),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.ExitCode.ShouldBe(0);
        result.ErrorOutput.Length.ShouldBe(4096);
        result.ErrorOutput.ShouldEndWith("END");
    }

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
