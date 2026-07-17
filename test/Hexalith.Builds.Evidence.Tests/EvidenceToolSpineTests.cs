// <copyright file="EvidenceToolSpineTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

using System.Reflection;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the evidence tool executable spine.
/// </summary>
public sealed class EvidenceToolSpineTests
{
    /// <summary>
    /// Verifies the tool assembly exposes an executable entry point.
    /// </summary>
    [Fact]
    public void EvidenceToolAssemblyHasEntryPoint()
    {
        Assembly assembly = Assembly.Load("Hexalith.Builds.Evidence.Cli");

        _ = assembly.EntryPoint.ShouldNotBeNull();
    }
}