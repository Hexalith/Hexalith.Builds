// <copyright file="ToolProjectSpineTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the repository-owned package and SDK spine.
/// </summary>
public sealed class ToolProjectSpineTests
{
    /// <summary>
    /// Verifies the exact SDK and approved tool package identities.
    /// </summary>
    [Fact]
    public void RepositorySpineDefinesAuthorizedToolContracts()
    {
        string repositoryRoot = FindRepositoryRoot();

        using JsonDocument globalJson = JsonDocument.Parse(File.ReadAllText(Path.Combine(repositoryRoot, "global.json")));
        JsonElement sdk = globalJson.RootElement.GetProperty("sdk");
        sdk.GetProperty("version").GetString().ShouldBe("10.0.302");
        sdk.GetProperty("rollForward").GetString().ShouldBe("latestPatch");

        AssertToolProject(
            repositoryRoot,
            "Hexalith.Builds.Module.Cli",
            "hexalith-module");
        AssertToolProject(
            repositoryRoot,
            "Hexalith.Builds.Evidence.Cli",
            "hexalith-evidence");

        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "Hexalith.Builds.slnx"));
        solution.ShouldContain("Hexalith.Builds.Module.Cli.csproj");
        solution.ShouldContain("Hexalith.Builds.Evidence.Cli.csproj");
        solution.ShouldContain("Hexalith.Builds.Module.Tests.csproj");
        solution.ShouldContain("Hexalith.Builds.Evidence.Tests.csproj");
        solution.ShouldContain("Hexalith.Builds.Tooling.IntegrationTests.csproj");
    }

    private static void AssertToolProject(string repositoryRoot, string packageId, string commandName)
    {
        string projectPath = Path.Combine(repositoryRoot, "src", "libraries", packageId, $"{packageId}.csproj");
        string project = File.ReadAllText(projectPath);
        project.ShouldContain($"<PackageId>{packageId}</PackageId>");
        project.ShouldContain("<PackAsTool>true</PackAsTool>");
        project.ShouldContain($"<ToolCommandName>{commandName}</ToolCommandName>");
        project.ShouldNotContain("Version=\"");
    }

    private static string FindRepositoryRoot()
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