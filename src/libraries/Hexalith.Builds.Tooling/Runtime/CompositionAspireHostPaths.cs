// <copyright file="CompositionAspireHostPaths.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Runtime.InteropServices;

using Hexalith.Builds.Tooling.Diagnostics;

using NuGet.Versioning;

/// <summary>
/// Local Aspire orchestration paths that replace the build-machine paths an AppHost bakes into its assembly metadata.
/// </summary>
/// <remarks>
/// A packaged AppHost carries the DCP, dashboard, project and intermediate-output paths of the machine that built it.
/// The runner resolves DCP and the dashboard from this machine's NuGet global packages folder at the catalog Aspire
/// version, and gives the AppHost directory and the Aspire store a run-private directory. A missing package is a
/// prerequisite failure; no other location is tried.
/// </remarks>
/// <param name="DcpCliPath">The absolute path of the DCP executable.</param>
/// <param name="DashboardPath">The absolute path of the Aspire dashboard executable.</param>
public sealed record CompositionAspireHostPaths(string DcpCliPath, string DashboardPath)
{
    /// <summary>The configuration key, as an environment variable, that Aspire reads for the DCP executable.</summary>
    public const string DcpCliPathVariable = "DcpPublisher__CliPath";

    /// <summary>The configuration key, as an environment variable, that Aspire reads for the dashboard executable.</summary>
    public const string DashboardPathVariable = "DcpPublisher__DashboardPath";

    /// <summary>The rule identity reported when the Aspire orchestration packages are unavailable.</summary>
    public const string UnavailableRuleId = "HXR016";

    /// <summary>The run-workspace subdirectory used as the AppHost directory.</summary>
    public const string AppHostDirectoryName = "apphost";

    /// <summary>The run-workspace subdirectory used as the Aspire store root.</summary>
    public const string AspireStoreDirectoryName = "aspire-store";

