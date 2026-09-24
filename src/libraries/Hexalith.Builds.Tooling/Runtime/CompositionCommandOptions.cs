// <copyright file="CompositionCommandOptions.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Locates the Builds-owned AppHost for a public command without using a consumer-owned topology.
/// </summary>
public static class CompositionCommandOptions
{
    /// <summary>
    /// Creates the public composition settings for a runner entry assembly.
    /// </summary>
    /// <param name="runnerAssembly">The runner assembly that hosts the descriptor child.</param>
    /// <returns>The production composition settings.</returns>
    public static CompositionEngineOptions Create(string runnerAssembly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runnerAssembly);
        string? configured = Environment.GetEnvironmentVariable("HEXALITH_G4_APPHOST");
        string packaged = Path.Combine(Path.GetDirectoryName(runnerAssembly)!, "g4-host", "Hexalith.Builds.Module.AppHost.dll");
        string appHost = packaged;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            appHost = configured;
        }
        else if (!File.Exists(packaged))
        {
            appHost = FindSourceAppHost(runnerAssembly) ?? packaged;
        }

        return new CompositionEngineOptions(appHost, runnerAssembly);
    }

    private static string? FindSourceAppHost(string runnerAssembly)
    {
        string configuration = runnerAssembly.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? "Release"
            : "Debug";
        DirectoryInfo? directory = new(Path.GetDirectoryName(runnerAssembly)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return Path.Combine(
                    directory.FullName,
                    "src",
                    "hosts",
                    "Hexalith.Builds.Module.AppHost",
                    "bin",
                    configuration,
                    "net10.0",
                    "Hexalith.Builds.Module.AppHost.dll");
            }

            directory = directory.Parent;
        }

        return null;
    }
}
