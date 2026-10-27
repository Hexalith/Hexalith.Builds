// <copyright file="CompositionSdkProcessTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Proves SDK launch controls before workspace SDK assertions execute.</summary>
public sealed class CompositionSdkProcessTests
{
    /// <summary>Verifies only MSBuild commands receive supported serialization arguments.</summary>
    /// <param name="command">The SDK command.</param>
    /// <param name="serial">Whether it is an MSBuild command.</param>
    [Theory]
    [InlineData("build", true)]
    [InlineData("restore", true)]
    [InlineData("pack", true)]
    [InlineData("msbuild", true)]
    [InlineData("--version", false)]
    [InlineData("exec", false)]
    public void StartDescriptionUsesCommandSpecificControls(string command, bool serial)
    {
        ProcessStartInfo start = CompositionSdkProcess.CreateStartInfo([command], CompositionTestFiles.RepositoryRoot());
        Path.IsPathFullyQualified(start.FileName).ShouldBeTrue();
        File.Exists(start.FileName).ShouldBeTrue();
        if (Environment.GetEnvironmentVariable("HEXALITH_TEST_DOTNET_EXECUTABLE") is string selected)
        {
            start.FileName.ShouldBe(selected);
        }

        _ = start.Environment.TryGetValue("HOME", out string? inheritedHome);
        _ = start.Environment.TryGetValue("USERPROFILE", out string? inheritedProfile);
        inheritedHome.ShouldBe(Environment.GetEnvironmentVariable("HOME"));
        inheritedProfile.ShouldBe(Environment.GetEnvironmentVariable("USERPROFILE"));
        start.UseShellExecute.ShouldBeFalse();
        start.RedirectStandardOutput.ShouldBeTrue();
        start.RedirectStandardError.ShouldBeTrue();
        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"].ShouldBe("false");
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"].ShouldBe("1");
        start.Environment["MSBUILDDISABLENODEREUSE"].ShouldBe("1");
        start.ArgumentList.Contains("-m:1").ShouldBe(serial);
        start.ArgumentList.Contains("-nr:false").ShouldBe(serial);
        start.ArgumentList.Contains("-p:BuildInParallel=false").ShouldBe(serial);
        start.ArgumentList.Contains("-p:RestoreBuildInParallel=false").ShouldBe(serial);
        start.ArgumentList.Contains("--disable-parallel").ShouldBe(command == "restore");
        foreach (string key in new[] { "DOTNET_CLI_HOME", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "TMPDIR" })
        {
            Path.IsPathFullyQualified(start.Environment[key]!).ShouldBeTrue(key);
            Directory.Exists(start.Environment[key]).ShouldBeTrue(key);
        }
    }

