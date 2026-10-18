// <copyright file="CompositionAspireHostPathsTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Runtime.InteropServices;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the runner replaces the AppHost's baked Aspire paths with this machine's catalog packages and run-private
/// directories, and fails closed when a package is missing.
/// </summary>
public sealed class CompositionAspireHostPathsTests
{
    /// <summary>Verifies both executables resolve under the catalog-version package folders.</summary>
    /// <param name="runtimeIdentifier">The package runtime identifier.</param>
    /// <param name="dcp">The DCP file name.</param>
    /// <param name="dashboard">The dashboard file name.</param>
    [Theory]
    [InlineData("linux-x64", "dcp", "Aspire.Dashboard")]
    [InlineData("osx-arm64", "dcp", "Aspire.Dashboard")]
    [InlineData("win-x64", "dcp.exe", "Aspire.Dashboard.exe")]
    public void ResolvesBothExecutablesFromTheCatalogVersionPackages(string runtimeIdentifier, string dcp, string dashboard)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string expectedDcp = Touch(root, "aspire.hosting.orchestration." + runtimeIdentifier, "13.6.1", "tools", dcp);
            string expectedDashboard = Touch(root, "aspire.dashboard.sdk." + runtimeIdentifier, "13.6.1", "tools", dashboard);

            ToolDiagnostic? diagnostic = CompositionAspireHostPaths.TryResolve(root, "13.6.1", runtimeIdentifier, out CompositionAspireHostPaths? paths);

            diagnostic.ShouldBeNull();
            CompositionAspireHostPaths resolved = paths.ShouldNotBeNull();
            resolved.DcpCliPath.ShouldBe(expectedDcp);
            resolved.DashboardPath.ShouldBe(expectedDashboard);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a prerelease catalog version maps to NuGet's lowercase normalized folder.</summary>
    [Fact]
    public void UsesTheLowercaseNormalizedVersionFolder()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string expectedDcp = Touch(root, "aspire.hosting.orchestration.linux-x64", "13.7.0-preview.1.2", "tools", "dcp");
            _ = Touch(root, "aspire.dashboard.sdk.linux-x64", "13.7.0-preview.1.2", "tools", "Aspire.Dashboard");

            ToolDiagnostic? diagnostic = CompositionAspireHostPaths.TryResolve(root, "13.7.0-Preview.1.2", "linux-x64", out CompositionAspireHostPaths? paths);

