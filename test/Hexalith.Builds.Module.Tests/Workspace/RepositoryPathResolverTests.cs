// <copyright file="RepositoryPathResolverTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Filesystem;

using Shouldly;

using Xunit;

/// <summary>Checks physical containment at filesystem roots.</summary>
public sealed class RepositoryPathResolverTests
{
    /// <summary>Verifies a directory beneath a filesystem or drive root is contained.</summary>
    [Fact]
    public void FilesystemRootContainsItsDescendants()
    {
        string directory = Path.GetFullPath(Path.GetTempPath());
        string root = Path.GetPathRoot(directory)!;
        string relative = Path.GetRelativePath(root, directory);

        RepositoryPathResolver.TryResolvePathWithinRoot(root, relative, out string resolved).ShouldBeTrue();
        Directory.Exists(resolved).ShouldBeTrue();
    }
}