    /// <summary>Gets the NuGet global packages folder: <c>NUGET_PACKAGES</c> when set, otherwise the user default.</summary>
    /// <returns>The absolute packages folder.</returns>
    public static string DefaultPackagesRoot() => DefaultPackagesRoot(
        Environment.GetEnvironmentVariable,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>Gets the Aspire package runtime identifier of this machine, or null when Aspire ships no orchestration for it.</summary>
    /// <returns>The runtime identifier, such as <c>linux-x64</c>.</returns>
    public static string? CurrentRuntimeIdentifier()
    {
        OSPlatform? platform = null;
        if (OperatingSystem.IsWindows())
        {
            platform = OSPlatform.Windows;
        }
        else if (OperatingSystem.IsMacOS())
        {
            platform = OSPlatform.OSX;
        }
        else if (OperatingSystem.IsLinux())
        {
            platform = OSPlatform.Linux;
        }

        return RuntimeIdentifier(platform, RuntimeInformation.OSArchitecture);
    }

    /// <summary>Resolves the DCP and dashboard executables of this machine at an Aspire version.</summary>
    /// <param name="packagesRoot">The NuGet global packages folder.</param>
    /// <param name="aspireVersion">The catalog Aspire AppHost SDK version.</param>
    /// <param name="runtimeIdentifier">The Aspire package runtime identifier, or null when unsupported.</param>
    /// <param name="paths">The resolved paths, or null when a package is unavailable.</param>
    /// <returns>Null when both executables exist; otherwise an <c>HXR016</c> prerequisite diagnostic.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "NuGet global packages folder names are lowercase.")]
    public static ToolDiagnostic? TryResolve(string packagesRoot, string aspireVersion, string? runtimeIdentifier, out CompositionAspireHostPaths? paths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packagesRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(aspireVersion);
        paths = null;
        if (runtimeIdentifier is null)
        {
            return Unavailable(
                $"Aspire {aspireVersion} ships no DCP or dashboard package for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}.",
                "runtimeIdentifier",
                "Run the composition on a platform with Aspire orchestration packages (Linux, macOS or Windows on x64 or arm64).");
        }

        if (!NuGetVersion.TryParse(aspireVersion, out NuGetVersion? parsed))
        {
            return Unavailable($"Aspire version '{aspireVersion}' is not a NuGet version.", "aspireVersion", "Correct the catalog Aspire.AppHost.Sdk version.");
        }

        string version = parsed.ToNormalizedString().ToLowerInvariant();
        bool windows = runtimeIdentifier.StartsWith("win-", StringComparison.Ordinal);
        string root = Path.GetFullPath(packagesRoot);
        string dcp = Path.Combine(root, "aspire.hosting.orchestration." + runtimeIdentifier, version, "tools", windows ? "dcp.exe" : "dcp");
        string dashboard = Path.Combine(root, "aspire.dashboard.sdk." + runtimeIdentifier, version, "tools", windows ? "Aspire.Dashboard.exe" : "Aspire.Dashboard");
        string[] missing = [.. new[] { dcp, dashboard }.Where(path => !File.Exists(path))];
        if (missing.Length > 0)
        {
            return Unavailable(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Aspire {aspireVersion} orchestration for {runtimeIdentifier} is not in the NuGet global packages folder '{root}': missing {string.Join(", ", missing.Select(path => $"'{path}'"))}."),
                missing[0],
                $"Restore an AppHost that uses Aspire.AppHost.Sdk/{aspireVersion} on this machine (for example build the Platform apphost.cs), or set NUGET_PACKAGES to the folder that holds aspire.hosting.orchestration.{runtimeIdentifier} and aspire.dashboard.sdk.{runtimeIdentifier} {version}, then retry.");
        }

        paths = new CompositionAspireHostPaths(dcp, dashboard);
        return null;
    }

    /// <summary>Creates the run-private Aspire directories and returns the AppHost environment overrides.</summary>
    /// <param name="workspace">The private run workspace.</param>
    /// <returns>The DCP, dashboard, AppHost-directory and store variables.</returns>
    public IReadOnlyDictionary<string, string> CreateEnvironment(string workspace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        string appHostDirectory = Path.Combine(workspace, AppHostDirectoryName);
        string storeDirectory = Path.Combine(workspace, AspireStoreDirectoryName);
        CompositionWorkspace.CreatePrivateDirectory(appHostDirectory);
        CompositionWorkspace.CreatePrivateDirectory(storeDirectory);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DcpCliPathVariable] = DcpCliPath,
            [DashboardPathVariable] = DashboardPath,
            [CompositionEnvironment.AppHostDirectory] = appHostDirectory,
            [CompositionEnvironment.AspireStorePath] = storeDirectory,
        };
    }

    /// <summary>Selects the NuGet global packages folder from an environment lookup and a user profile.</summary>
    /// <param name="environment">The environment lookup.</param>
    /// <param name="userProfile">The user profile directory.</param>
    /// <returns>The absolute packages folder.</returns>
    internal static string DefaultPackagesRoot(Func<string, string?> environment, string userProfile)
    {
        ArgumentNullException.ThrowIfNull(environment);
        string? configured = environment("NUGET_PACKAGES");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(userProfile, ".nuget", "packages")
            : configured);
    }

    /// <summary>Maps an operating system and architecture to an Aspire package runtime identifier.</summary>
    /// <param name="platform">The operating system, or null when unsupported.</param>
    /// <param name="architecture">The operating-system architecture.</param>
    /// <returns>The runtime identifier, or null when Aspire ships no orchestration for it.</returns>
    internal static string? RuntimeIdentifier(OSPlatform? platform, Architecture architecture)
    {
        string? system = null;
        if (platform == OSPlatform.Windows)
        {
            system = "win";
        }
        else if (platform == OSPlatform.OSX)
        {
            system = "osx";
        }
        else if (platform == OSPlatform.Linux)
        {
            system = "linux";
        }

        string? cpu = null;
        if (architecture == Architecture.X64)
        {
            cpu = "x64";
        }
        else if (architecture == Architecture.Arm64)
        {
            cpu = "arm64";
        }

        return system is null || cpu is null ? null : system + "-" + cpu;
    }

    private static ToolDiagnostic Unavailable(string message, string field, string hint) =>
        new(UnavailableRuleId, ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, message, field, hint);
}
