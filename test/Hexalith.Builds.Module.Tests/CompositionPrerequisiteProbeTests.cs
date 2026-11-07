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
            string home = CompositionTestFiles.CreateDaprHome(root, "1.18.0", CompositionToolchainPins.DaprRuntimeVersion);

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                home,
                CompositionTestFiles.CreateDocker(root),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR012"]);
            string hint = result.Diagnostics[0].Hint.ShouldNotBeNull();
            hint.ShouldContain(CompositionToolchainPins.DaprCliVersion);
            hint.ShouldContain(CompositionToolchainPins.DaprRuntimeVersion);
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
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);
            CompositionTestFiles.WriteScript(
                Path.Combine(home, ".dapr", "bin", "scheduler"),
                "echo 'msg=\"Starting Dapr Scheduler Service -- version 1.18.4 -- commit test\"'\nsleep 30");

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(
                home,
                CompositionTestFiles.CreateDocker(root),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            foreach (ToolDiagnostic diagnostic in result.Diagnostics)
            {
                TestContext.Current.TestOutputHelper!.WriteLine(diagnostic.Message);
            }

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR013"]);
            result.Diagnostics[0].Hint.ShouldNotBeNull().ShouldContain(CompositionToolchainPins.DaprRuntimeVersion);
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
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);

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
            if (accepted)
            {
                result.ShouldBeNull($"Controlled Aspire fixture refused suffix '{suffix}': {result?.Message}");
            }
            else
            {
                _ = result.ShouldNotBeNull();
                result.RuleId.ShouldBe("HXR015");
                result.Phase.ShouldBe(ToolPhase.Prerequisite);
                result.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
            }
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
                aspire,
                mode == "timeout" ? TimeSpan.FromMilliseconds(200) : TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = result.ShouldNotBeNull();
            result.RuleId.ShouldBe("HXR015");
            result.Message.ShouldContain(CompositionToolchainPins.AspireAppHostSdkVersion);
            if (mode is "missing" or "timeout")
            {
                result.Message.ShouldNotContain("(exit ");
            }
            else
            {
                result.Message.ShouldContain("(exit ");
            }

            result.Phase.ShouldBe(ToolPhase.Prerequisite);
            result.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies actual launch failures remain distinct without disclosing executable paths or exception details.</summary>
    /// <param name="mode">The controlled filesystem launch condition.</param>
    /// <param name="nativeCode">The expected native launch failure code.</param>
    /// <returns>The bounded fail-closed probe assertion.</returns>
    [Theory]
    [InlineData("missing", 2)]
    [InlineData("permission-denied", 13)]
    [InlineData("held-writer", 26)]
    public async Task AspireLaunchFailuresRetainSafeNativeCodeAsync(string mode, int nativeCode)
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            const string sensitiveDetail = "private-launch-detail-must-not-leak";
            string aspire = Path.Combine(root, sensitiveDetail);
            if (mode != "missing")
            {
                CompositionTestFiles.WriteScript(aspire, $"echo '{CompositionToolchainPins.AspireAppHostSdkVersion}'");
            }

            if (mode == "permission-denied" && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(aspire, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using FileStream? writer = mode == "held-writer"
                ? new(aspire, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                : null;
            ToolDiagnostic? result = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                aspire, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);

            _ = result.ShouldNotBeNull();
            result.RuleId.ShouldBe("HXR015");
            result.Phase.ShouldBe(ToolPhase.Prerequisite);
            result.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
            result.Message.ShouldBe($"Aspire CLI observed 'missing' (start failure Win32, native error {nativeCode}); expected Aspire.AppHost.Sdk/{CompositionToolchainPins.AspireAppHostSdkVersion}.");
            result.Message.Length.ShouldBeLessThan(512);
            result.Message.ShouldNotContain(root);
            result.Message.ShouldNotContain(sensitiveDetail);
            result.Message.ShouldNotContain("(exit ");
            result.Hint.ShouldBe($"Install Aspire CLI {CompositionToolchainPins.AspireAppHostSdkVersion} to match the catalog-selected Aspire.AppHost.Sdk/{CompositionToolchainPins.AspireAppHostSdkVersion}, then retry.");
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

    /// <summary>Retains bounded Docker launch metadata without disclosing process inputs.</summary>
    /// <param name="mode">The controlled launch condition.</param>
    /// <param name="nativeCode">The actual native launch refusal.</param>
    /// <returns>The original fail-closed prerequisite assertions.</returns>
    [Theory]
    [InlineData("missing", 2)]
    [InlineData("permission-denied", 13)]
    [InlineData("held-writer", 26)]
    public async Task DockerLaunchFailuresRetainSafeNativeCodeAsync(string mode, int nativeCode)
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            const string sensitiveDetail = "private-docker-launch-detail-must-not-leak";
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);
            string docker = Path.Combine(root, sensitiveDetail);
            if (mode != "missing")
            {
                CompositionTestFiles.WriteScript(docker, "echo '" + sensitiveDetail + "'");
            }

            if (mode == "permission-denied" && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(docker, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using FileStream? writer = mode == "held-writer"
                ? new(docker, FileMode.Open, FileAccess.Write, FileShare.ReadWrite)
                : null;
            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(home, docker, TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR010"]);
            result.IsAvailable.ShouldBeFalse();
            result.DaprHome.ShouldBeNull();
            ToolDiagnostic diagnostic = result.Diagnostics.Single();
            diagnostic.Message.ShouldBe($"The container runtime required for run-scoped resources is unavailable. Docker probe observed 'not started' (start failure Win32, native error {nativeCode}); stdout empty; stderr empty.");
            diagnostic.Message.Length.ShouldBeLessThan(512);
            diagnostic.Message.ShouldNotContain(root);
            diagnostic.Message.ShouldNotContain(sensitiveDetail);
            diagnostic.Phase.ShouldBe(ToolPhase.Prerequisite);
            diagnostic.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
            diagnostic.Hint.ShouldBe("Start Docker and make its CLI available before retrying.");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Distinguishes empty output from a nonzero Docker exit without retaining its output values.</summary>
    /// <param name="empty">Whether the fake command produces no standard output.</param>
    /// <returns>The exact safe completion observation.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DockerCompletionFailuresRetainSafeObservationAsync(bool empty)
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            const string sensitiveDetail = "private-docker-output-must-not-leak";
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);
            string docker = Path.Combine(root, sensitiveDetail);
            string body = empty
                ? "printf '%s' '" + sensitiveDetail + "' >&2\nexit 0"
                : "printf '%s' '" + sensitiveDetail + "'\nprintf '%s' '" + sensitiveDetail + "' >&2\nexit 17";
            CompositionTestFiles.WriteScript(docker, body);

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(home, docker, TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR010"]);
            result.IsAvailable.ShouldBeFalse();
            result.DaprHome.ShouldBeNull();
            string observed = empty ? "exit 0" : "exit 17";
            string output = empty ? "empty" : "present";
            ToolDiagnostic diagnostic = result.Diagnostics.Single();
            diagnostic.Message.ShouldBe($"The container runtime required for run-scoped resources is unavailable. Docker probe observed '{observed}'; stdout {output}; stderr present.");
            diagnostic.Message.ShouldNotContain(root);
            diagnostic.Message.ShouldNotContain(sensitiveDetail);
            diagnostic.Message.ShouldNotContain("start failure");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Preserves the original Docker timeout and refuses it as an unavailable prerequisite.</summary>
    /// <returns>The bounded actual timeout observation.</returns>
    [Fact]
    public async Task DockerTimeoutRetainsSafeObservationAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);
            string docker = Path.Combine(root, "slow-docker");
            CompositionTestFiles.WriteScript(docker, "sleep 30");

            CompositionPrerequisiteResult result = await CompositionPrerequisiteProbe.ProbeAsync(home, docker, TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXR010"]);
            result.IsAvailable.ShouldBeFalse();
            result.Diagnostics.Single().Message.ShouldBe("The container runtime required for run-scoped resources is unavailable. Docker probe observed 'timed out'; stdout empty; stderr empty.");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Preserves caller cancellation through the unchanged bounded Docker process path.</summary>
    /// <returns>The original cancellation exception assertion.</returns>
    [Fact]
    public async Task DockerProbePropagatesCancellationAsync()
    {
        SkipOnWindows();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string home = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion);
            string docker = Path.Combine(root, "cancelled-docker");
            CompositionTestFiles.WriteScript(docker, "sleep 30");
            using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(200));

            _ = await Should.ThrowAsync<OperationCanceledException>(() => CompositionPrerequisiteProbe.ProbeAsync(home, docker, cancellation.Token)).ConfigureAwait(true);
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