// <copyright file="ToolAssemblySpineTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests;

using System.Reflection;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies both independently consumable command assemblies are present.
/// </summary>
public sealed class ToolAssemblySpineTests
{
    /// <summary>
    /// Verifies each command assembly has an executable entry point.
    /// </summary>
    [Fact]
    public void ToolAssembliesHaveIndependentEntryPoints()
    {
        _ = Assembly.Load("Hexalith.Builds.Module.Cli").EntryPoint.ShouldNotBeNull();
        _ = Assembly.Load("Hexalith.Builds.Evidence.Cli").EntryPoint.ShouldNotBeNull();
    }
}