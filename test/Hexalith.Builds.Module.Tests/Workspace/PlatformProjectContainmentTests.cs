// <copyright file="PlatformProjectContainmentTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Private source hosts remain inside the exact tool-selected Platform checkout.</summary>
public sealed class PlatformProjectContainmentTests
{
    /// <summary>Source mode honors the selected Platform identity without admitting foreign or unselected nested projects.</summary>
    /// <param name="mode">The tool-selected dependency mode.</param>
    /// <param name="expected">Whether the selected Platform project is admitted.</param>
    [Theory]
    [InlineData(WorkspaceMode.Source, true)]
    [InlineData(WorkspaceMode.Package, false)]
    public void SelectedPlatformProjectsKeepModeAndNestedRepositoryBoundaries(WorkspaceMode mode, bool expected)
    {
        string root = Path.Combine(Path.GetTempPath(), "platform-containment-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            string platform = Path.Combine(root, "selected-platform");
            string project = Project(platform, "src/Private/Private.csproj");
            string nested = Project(platform, "references/Unselected/Unselected.csproj");
            string foreign = Project(Path.Combine(root, "foreign"), "Foreign.csproj");
            SourceMapping mapping = new(mode, root, "Hexalith.Parties", [], true)
            {
                PlatformIdentity = new("commit", "commit", "commit", platform, null, null, null, "record", false),
            };
            mapping.ContainsProject(project).ShouldBe(expected);
            mapping.ContainsProject(nested).ShouldBeFalse();
            mapping.ContainsProject(foreign).ShouldBeFalse();
            mapping.ContainsProject(Path.Combine(platform, "..", "foreign", "Foreign.csproj")).ShouldBeFalse();
            if (!OperatingSystem.IsWindows())
            {
                string link = Path.Combine(platform, "src", "escape");
                _ = Directory.CreateSymbolicLink(link, Path.GetDirectoryName(foreign)!);
                mapping.ContainsProject(Path.Combine(link, "Foreign.csproj")).ShouldBeFalse();
            }

            SourceMapping explicitlySelected = mapping with { Entries = [new("Unselected", "source", Path.GetDirectoryName(nested))] };
            if (mode == WorkspaceMode.Source)
            {
                explicitlySelected.ContainsProject(nested).ShouldBeTrue();
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static string Project(string root, string relative)
    {
        string path = Path.Combine(root, relative);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return path;
    }
}