    /// <summary>Verifies an explicit selection cannot fall back to PATH or an arbitrary executable.</summary>
    /// <param name="executable">The invalid selection.</param>
    [Theory]
    [InlineData("dotnet")]
    [InlineData("relative/dotnet")]
    [InlineData("/nonexistent-sdk/dotnet")]
    public void InvalidExplicitSelectionRefusesBeforeExecution(string executable) =>
        Should.Throw<ArgumentException>(() => CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot(), executable: executable));

    /// <summary>Verifies unrelated inherited credentials are scrubbed and explicit controls cannot disable certificate prevention.</summary>
    [Fact]
    public void StartDescriptionScrubsUnrelatedInheritedValues()
    {
        const string sentinel = "HEXALITH_SDK_TEST_SECRET_SENTINEL";
        Environment.SetEnvironmentVariable(sentinel, "must-not-reach-sdk");
        try
        {
            ProcessStartInfo start = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot(), new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "true",
                ["HEXALITH_SOURCE_MAPPING_HASH"] = "selected",
            });
            start.Environment.ContainsKey(sentinel).ShouldBeFalse();
            start.Environment.ContainsKey("DOCKER_CONFIG").ShouldBeFalse();
            start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"].ShouldBe("false");
            start.Environment["HEXALITH_SOURCE_MAPPING_HASH"].ShouldBe("selected");
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
        }
    }

    /// <summary>Verifies qualification intercepts production bare-dotnet probes before they may invoke the SDK.</summary>
    /// <returns>The bounded non-SDK interception assertion.</returns>
    [Fact]
    public async Task QualificationCatalogProbeInterceptionIsEstablishedAsync()
    {
        if (Environment.GetEnvironmentVariable("HEXALITH_TEST_SDK_INTERCEPTION_REQUIRED") != "true")
        {
            CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot()).Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"].ShouldBe("false");
            return;
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["--hexalith-controlled-interception-probe"], AppContext.BaseDirectory, null),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
        result.TimedOut.ShouldBeFalse();
        result.Output.Trim().ShouldBe("intercepted|certificate=false|serial=1");
    }

    /// <summary>Verifies the actual SDK reports the repository's pinned version under the controlled environment.</summary>
    /// <returns>The bounded SDK probe assertion.</returns>
    [Fact]
    public async Task SelectedSdkReportsPinnedVersionAsync()
    {
        string root = CompositionTestFiles.RepositoryRoot();
        using JsonDocument global = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        CompositionProcessResult result = await CompositionSdkProcess.RunAsync(root, null, "--version").ConfigureAwait(true);
        result.Started.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
        result.Output.Trim().ShouldBe(global.RootElement.GetProperty("sdk").GetProperty("version").GetString());
    }

    /// <summary>Verifies an unresponsive controlled SDK child is stopped within its bound.</summary>
    /// <returns>The SDK cancellation assertion.</returns>
    [Fact]
    public async Task SdkChildStopsAtExplicitBoundAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string project = Path.Combine(root, "Wait.proj");
            string wait = OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1 &gt;nul" : "/bin/sleep 30";
            await File.WriteAllTextAsync(project, "<Project><Target Name=\"Wait\"><Exec Command=\"" + wait + "\" /></Target></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            Stopwatch elapsed = Stopwatch.StartNew();
            CompositionProcessResult result = await CompositionProcess.RunAsync(
                CompositionSdkProcess.CreateStartInfo(["msbuild", project, "-nologo", "-target:Wait"], root),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Started.ShouldBeTrue();
            result.TimedOut.ShouldBeTrue();
            result.ExitCode.ShouldBe(-1);
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Proves explicit controls and serialized properties reach a bounded real SDK child.</summary>
    /// <returns>The bounded child probe assertion.</returns>
    [Fact]
    public async Task SdkChildObservesExplicitControlsAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        const string sentinel = "HEXALITH_SDK_CHILD_SECRET_SENTINEL";
        Environment.SetEnvironmentVariable(sentinel, "must-not-reach-sdk");
        try
        {
            string output = Path.Combine(root, "controls.txt");
            string project = Path.Combine(root, "Probe.proj");
            await File.WriteAllTextAsync(project, "<Project><Target Name=\"Probe\"><WriteLinesToFile File=\"" + output + "\" Lines=\"$(DOTNET_GENERATE_ASPNET_CERTIFICATE)|$(DOTNET_CLI_TELEMETRY_OPTOUT)|$(MSBUILDDISABLENODEREUSE)|$(BuildInParallel)|$(RestoreBuildInParallel)|$(HEXALITH_SDK_CHILD_SECRET_SENTINEL)|$(HEXALITH_SOURCE_MAPPING_HASH)\" Overwrite=\"true\" /></Target></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult result = await CompositionSdkProcess.RunAsync(root, new Dictionary<string, string>(StringComparer.Ordinal) { ["HEXALITH_SOURCE_MAPPING_HASH"] = "verified" }, "msbuild", project, "-nologo", "-target:Probe").ConfigureAwait(true);
            result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
            result.TimedOut.ShouldBeFalse();
            (await File.ReadAllTextAsync(output, TestContext.Current.CancellationToken).ConfigureAwait(true)).Trim().ShouldBe("false|1|1|false|false||verified");
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinel, null);
            CompositionTestFiles.Delete(root);
        }
    }
}