            diagnostic.ShouldBeNull();
            paths.ShouldNotBeNull().DcpCliPath.ShouldBe(expectedDcp);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies a missing package is an <c>HXR016</c> prerequisite naming the expected path, with no fallback.</summary>
    /// <param name="present">The only package present.</param>
    /// <param name="missingPackage">The package expected to be named.</param>
    [Theory]
    [InlineData("aspire.dashboard.sdk.linux-x64", "aspire.hosting.orchestration.linux-x64")]
    [InlineData("aspire.hosting.orchestration.linux-x64", "aspire.dashboard.sdk.linux-x64")]
    [InlineData(null, "aspire.hosting.orchestration.linux-x64")]
    public void MissingPackageIsANamedPrerequisite(string? present, string missingPackage)
    {
        ArgumentNullException.ThrowIfNull(missingPackage);
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            if (present is not null)
            {
                _ = Touch(root, present, "13.6.1", "tools", present.StartsWith("aspire.dashboard", StringComparison.Ordinal) ? "Aspire.Dashboard" : "dcp");
            }

            // A copy at another Aspire version must not be used instead.
            _ = Touch(root, missingPackage, "13.5.4", "tools", missingPackage.StartsWith("aspire.dashboard", StringComparison.Ordinal) ? "Aspire.Dashboard" : "dcp");

            ToolDiagnostic? diagnostic = CompositionAspireHostPaths.TryResolve(root, "13.6.1", "linux-x64", out CompositionAspireHostPaths? paths);

            paths.ShouldBeNull();
            ToolDiagnostic found = diagnostic.ShouldNotBeNull();
            found.RuleId.ShouldBe("HXR016");
            found.Phase.ShouldBe(ToolPhase.Prerequisite);
            found.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
            found.Message.ShouldContain(Path.Combine(root, missingPackage, "13.6.1", "tools"));
            found.Message.ShouldNotContain("13.5.4");
            found.Hint.ShouldNotBeNull().ShouldContain("Aspire.AppHost.Sdk/13.6.1");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies an unsupported platform is a prerequisite failure rather than a guessed package.</summary>
    [Fact]
    public void UnsupportedPlatformIsANamedPrerequisite()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            ToolDiagnostic? diagnostic = CompositionAspireHostPaths.TryResolve(root, "13.6.1", null, out CompositionAspireHostPaths? paths);

            paths.ShouldBeNull();
            diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXR016");
            diagnostic.Field.ShouldBe("runtimeIdentifier");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies the package runtime identifiers Aspire ships orchestration for.</summary>
    [Fact]
    public void MapsPlatformsToAspirePackageRuntimeIdentifiers()
    {
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.Linux, Architecture.X64).ShouldBe("linux-x64");
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.Linux, Architecture.Arm64).ShouldBe("linux-arm64");
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.OSX, Architecture.Arm64).ShouldBe("osx-arm64");
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.Windows, Architecture.X64).ShouldBe("win-x64");
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.Linux, Architecture.X86).ShouldBeNull();
        CompositionAspireHostPaths.RuntimeIdentifier(OSPlatform.FreeBSD, Architecture.X64).ShouldBeNull();
        CompositionAspireHostPaths.RuntimeIdentifier(null, Architecture.X64).ShouldBeNull();
    }

    /// <summary>Verifies <c>NUGET_PACKAGES</c> selects the packages folder and the user default applies otherwise.</summary>
    [Fact]
    public void PackagesRootHonoursNuGetPackagesThenTheUserDefault()
    {
        string profile = Path.Combine(Path.GetTempPath(), "profile");
        string configured = Path.Combine(Path.GetTempPath(), "custom-packages");

        CompositionAspireHostPaths.DefaultPackagesRoot(name => name == "NUGET_PACKAGES" ? configured : null, profile).ShouldBe(configured);
        CompositionAspireHostPaths.DefaultPackagesRoot(_ => " ", profile).ShouldBe(Path.Combine(profile, ".nuget", "packages"));
        CompositionAspireHostPaths.DefaultPackagesRoot(_ => null, profile).ShouldBe(Path.Combine(profile, ".nuget", "packages"));
    }

    /// <summary>Verifies the AppHost environment carries the executables and creates run-private directories.</summary>
    [Fact]
    public void EnvironmentCreatesRunPrivateAppHostAndStoreDirectories()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string workspace = Path.Combine(root, "workspace");
            CompositionAspireHostPaths paths = new(Path.Combine(root, "dcp"), Path.Combine(root, "Aspire.Dashboard"));

            IReadOnlyDictionary<string, string> environment = paths.CreateEnvironment(workspace);

            environment.Keys.Order(StringComparer.Ordinal).ShouldBe(
                ["DcpPublisher__CliPath", "DcpPublisher__DashboardPath", "HEXALITH_G4_APPHOST_DIRECTORY", "HEXALITH_G4_ASPIRE_STORE_PATH"]);
            environment["DcpPublisher__CliPath"].ShouldBe(paths.DcpCliPath);
            environment["DcpPublisher__DashboardPath"].ShouldBe(paths.DashboardPath);
            environment[CompositionEnvironment.AppHostDirectory].ShouldBe(Path.Combine(workspace, "apphost"));
            environment[CompositionEnvironment.AspireStorePath].ShouldBe(Path.Combine(workspace, "aspire-store"));
            Directory.Exists(environment[CompositionEnvironment.AppHostDirectory]).ShouldBeTrue();
            Directory.Exists(environment[CompositionEnvironment.AspireStorePath]).ShouldBeTrue();
            if (!OperatingSystem.IsWindows())
            {
                File.GetUnixFileMode(environment[CompositionEnvironment.AspireStorePath])
                    .ShouldBe(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string Touch(string root, params string[] segments)
    {
        string path = Path.Combine([root, .. segments]);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }
}
