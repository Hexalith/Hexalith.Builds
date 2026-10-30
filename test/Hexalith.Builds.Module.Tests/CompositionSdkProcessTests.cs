// <copyright file="CompositionSdkProcessTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Proves SDK launch controls before workspace SDK assertions execute.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
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
            Assert.Skip("This qualification-only assertion requires the controlled catalog SDK interception harness.");
        }

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["--hexalith-controlled-interception-probe"], AppContext.BaseDirectory, null),
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
        result.TimedOut.ShouldBeFalse();
        result.Output.Trim().ShouldBe("intercepted|certificate=false|serial=1");
    }

    /// <summary>Verifies the actual SDK stays on a latestPatch resolution of the repository pin.</summary>
    /// <returns>The bounded SDK probe assertion.</returns>
    [Fact]
    public async Task SelectedSdkReportsPinnedVersionAsync()
    {
        string root = CompositionTestFiles.RepositoryRoot();
        using JsonDocument global = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "global.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement sdk = global.RootElement.GetProperty("sdk");
        CompositionProcessResult result = await CompositionSdkProcess.RunAsync(root, null, "--version").ConfigureAwait(true);
        result.Started.ShouldBeTrue();
        result.TimedOut.ShouldBeFalse();
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);

        // global.json uses latestPatch, so a runner may report a newer patch in the pinned feature band.
        sdk.GetProperty("rollForward").GetString().ShouldBe("latestPatch");
        string observed = result.Output.Trim();
        SatisfiesLatestPatchRollForward(observed, Version.Parse(sdk.GetProperty("version").GetString()!)).ShouldBeTrue(observed);
    }

    /// <summary>Verifies a waiting SDK descendant is observed before timeout and terminates within teardown.</summary>
    /// <returns>The SDK cancellation and owned descendant assertions.</returns>
    [Fact]
    public async Task SdkChildStopsAtExplicitBoundAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        Process? descendant = null;
        Task<CompositionProcessResult>? waiting = null;
        try
        {
            string childProject = Path.Combine(root, "WaitChild.csproj");
            string childOutput = Path.Combine(root, "out");
            await File.WriteAllTextAsync(childProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(root, "Program.cs"), "File.WriteAllText(args[0], Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); await Task.Delay(TimeSpan.FromMinutes(1));", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult built = await CompositionSdkProcess.RunAsync(root, null, "build", childProject, "--configuration", "Debug", "--output", childOutput, "-v:q", "-p:NuGetAudit=false").ConfigureAwait(true);
            built.ExitCode.ShouldBe(0, built.Output + built.ErrorOutput);
            string marker = Path.Combine(root, "waiting-child.pid");
            string project = Path.Combine(root, "Wait.proj");
            string executable = CompositionSdkProcess.CreateStartInfo(["--version"], root).FileName;
            string command = "\"" + executable + "\" \"" + Path.Combine(childOutput, "WaitChild.dll") + "\" \"" + marker + "\"";
            new XDocument(new XElement("Project", new XElement("Target", new XAttribute("Name", "Wait"), new XElement("Exec", new XAttribute("Command", command))))).Save(project);
            Stopwatch elapsed = Stopwatch.StartNew();
            waiting = CompositionProcess.RunAsync(
                CompositionSdkProcess.CreateStartInfo(["msbuild", project, "-nologo", "-target:Wait"], root),
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            int pid = 0;
            while (!waiting.IsCompleted && (!File.Exists(marker) || !int.TryParse(await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken).ConfigureAwait(true), out pid)))
            {
                await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            pid.ShouldBeGreaterThan(0, "the real waiting descendant must start before timeout can earn credit");
            descendant = Process.GetProcessById(pid);
            descendant.HasExited.ShouldBeFalse();
            CompositionProcessResult result = await waiting.ConfigureAwait(true);
            result.Started.ShouldBeTrue();
            result.TimedOut.ShouldBeTrue();
            result.ExitCode.ShouldBe(-1);
            using CancellationTokenSource teardown = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            teardown.CancelAfter(TimeSpan.FromSeconds(5));
            await descendant.WaitForExitAsync(teardown.Token).ConfigureAwait(true);
            descendant.HasExited.ShouldBeTrue("the observed owned descendant must terminate during bounded teardown");
            elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30));
        }
        finally
        {
            try
            {
                if (waiting is not null)
                {
                    _ = await waiting.ConfigureAwait(true);
                }
            }
            finally
            {
                if (descendant is not null)
                {
                    CompositionProcess.Kill(descendant);
                    descendant.Dispose();
                }

                CompositionTestFiles.Delete(root);
            }
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

    /// <summary>Verifies ambient writable directories cannot select the launcher's default caches.</summary>
    [Fact]
    public void DefaultDirectoriesIgnoreAmbientSelections()
    {
        _ = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot());
        string root = CompositionTestFiles.CreateDirectory();
        string[] names = ["DOTNET_CLI_HOME", "NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "NUGET_PLUGINS_CACHE_PATH", "NUGET_SCRATCH", "TMPDIR", "TEMP", "TMP"];
        Dictionary<string, string?> original = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (string name in names)
            {
                Environment.SetEnvironmentVariable(name, root);
            }

            ProcessStartInfo defaults = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot());
            foreach (string name in names)
            {
                defaults.Environment[name].ShouldNotBe(root, name);
            }

            ProcessStartInfo explicitSelection = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot(), new Dictionary<string, string>(StringComparer.Ordinal) { ["NUGET_PACKAGES"] = root });
            explicitSelection.Environment["NUGET_PACKAGES"].ShouldBe(root);
            defaults.Environment["HOME"].ShouldBe(Environment.GetEnvironmentVariable("HOME"));
        }
        finally
        {
            foreach ((string name, string? value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a selected symlink retains its launch spelling and uses the physical SDK installation.</summary>
    /// <returns>The symlink SDK launch assertion.</returns>
    [Fact]
    public async Task SymlinkSelectionUsesPhysicalInstallationAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            ProcessStartInfo selected = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot());
            string link = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
            try
            {
                _ = File.CreateSymbolicLink(link, selected.FileName);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
            {
                Assert.Skip("This assertion requires executable symbolic-link support: " + exception.Message);
            }

            ProcessStartInfo start = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot(), executable: link);
            start.FileName.ShouldBe(link);
            start.Environment["DOTNET_HOST_PATH"].ShouldBe(link);
            start.Environment["DOTNET_ROOT"].ShouldBe(Path.GetDirectoryName(new FileInfo(link).ResolveLinkTarget(returnFinalTarget: true)!.FullName));
            CompositionProcessResult result = await CompositionProcess.RunAsync(start, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
            result.TimedOut.ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies process-exit cleanup removes only launcher-owned defaults and preserves explicit fixture caches.</summary>
    /// <returns>The separate-process cleanup assertion.</returns>
    [Fact]
    public async Task OwnedDirectoriesAreRemovedWhenProcessExitsAsync()
    {
        const string observationVariable = "HEXALITH_SDK_CLEANUP_OBSERVATION";
        const string explicitVariable = "HEXALITH_SDK_CLEANUP_EXPLICIT_PATH";
        if (Environment.GetEnvironmentVariable(observationVariable) is string observation)
        {
            ProcessStartInfo probe = CompositionSdkProcess.CreateStartInfo(["--version"], CompositionTestFiles.RepositoryRoot(), new Dictionary<string, string>(StringComparer.Ordinal) { ["NUGET_PACKAGES"] = Environment.GetEnvironmentVariable(explicitVariable)! });
            Dictionary<string, string> paths = new(StringComparer.Ordinal)
            {
                ["owned"] = Path.GetDirectoryName(probe.Environment["DOTNET_CLI_HOME"])!,
                ["explicit"] = probe.Environment["NUGET_PACKAGES"]!,
            };
            await File.WriteAllTextAsync(observation, JsonSerializer.Serialize(paths), TestContext.Current.CancellationToken).ConfigureAwait(true);
            return;
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string explicitDirectory = Path.Combine(root, "selected-cache");
            _ = Directory.CreateDirectory(explicitDirectory);
            string sentinel = Path.Combine(explicitDirectory, "preserve.txt");
            await File.WriteAllTextAsync(sentinel, "preserved", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string observed = Path.Combine(root, "cleanup.json");
            ProcessStartInfo start = CompositionSdkProcess.CreateStartInfo(
                ["exec", typeof(CompositionSdkProcessTests).Assembly.Location, "-method", typeof(CompositionSdkProcessTests).FullName + "." + nameof(OwnedDirectoriesAreRemovedWhenProcessExitsAsync), "-parallel", "none"],
                CompositionTestFiles.RepositoryRoot(),
                new Dictionary<string, string>(StringComparer.Ordinal) { [observationVariable] = observed, [explicitVariable] = explicitDirectory });
            CompositionProcessResult result = await CompositionProcess.RunAsync(start, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
            result.TimedOut.ShouldBeFalse();
            using JsonDocument paths = JsonDocument.Parse(await File.ReadAllTextAsync(observed, TestContext.Current.CancellationToken).ConfigureAwait(true));
            Directory.Exists(paths.RootElement.GetProperty("owned").GetString()).ShouldBeFalse("the SDK launcher must clean its own default tree when the child test process exits");
            paths.RootElement.GetProperty("explicit").GetString().ShouldBe(explicitDirectory);
            (await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("preserved");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Accepts the pinned SDK and a newer patch in the same feature band.</summary>
    /// <param name="sdkVersion">The SDK version the runner resolved.</param>
    /// <param name="pinned">The version declared in global.json.</param>
    /// <returns>Whether the resolved version satisfies latestPatch.</returns>
    private static bool SatisfiesLatestPatchRollForward(string sdkVersion, Version pinned)
    {
        int suffixIndex = sdkVersion.IndexOfAny(['-', '+']);
        Version resolved = Version.Parse(suffixIndex < 0 ? sdkVersion : sdkVersion[..suffixIndex]);
        return resolved.Major == pinned.Major
            && resolved.Minor == pinned.Minor
            && resolved.Build / 100 == pinned.Build / 100
            && resolved >= pinned;
    }
}
