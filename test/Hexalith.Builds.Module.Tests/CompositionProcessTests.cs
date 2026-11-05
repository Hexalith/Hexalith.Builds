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
    /// <summary>Verifies missing executables retain native metadata without sensitive process inputs.</summary>
    /// <returns>The bounded launch refusal assertion.</returns>
    [Fact]
    public async Task MissingExecutableRetainsOnlySafeLaunchMetadataAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            const string sensitiveDetail = "private-launch-detail-must-not-leak";
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(
                    Path.Combine(root, sensitiveDetail),
                    [sensitiveDetail + "-argument"],
                    root,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["HEXALITH_PRIVATE_LAUNCH_DETAIL"] = sensitiveDetail + "-environment" }),
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Started.ShouldBeFalse();
            result.ExitCode.ShouldBe(-1);
            result.TimedOut.ShouldBeFalse();
            result.Output.ShouldBeEmpty();
            result.ErrorOutput.ShouldBeEmpty();
            result.OutputTruncated.ShouldBeFalse();
            CompositionProcessStartFailure failure = result.StartFailure.ShouldNotBeNull();
            failure.Kind.ShouldBe(CompositionProcessStartFailureKind.Win32);
            failure.NativeErrorCode.ShouldBe(2);
            result.ToString().ShouldNotContain(root);
            result.ToString().ShouldNotContain(sensitiveDetail);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies invalid start descriptions retain only their finite failure kind.</summary>
    /// <returns>The invalid-operation launch refusal assertion.</returns>
    [Fact]
    public async Task InvalidStartDescriptionRetainsSafeFailureKindAsync()
    {
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            new System.Diagnostics.ProcessStartInfo(),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Started.ShouldBeFalse();
        result.ExitCode.ShouldBe(-1);
        result.TimedOut.ShouldBeFalse();
        result.Output.ShouldBeEmpty();
        result.ErrorOutput.ShouldBeEmpty();
        CompositionProcessStartFailure failure = result.StartFailure.ShouldNotBeNull();
        failure.Kind.ShouldBe(CompositionProcessStartFailureKind.InvalidOperation);
        failure.NativeErrorCode.ShouldBeNull();
    }

    /// <summary>Verifies the line-marker API preserves its missing-helper null result.</summary>
    /// <returns>The bounded missing-helper assertion.</returns>
    [Fact]
    public async Task MissingExecutableHasNoFirstMatchingLineAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string? line = await CompositionProcess.ReadFirstLineContainingAsync(
                CompositionProcess.CreateStartInfo(Path.Combine(root, "missing-helper"), [], null, null),
                "marker",
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            line.ShouldBeNull();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a started executable's nonzero exit is not classified as a launch failure.</summary>
    /// <returns>The actual controlled nonzero-exit assertion.</returns>
    [Fact]
    public async Task StartedNonzeroProcessHasNoLaunchFailureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The nonzero-exit probe uses a POSIX shell.");
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("/bin/sh", ["-c", "printf observed; exit 1"], null, null),
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Started.ShouldBeTrue();
        result.ExitCode.ShouldBe(1);
        result.Output.ShouldBe("observed");
        result.TimedOut.ShouldBeFalse();
        result.StartFailure.ShouldBeNull();
    }

    /// <summary>Verifies a started executable's timeout is not classified as a launch failure.</summary>
    /// <returns>The actual controlled timeout assertion.</returns>
    [Fact]
    public async Task TimedOutProcessHasNoLaunchFailureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The timeout probe uses a POSIX shell.");
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("/bin/sh", ["-c", "sleep 30"], null, null),
            TimeSpan.FromMilliseconds(200),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Started.ShouldBeTrue();
        result.ExitCode.ShouldBe(-1);
        result.TimedOut.ShouldBeTrue();
        result.StartFailure.ShouldBeNull();
    }

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
