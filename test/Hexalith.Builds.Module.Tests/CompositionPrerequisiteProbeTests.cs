// <copyright file="CompositionPrerequisiteProbeTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies prerequisite probing executes the toolchain and fails closed with stable <c>HXR01x</c> diagnostics.
/// </summary>
public sealed class CompositionPrerequisiteProbeTests
{
    /// <summary>
    /// Verifies the Dapr CLI and service version parsers.
    /// </summary>
    [Fact]
    public void ParsersReadCliRuntimeAndServiceVersions()
    {
        (string? cli, string? runtime) = CompositionPrerequisiteProbe.ParseDaprVersions("CLI version: 1.18.0 \nRuntime version: 1.18.2\n");

        cli.ShouldBe("1.18.0");
        runtime.ShouldBe("1.18.2");
        CompositionPrerequisiteProbe.ParseDaprVersions("Runtime version: n/a").Cli.ShouldBeNull();
        CompositionPrerequisiteProbe.ParseServiceVersion("msg=\"Starting Dapr Scheduler Service -- version 1.18.2 -- commit x\"").ShouldBe("1.18.2");
        CompositionPrerequisiteProbe.ParseServiceVersion(null).ShouldBeNull();
    }

    /// <summary>
    /// Verifies a missing Docker CLI and a missing Dapr home are both reported as unavailable prerequisites.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingDockerAndDaprHomeAreUnavailableAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                Path.Combine(root, "missing-dapr-home"),
                Path.Combine(root, "missing-docker"),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsAvailable.ShouldBeFalse();
            result.DaprHome.ShouldBeNull();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR010", "HXR011"]);
            result.Diagnostics.ShouldAllBe(diagnostic =>
                diagnostic.Phase == ToolPhase.Prerequisite && diagnostic.Category == ToolFailureCategory.PrerequisiteUnavailable);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies a Dapr CLI reporting another version is rejected.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task WrongDaprCliVersionIsUnavailableAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string home = CompositionTestFiles.CreateDaprHome(root, "1.18.2", "1.18.2");

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                home,
                CompositionTestFiles.CreateDocker(root),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR012"]);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies runtime binaries reporting another version are rejected even when the CLI matches.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task WrongRuntimeBinaryVersionIsUnavailableAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string home = CompositionTestFiles.CreateDaprHome(root, "1.18.0", "1.18.2");
            CompositionTestFiles.WriteScript(
                Path.Combine(home, ".dapr", "bin", "scheduler"),
                "echo 'msg=\"Starting Dapr Scheduler Service -- version 1.18.4 -- commit test\"'\nsleep 30");

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                home,
                CompositionTestFiles.CreateDocker(root),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR013"]);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies the selected toolchain is accepted when every binary reports the selected versions.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task SelectedToolchainIsAvailableAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string home = CompositionTestFiles.CreateDaprHome(root, "1.18.0", "1.18.2");

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                home,
                CompositionTestFiles.CreateDocker(root),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.ShouldBeEmpty();
            result.IsAvailable.ShouldBeTrue();
            result.DaprHome.ShouldBe(Path.GetFullPath(home));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies release/prerelease equality ignores only build metadata.</summary>
    /// <param name="suffix">The observed suffix.</param>
    /// <param name="accepted">Whether the observation must match.</param>
    /// <returns>The asynchronous test.</returns>
    [Theory]
    [InlineData("", true)]
    [InlineData("+build.hash", true)]
    [InlineData("-preview.1", false)]
    public async Task AspireVersionEqualityAsync(string suffix, bool accepted)
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string aspire = Path.Combine(root, "aspire");
            CompositionTestFiles.WriteScript(aspire, $"[ \"$#\" -eq 1 ] && [ \"$1\" = '--version' ] || exit 64\necho '{CompositionToolchainPins.AspireAppHostSdkVersion.Split('+')[0]}{suffix}'");
            ToolDiagnostic? result = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                aspire, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            (result is null).ShouldBe(accepted);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies missing, malformed, failed and timed-out Aspire never succeeds.</summary>
    /// <param name="mode">The fake executable behavior.</param>
    /// <returns>The asynchronous test.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("different")]
    [InlineData("empty")]
    [InlineData("failed")]
    [InlineData("timeout")]
    public async Task AspireUnavailableObservationsFailClosedAsync(string mode)
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string aspire = Path.Combine(root, "aspire");
            if (mode != "missing")
            {
                string body = mode switch
                {
                    "malformed" => "echo 'invalid-version'",
                    "different" => "echo '0.0.1'",
                    "empty" => "exit 0",
                    "failed" => $"echo '{CompositionToolchainPins.AspireAppHostSdkVersion}'; exit 1",
                    _ => "sleep 30",
                };
                CompositionTestFiles.WriteScript(aspire, body);
            }

            ToolDiagnostic? result = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                aspire, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = result.ShouldNotBeNull();
            result.RuleId.ShouldBe("HXR015");
            result.Message.ShouldContain(CompositionToolchainPins.AspireAppHostSdkVersion);
            result.Phase.ShouldBe(ToolPhase.Prerequisite);
            result.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a caller cancellation propagates from the bounded Aspire probe.</summary>
    /// <returns>The asynchronous test.</returns>
    [Fact]
    public async Task AspireProbePropagatesCancellationAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string aspire = Path.Combine(root, "aspire");
            CompositionTestFiles.WriteScript(aspire, "sleep 30");
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));
            _ = await Should.ThrowAsync<OperationCanceledException>(() => CompositionPrerequisiteProbe.ProbeAspireAsync(
                aspire, TimeSpan.FromSeconds(5), cancellation.Token)).ConfigureAwait(true);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }
    }
}