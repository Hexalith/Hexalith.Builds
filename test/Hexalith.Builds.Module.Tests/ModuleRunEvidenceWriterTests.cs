// <copyright file="ModuleRunEvidenceWriterTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies fail-closed path handling and atomic canonical writes for module-run evidence.
/// </summary>
public sealed class ModuleRunEvidenceWriterTests
{
    /// <summary>
    /// Verifies non-canonical, escaping, non-JSON, and placeholder evidence paths are rejected.
    /// </summary>
    /// <param name="evidencePath">The rejected evidence path.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [InlineData("/rooted/evidence.json")]
    [InlineData("../escape/evidence.json")]
    [InlineData("evidence\\windows.json")]
    [InlineData("evidence/output.txt")]
    [InlineData("evidence/${SECRET}.json")]
    public async Task WriteAsyncRejectsNonCanonicalPathsAsync(string evidencePath)
    {
        ModuleRunEvidenceWriteResult result = await ModuleRunEvidenceWriter.WriteAsync(
            evidencePath,
            "manifest.json",
            CreateEvidence(),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Succeeded.ShouldBeFalse();
        ToolDiagnostic diagnostic = result.Diagnostic.ShouldNotBeNull();
        diagnostic.RuleId.ShouldBe("HXE160");
    }

    /// <summary>
    /// Verifies a valid evidence path writes canonical, newline-terminated JSON within the repository root.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task WriteAsyncWritesCanonicalArtifactAtomicallyAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), $"hexalith-builds-writer-{Guid.NewGuid():N}");
        try
        {
            _ = Directory.CreateDirectory(root);
            await File.WriteAllTextAsync(
                Path.Combine(root, ".git"),
                "gitdir: fake",
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            ModuleRunEvidenceWriteResult result = await ModuleRunEvidenceWriter.WriteAsync(
                "evidence/out.json",
                Path.Combine(root, "manifest.json"),
                CreateEvidence(),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Succeeded.ShouldBeTrue();
            string written = Path.Combine(root, "evidence", "out.json");
            File.Exists(written).ShouldBeTrue();

            byte[] bytes = await File.ReadAllBytesAsync(written, TestContext.Current.CancellationToken).ConfigureAwait(true);
            bytes[^1].ShouldBe((byte)'\n');
            using JsonDocument document = JsonDocument.Parse(bytes);
            document.RootElement.GetProperty("schema").GetString().ShouldBe("hexalith.module-run-evidence.v1");
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static ModuleRunEvidence CreateEvidence() => new(
        "hexalith.module-run-evidence.v1",
        "0123456789abcdef0123456789abcdef",
        new ModuleRunTimestamps(
            new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 17, 10, 1, 0, TimeSpan.Zero)),
        new ModuleRunEnvironment("unavailable", "unknown", "10.0.302", "test-os", "1.0.0"),
        new ModuleRunInvocation("hexalith-module test --profile full", "manifest.json", "HASH", "full", null, null, null),
        new ModuleRunTopology(
            [new ModuleRunModule("orders", "orders", "orders", "orders", "assemblies/orders.dll")],
            new PlatformPins("3.70.0", "1.18.0", "1.18.4", "4.0.1")),
        [new ModuleRunPhaseOutcome(ToolPhase.Test, ToolFailureCategory.None, null)],
        new ModuleRunTestCounts(true, 1, 1, 0, 0),
        new Dictionary<string, string>(StringComparer.Ordinal),
        "completed",
        ToolOutcome.Passed(),
        ["runId"],
        [],
        []);
}
