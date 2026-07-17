// <copyright file="ModuleRunEvidenceSerializationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies canonical metadata-only module-run evidence serialization.
/// </summary>
public sealed class ModuleRunEvidenceSerializationTests
{
    /// <summary>
    /// Verifies canonical JSON orders unordered metadata and terminates the UTF-8 artifact with one newline.
    /// </summary>
    [Fact]
    public void SerializeCanonicalOrdersArtifactHashesAndTerminatesWithNewline()
    {
        ModuleRunEvidence evidence = new(
            "hexalith.module-run-evidence.v1",
            "0123456789abcdef0123456789abcdef",
            new ModuleRunTimestamps(
                new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 17, 10, 1, 0, TimeSpan.Zero)),
            new ModuleRunEnvironment("unavailable", "unknown", "10.0.302", "test-os", "1.0.0"),
            new ModuleRunInvocation(
                "hexalith-module down --manifest test/fixture.json --filter-sha256 HASH",
                "test/fixture.json",
                "MANIFEST_HASH",
                null,
                null,
                null,
                "HASH"),
            new ModuleRunTopology(
                [new ModuleRunModule("orders", "orders", "orders", "orders", "assemblies/orders.dll")],
                new PlatformPins("3.70.0", "1.18.0", "1.18.4", "4.0.1")),
            [new ModuleRunPhaseOutcome(ToolPhase.Cleanup, ToolFailureCategory.None, "HXI001")],
            new ModuleRunTestCounts(false, 0, 0, 0, 0),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["z-artifact.json"] = "Z_HASH",
                ["a-artifact.json"] = "A_HASH",
            },
            "completed",
            ToolOutcome.Passed(),
            ["timestamps.startedUtc", "runId", "timestamps.completedUtc"]);

        byte[] serialized = ModuleRunEvidenceWriter.SerializeCanonical(evidence);
        string text = Encoding.UTF8.GetString(serialized);

        serialized[^1].ShouldBe((byte)'\n');
        text.IndexOf("a-artifact.json", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("z-artifact.json", StringComparison.Ordinal));
        text.ShouldNotContain("Bearer");
        text.ShouldContain("\"volatileFields\":[\"runId\",\"timestamps.completedUtc\",\"timestamps.startedUtc\"]");
    }
}