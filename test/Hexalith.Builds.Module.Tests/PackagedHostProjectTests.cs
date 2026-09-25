// <copyright file="PackagedHostProjectTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies packaged host shims are built from private run-scoped copies and never from the installed tool.
/// </summary>
public sealed class PackagedHostProjectTests
{
    /// <summary>Checks a source AppHost layout falls back to its source host projects.</summary>
    [Fact]
    public void SourceLayoutIsNotMaterialized()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            PackagedHostProject.TryMaterialize(root, "EventStore", "EventStoreHost", Path.Combine(root, "run", "hosts", "eventstore"), out string? project)
                .ShouldBeFalse();
            project.ShouldBeNull();
            Directory.Exists(Path.Combine(root, "run")).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks each resource receives its own shim bound to the packaged binaries, leaving the package untouched.</summary>
    [Fact]
    public void PackagedLayoutIsCopiedPerResource()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string package = SeedPackage(root);
            string first = Path.Combine(root, "run", "hosts", "eventstore");
            string second = Path.Combine(root, "run", "hosts", "eventstore-2");

            PackagedHostProject.TryMaterialize(package, "EventStore", "EventStoreHost", first, out string? firstProject).ShouldBeTrue();
            PackagedHostProject.TryMaterialize(package, "EventStore", "EventStoreHost", second, out string? secondProject).ShouldBeTrue();

            firstProject.ShouldBe(Path.Combine(first, PackagedHostProject.ProjectFileName));
            secondProject.ShouldBe(Path.Combine(second, PackagedHostProject.ProjectFileName));
            File.ReadAllText(firstProject!).ShouldBe("<Project />");
            File.Exists(Path.Combine(second, "Placeholder.cs")).ShouldBeTrue();
            File.ReadAllText(Path.Combine(first, "PackagedHost.props"))
                .ShouldContain("<PackagedHostDirectory>" + Path.Combine(package, "bin", "EventStoreHost") + Path.DirectorySeparatorChar + "</PackagedHostDirectory>");
            Directory.GetFileSystemEntries(Path.Combine(package, "projects", "EventStore")).Length.ShouldBe(2);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks a package with only part of the host layout fails closed instead of using source paths.</summary>
    [Fact]
    public void PartialPackagedLayoutFailsClosed()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string package = SeedPackage(root);
            File.Delete(Path.Combine(package, "projects", "EventStore", PackagedHostProject.ProjectFileName));

            _ = Should.Throw<InvalidOperationException>(() =>
                PackagedHostProject.TryMaterialize(package, "EventStore", "EventStoreHost", Path.Combine(root, "run", "hosts", "eventstore"), out _));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string SeedPackage(string root)
    {
        string package = Path.Combine(root, "g4-host");
        _ = Directory.CreateDirectory(Path.Combine(package, "projects", "EventStore"));
        _ = Directory.CreateDirectory(Path.Combine(package, "bin", "EventStoreHost"));
        File.WriteAllText(Path.Combine(package, "projects", "EventStore", PackagedHostProject.ProjectFileName), "<Project />");
        File.WriteAllText(Path.Combine(package, "projects", "EventStore", "Placeholder.cs"), "// placeholder");
        File.WriteAllText(Path.Combine(package, "bin", "EventStoreHost", "Hexalith.Builds.Module.EventStoreHost.dll"), "binary");
        return package;
    }
}
