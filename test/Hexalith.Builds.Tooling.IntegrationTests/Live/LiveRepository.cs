// <copyright file="LiveRepository.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Reflection;

/// <summary>
/// Locates the built Builds-owned AppHost and the executable fixture manifest.
/// </summary>
internal static class LiveRepository
{
    /// <summary>
    /// Gets the repository root.
    /// </summary>
    public static string Root { get; } = FindRoot();

    /// <summary>
    /// Gets the executable two-module fixture manifest.
    /// </summary>
    public static string Manifest => Path.Combine(Root, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");

    /// <summary>
    /// Gets the built AppHost assembly for the test configuration.
    /// </summary>
    public static string AppHostAssembly => Path.Combine(
        Root,
        "src",
        "hosts",
        "Hexalith.Builds.Module.AppHost",
        "bin",
        typeof(LiveRepository).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug",
        "net10.0",
        "Hexalith.Builds.Module.AppHost.dll");

    /// <summary>
    /// Gets the runner assembly hosting the private descriptor child command.
    /// </summary>
    public static string DescriptorChildAssembly => Assembly.Load("Hexalith.Builds.Module.Cli").Location;

    private static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hexalith.Builds repository root.");
    }
}