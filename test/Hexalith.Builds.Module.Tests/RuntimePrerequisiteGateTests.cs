// <copyright file="RuntimePrerequisiteGateTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the exact G-6 exception tuple prerequisite.
/// </summary>
public sealed class RuntimePrerequisiteGateTests
{
    /// <summary>
    /// Verifies the owner-approved runtime/package pair passes G-6.
    /// </summary>
    [Fact]
    public void ApprovedExceptionTupleIsAvailable()
    {
        RuntimePrerequisiteCheck result = RuntimePrerequisiteGate.Check(CreateManifest("1.18.2", "1.18.7"));

        result.IsAvailable.ShouldBeTrue();
        result.Diagnostic.ShouldBeNull();
    }

    /// <summary>
    /// Verifies any stale runtime pin remains fail-closed.
    /// </summary>
    [Fact]
    public void StaleRuntimeTupleIsUnavailable()
    {
        RuntimePrerequisiteCheck result = RuntimePrerequisiteGate.Check(CreateManifest("1.18.1", "1.18.7"));

        result.IsAvailable.ShouldBeFalse();
        result.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXR002");
    }

    private static ModuleManifest CreateManifest(string runtimeVersion, string sdkVersion) => new(
        "hexalith.module-manifest.v1",
        "g6-test",
        [],
        new PlatformPins("3.90.0", runtimeVersion, sdkVersion, "4.0.1"),
        null,
        new Dictionary<string, ModuleProfile>());
}
