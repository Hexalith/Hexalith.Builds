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

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Dapr toolchain uses POSIX shell scripts.");
        }
    }
}