// <copyright file="PackagedHostProject.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security;

/// <summary>
/// Materializes a packaged host shim project into a private run directory so that its build never writes into the installed tool.
/// </summary>
public static class PackagedHostProject
{
    /// <summary>The shim project file name.</summary>
    public const string ProjectFileName = "Host.csproj";

    private const string _placeholderFileName = "Placeholder.cs";

    private const string _propertiesFileName = "PackagedHost.props";

    /// <summary>
    /// Copies the packaged shim for one host into a private directory bound to the packaged host binaries.
    /// </summary>
    /// <param name="packageRoot">The directory that holds the packaged AppHost.</param>
    /// <param name="projectFolder">The shim folder under <c>projects</c>, for example <c>EventStore</c>.</param>
    /// <param name="hostFolder">The host binaries folder under <c>bin</c>, for example <c>EventStoreHost</c>.</param>
    /// <param name="destination">The private, run-scoped destination directory.</param>
    /// <param name="projectPath">The materialized shim project path, when the AppHost runs from a package.</param>
    /// <returns>False when the AppHost runs from source and no packaged host exists.</returns>
    /// <exception cref="InvalidOperationException">The package holds only part of the host layout.</exception>
    public static bool TryMaterialize(string packageRoot, string projectFolder, string hostFolder, string destination, out string? projectPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        projectPath = null;
        string template = Path.Combine(packageRoot, "projects", projectFolder);
        string binaries = Path.Combine(packageRoot, "bin", hostFolder);
        if (!Directory.Exists(template) && !Directory.Exists(binaries))
        {
            return false;
        }

        string templateProject = Path.Combine(template, ProjectFileName);
        string templatePlaceholder = Path.Combine(template, _placeholderFileName);
        if (!File.Exists(templateProject) || !File.Exists(templatePlaceholder) || !Directory.Exists(binaries))
        {
            throw new InvalidOperationException($"The packaged {projectFolder} host layout is incomplete.");
        }

        CompositionWorkspace.CreatePrivateDirectory(destination);
        projectPath = Path.Combine(destination, ProjectFileName);
        File.Copy(templateProject, projectPath, overwrite: true);
        File.Copy(templatePlaceholder, Path.Combine(destination, _placeholderFileName), overwrite: true);
        string hostDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(binaries)) + Path.DirectorySeparatorChar;
        File.WriteAllText(
            Path.Combine(destination, _propertiesFileName),
            "<Project>\n  <PropertyGroup>\n    <PackagedHostDirectory>" + SecurityElement.Escape(MSBuildEscape(hostDirectory)) + "</PackagedHostDirectory>\n  </PropertyGroup>\n</Project>\n");
        return true;
    }

    private static string MSBuildEscape(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("$", "%24", StringComparison.Ordinal)
        .Replace("@", "%40", StringComparison.Ordinal)
        .Replace(";", "%3B", StringComparison.Ordinal)
        .Replace("'", "%27", StringComparison.Ordinal);
}
