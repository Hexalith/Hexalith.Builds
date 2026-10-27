// <copyright file="CompositionSdkProcess.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.Tooling.Runtime;

using Xunit;

/// <summary>Creates controlled SDK children for tests without changing production process behavior.</summary>
internal static class CompositionSdkProcess
{
    private static readonly string _privateRoot = Path.Combine(Path.GetTempPath(), "hexalith-sdk-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Creates a scrubbed SDK start description with explicit safety and serial controls.</summary>
    /// <param name="arguments">The SDK command and arguments.</param>
    /// <param name="directory">The working directory.</param>
    /// <param name="environment">Explicit fixture or mapping values.</param>
    /// <param name="executable">An optional absolute dotnet executable; otherwise the test selection or installed SDK is resolved automatically.</param>
    /// <returns>The controlled start description.</returns>
    /// <exception cref="ArgumentException">The executable or private directory selection is invalid.</exception>
    public static ProcessStartInfo CreateStartInfo(IEnumerable<string> arguments, string directory, IReadOnlyDictionary<string, string>? environment = null, string? executable = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        string expectedName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        string? selected = executable ?? Environment.GetEnvironmentVariable("HEXALITH_TEST_DOTNET_EXECUTABLE")
            ?? Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (selected is null && Environment.GetEnvironmentVariable("DOTNET_ROOT") is string root)
        {
            selected = Path.Combine(root, expectedName);
        }

        selected ??= (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator)
            .Where(Path.IsPathFullyQualified).Select(directoryPath => Path.Combine(directoryPath, expectedName)).FirstOrDefault(File.Exists);
        if (selected is null || !Path.IsPathFullyQualified(selected) || !File.Exists(selected)
            || !Path.GetFileName(selected).Equals(expectedName, StringComparison.OrdinalIgnoreCase)
            || (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(selected) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) == 0))
        {
            throw new ArgumentException("The selected test SDK must be an existing absolute dotnet executable.", nameof(executable));
        }

        ProcessStartInfo start = CompositionProcess.CreateStartInfo(selected, [], directory, null);
        start.Environment.Clear();
        foreach (string name in new[] { "PATH", "SYSTEMROOT", "WINDIR", "LANG", "HOME", "USERPROFILE" })
        {
            if (Environment.GetEnvironmentVariable(name) is string value)
            {
                start.Environment[name] = value;
            }
        }

        if (environment is not null)
        {
            foreach ((string name, string value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        CompositionWorkspace.CreatePrivateDirectory(_privateRoot);
        foreach ((string name, string leaf) in new[]
        {
            ("DOTNET_CLI_HOME", "cli"), ("NUGET_PACKAGES", "packages"),
            ("NUGET_HTTP_CACHE_PATH", "http-cache"), ("NUGET_PLUGINS_CACHE_PATH", "plugins-cache"), ("NUGET_SCRATCH", "scratch"),
            ("TMPDIR", "temp"), ("TEMP", "temp"), ("TMP", "temp"),
        })
        {
            string value = start.Environment.TryGetValue(name, out string? explicitValue) && explicitValue is not null
                ? explicitValue
                : Environment.GetEnvironmentVariable(name) ?? Path.Combine(_privateRoot, leaf);
            if (!Path.IsPathFullyQualified(value))
            {
                throw new ArgumentException("SDK test directory must be absolute: " + name, nameof(environment));
            }

            _ = Directory.CreateDirectory(value);
            start.Environment[name] = value;
        }

        start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"] = "false";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(selected);
        start.Environment["DOTNET_HOST_PATH"] = selected;

        string[] command = [.. arguments];
        foreach (string argument in command)
        {
            start.ArgumentList.Add(argument);
        }

        if (command.FirstOrDefault() is "build" or "restore" or "pack" or "msbuild")
        {
            start.ArgumentList.Add("-m:1");
            start.ArgumentList.Add("-nr:false");
            start.ArgumentList.Add("-p:BuildInParallel=false");
            start.ArgumentList.Add("-p:RestoreBuildInParallel=false");
            start.ArgumentList.Add("-p:UseSharedCompilation=false");
            if (command[0] == "restore")
            {
                start.ArgumentList.Add("--disable-parallel");
            }
        }

        return start;
    }

    /// <summary>Runs a controlled SDK child within the test's finite deadline.</summary>
    /// <param name="directory">The working directory.</param>
    /// <param name="environment">Explicit fixture or mapping values.</param>
    /// <param name="arguments">The SDK command and arguments.</param>
    /// <returns>The bounded process result.</returns>
    public static Task<CompositionProcessResult> RunAsync(string directory, IReadOnlyDictionary<string, string>? environment, params string[] arguments) =>
        CompositionProcess.RunAsync(CreateStartInfo(arguments, directory, environment), TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
}
