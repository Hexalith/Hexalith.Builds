// <copyright file="ModuleRunEvidenceSerializationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies canonical metadata-only module-run evidence serialization.
/// </summary>
public sealed class ModuleRunEvidenceSerializationTests
{
    private static readonly string[] _knownDirtyMarkers = ["clean", "dirty"];

    /// <summary>
    /// Verifies canonical JSON orders unordered metadata deterministically, pins the top-level
    /// property sequence, and terminates the UTF-8 artifact with one newline.
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
                [
                    new ModuleRunModule("orders", "orders", "orders", "orders", "assemblies/orders.dll"),
                    new ModuleRunModule("accounts", "accounts", "accounts", "accounts", "assemblies/accounts.dll"),
                ],
                new PlatformPins("3.70.1", "1.18.0", "1.18.4", "4.0.1")),
            [new ModuleRunPhaseOutcome(ToolPhase.Cleanup, ToolFailureCategory.None, "HXI001")],
            new ModuleRunTestCounts(false, 0, 0, 0, 0),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["z-artifact.json"] = "Z_HASH",
                ["a-artifact.json"] = "A_HASH",
            },
            "completed",
            ToolOutcome.Passed(),
            ["timestamps.startedUtc", "runId", "timestamps.completedUtc"],
            ["persisted-order-created"],
            ["orders:0..1"]);

        byte[] serialized = ModuleRunEvidenceWriter.SerializeCanonical(evidence);
        string text = Encoding.UTF8.GetString(serialized);

        serialized[^1].ShouldBe((byte)'\n');
        text.IndexOf("a-artifact.json", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("z-artifact.json", StringComparison.Ordinal));

        // Topology modules must be ordered by id for deterministic comparison.
        text.IndexOf("\"id\":\"accounts\"", StringComparison.Ordinal).ShouldBeLessThan(
            text.IndexOf("\"id\":\"orders\"", StringComparison.Ordinal));

        // The top-level property sequence is part of the canonical contract.
        text.ShouldStartWith("{\"schema\":\"hexalith.module-run-evidence.v1\",\"runId\":");
        text.ShouldContain("\"testCounts\":{\"reported\":false,\"total\":0,\"passed\":0,\"failed\":0,\"skipped\":0},\"persistedAssertions\":[\"persisted-order-created\"],\"expectedSequences\":[\"orders:0..1\"],\"artifactHashes\":");
        text.ShouldContain("\"volatileFields\":[\"runId\",\"timestamps.completedUtc\",\"timestamps.startedUtc\"]");
    }

    /// <summary>
    /// Verifies source evidence resolves the current submodule revision, dirty marker, and selected SDK.
    /// </summary>
    [Fact]
    public void CreateEvidenceCapturesSourceControlAndSdkMetadata()
    {
        string repositoryRoot = FindRepositoryRoot();
        ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
            ModuleInvocationCommand.Down,
            Path.Combine(repositoryRoot, "test", "fixtures", "module", "positive", "hexalith.module-manifest.v1.json"),
            null,
            null,
            null,
            new ToolCommandResult("completed", ToolOutcome.Passed(), []),
            new DateTimeOffset(2026, 7, 17, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 17, 10, 1, 0, TimeSpan.Zero),
            "0123456789abcdef0123456789abcdef");

        evidence.Environment.RepositoryRevision.ShouldNotBe("unavailable");
        evidence.Environment.RepositoryRevision.Length.ShouldBeGreaterThanOrEqualTo(40);
        _knownDirtyMarkers.ShouldContain(evidence.Environment.RepositoryDirtyMarker);
        evidence.Environment.SdkVersion.ShouldBe("10.0.302");
    }

    /// <summary>
    /// Verifies evidence creation revalidates a fixture path after manifest loading and rejects a later symlink escape.
    /// </summary>
    [Fact]
    public void CreateEvidenceRejectsFixtureSymlinkRetargetedAfterValidation()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hexalith-evidence-fixture-{Guid.NewGuid():N}");
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"hexalith-evidence-external-{Guid.NewGuid():N}");
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(directory, "assemblies"));
            _ = Directory.CreateDirectory(Path.Combine(directory, "fixtures"));
            _ = Directory.CreateDirectory(externalDirectory);
            File.WriteAllText(Path.Combine(directory, "assemblies", "module.dll"), string.Empty);
            string internalFixture = Path.Combine(directory, "fixtures", "internal.json");
            string externalFixture = Path.Combine(externalDirectory, "external.json");
            string fixtureLink = Path.Combine(directory, "fixtures", "current.json");
            File.WriteAllText(internalFixture, "{}");
            File.WriteAllText(externalFixture, "{}");
            _ = File.CreateSymbolicLink(fixtureLink, internalFixture);
            string manifestPath = Path.Combine(directory, "manifest.json");
            const string manifestJson =
                """
                {
                  "schema": "hexalith.module-manifest.v1",
                  "id": "fixture-swap",
                  "modules": [{
                    "id": "module-a",
                    "descriptorAssembly": "assemblies/module.dll",
                    "dependencies": [],
                    "domain": "module-a",
                    "applicationId": "module-a",
                    "resourceId": "module-a"
                  }],
                  "platform": {
                    "eventStoreVersion": "3.70.1",
                    "daprRuntimeVersion": "1.18.0",
                    "daprSdkVersion": "1.18.4",
                    "frontComposerVersion": "4.0.1"
                  },
                  "profiles": {
                    "full": {
                      "fixture": "fixtures/current.json",
                      "classes": ["pure-domain"]
                    }
                  }
                }
                """;
            File.WriteAllText(manifestPath, manifestJson);
            ManifestLoadResult manifestResult = ModuleManifestLoader.Load(manifestPath);
            manifestResult.IsValid.ShouldBeTrue();

            File.Delete(fixtureLink);
            _ = File.CreateSymbolicLink(fixtureLink, externalFixture);
            ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
                ModuleInvocationCommand.Test,
                manifestPath,
                manifestResult.Manifest,
                "full",
                null,
                new ToolCommandResult("unavailable", ToolOutcome.Passed(), []),
                new DateTimeOffset(2026, 7, 21, 10, 0, 0, TimeSpan.Zero),
                new DateTimeOffset(2026, 7, 21, 10, 1, 0, TimeSpan.Zero),
                "0123456789abcdef0123456789abcdef");

            evidence.Invocation.FixturePath.ShouldBeNull();
            evidence.Invocation.FixtureHash.ShouldBeNull();
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(externalDirectory, true);
        }
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

        throw new DirectoryNotFoundException("The Builds repository root was not found.");
    }
}