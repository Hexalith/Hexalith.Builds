// <copyright file="PersistedProfileNativeTestsTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies native test declarations in persisted profiles and their binding into module-run evidence.
/// </summary>
public sealed class PersistedProfileNativeTestsTests
{
    /// <summary>Checks the executable fixture declares one native platform per full profile.</summary>
    [Fact]
    public void FullProfilesDeclareVsTestAndMicrosoftTestingPlatform()
    {
        string manifestPath = ManifestPath();
        ModuleManifest manifest = ModuleManifestLoader.Load(manifestPath).Manifest.ShouldNotBeNull();

        PersistedProfileNativeTests vstest = PersistedProfileLoader.TryLoad(manifest, manifestPath, "full", null).ShouldNotBeNull().NativeTests.ShouldNotBeNull();
        PersistedProfileNativeTests mtp = PersistedProfileLoader.TryLoad(manifest, manifestPath, "full-mtp", null).ShouldNotBeNull().NativeTests.ShouldNotBeNull();

        vstest.Platform.ShouldBe("vstest");
        vstest.Project.ShouldBe("test/fixtures/module/executable/P0Fixture.NativeTests.VsTest/P0Fixture.NativeTests.VsTest.csproj");
        mtp.Platform.ShouldBe("mtp");
        mtp.Project.ShouldBe("test/fixtures/module/executable/P0Fixture.NativeTests/P0Fixture.NativeTests.csproj");
    }

    /// <summary>Checks unsupported platforms, non-project files, and escaping paths make the profile unsupported.</summary>
    /// <param name="nativeTests">The seeded native test declaration.</param>
    [Theory]
    [InlineData("{\"project\":\"tests/Native.csproj\",\"platform\":\"nunit\"}")]
    [InlineData("{\"project\":\"tests/Native.cs\",\"platform\":\"vstest\"}")]
    [InlineData("{\"project\":\"../outside/Native.csproj\",\"platform\":\"mtp\"}")]
    [InlineData("{\"project\":\"tests/Missing.csproj\",\"platform\":\"mtp\"}")]
    [InlineData("{\"project\":\"tests/Native.csproj\",\"platform\":\"mtp\",\"extra\":true}")]
    public void MalformedNativeTestsMakeTheProfileUnsupported(string nativeTests) =>
        LoadSeededProfile(nativeTests).ShouldBeNull();

    /// <summary>Checks the seeded repository accepts a valid declaration, so the malformed cases fail for their own reason.</summary>
    [Fact]
    public void ValidNativeTestsLoadInTheSeededRepository() =>
        LoadSeededProfile("{\"project\":\"tests/Native.csproj\",\"platform\":\"mtp\"}").ShouldNotBeNull()
            .NativeTests.ShouldNotBeNull().Platform.ShouldBe("mtp");

    /// <summary>Checks the VSTest fixture's directory-local SDK pin follows the repository pin.</summary>
    [Fact]
    public void VsTestFixtureSdkPinMatchesRepository()
    {
        using JsonDocument repository = JsonDocument.Parse(File.ReadAllText(Path.Combine(CompositionTestFiles.RepositoryRoot(), "global.json")));
        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            Path.GetDirectoryName(ManifestPath())!,
            "P0Fixture.NativeTests.VsTest",
            "global.json")));

        fixture.RootElement.GetProperty("sdk").GetProperty("version").GetString()
            .ShouldBe(repository.RootElement.GetProperty("sdk").GetProperty("version").GetString());
    }

    /// <summary>Checks native counts and report hashes survive canonical evidence validation.</summary>
    [Fact]
    public void NativeReportBindsCountsAndHashIntoValidEvidence()
    {
        string manifestPath = ManifestPath();
        ModuleManifest manifest = ModuleManifestLoader.Load(manifestPath).Manifest.ShouldNotBeNull();
        string reportHash = new('A', 64);

        ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
            ModuleInvocationCommand.Test,
            manifestPath,
            manifest,
            "full",
            null,
            new ToolCommandResult("completed", ToolOutcome.Passed(), []),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            CompositionTestFiles.RunId,
            testCounts: new ModuleRunTestCounts(true, 2, 2, 0, 0),
            artifactHashes: new Dictionary<string, string>(StringComparer.Ordinal) { ["evidence/full.vstest.trx"] = reportHash });
        byte[] bytes = ModuleRunEvidenceWriter.SerializeCanonical(evidence);

        ModuleRunEvidenceArtifactValidator.TryValidate(bytes, out ModuleRunEvidenceArtifactSummary? summary).ShouldBeTrue();
        summary.ShouldNotBeNull().TestsReported.ShouldBeTrue();
        summary.TestsPassed.ShouldBe(2);
        using JsonDocument document = JsonDocument.Parse(bytes);
        document.RootElement.GetProperty("artifactHashes").GetProperty("evidence/full.vstest.trx").GetString().ShouldBe(reportHash);
    }

    /// <summary>Checks retained reports obey the same containment rules as evidence and require the TRX extension.</summary>
    /// <param name="artifactPath">The requested artifact path.</param>
    /// <returns>A task that completes after the rejected write.</returns>
    [Theory]
    [InlineData("/absolute/native.trx")]
    [InlineData("../escape/native.trx")]
    [InlineData("evidence/native.json")]
    [InlineData("evidence\\native.trx")]
    public async Task ReportArtifactPathIsContainedAsync(string artifactPath)
    {
        ModuleRunEvidenceWriteResult result = await ModuleRunEvidenceWriter.WriteArtifactAsync(
            artifactPath,
            ManifestPath(),
            [1, 2, 3],
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Succeeded.ShouldBeFalse();
    }

    /// <summary>Checks a contained report is written with its exact bytes.</summary>
    /// <returns>A task that completes after the write.</returns>
    [Fact]
    public async Task ReportArtifactIsWrittenWithExactBytesAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string repository = Path.Combine(root, "repo");
            _ = Directory.CreateDirectory(repository);
            await File.WriteAllTextAsync(Path.Combine(repository, "Hexalith.Builds.slnx"), "<Solution />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            byte[] report = [60, 84, 101, 115, 116, 82, 117, 110, 32, 47, 62];

            ModuleRunEvidenceWriteResult result = await ModuleRunEvidenceWriter.WriteArtifactAsync(
                "evidence/full.vstest.trx",
                Path.Combine(repository, "hexalith.module-manifest.v1.json"),
                report,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.Succeeded.ShouldBeTrue();
            (await File.ReadAllBytesAsync(Path.Combine(repository, "evidence", "full.vstest.trx"), TestContext.Current.CancellationToken).ConfigureAwait(true))
                .ShouldBe(report);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static PersistedProfileDefinition? LoadSeededProfile(string nativeTests)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string repository = Path.Combine(root, "repo");
            _ = Directory.CreateDirectory(Path.Combine(repository, "tests"));
            File.WriteAllText(Path.Combine(repository, "Hexalith.Builds.slnx"), "<Solution />");
            File.WriteAllText(Path.Combine(repository, "tests", "Native.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(repository, "tests", "Native.cs"), "// fixture");
            string manifestPath = ManifestPath();
            ModuleManifest manifest = ModuleManifestLoader.Load(manifestPath).Manifest.ShouldNotBeNull();
            string fixture = File.ReadAllText(Path.Combine(Path.GetDirectoryName(manifestPath)!, "profiles", "p0-two-module-full.fixture.json"));
            using JsonDocument document = JsonDocument.Parse(fixture);
            Dictionary<string, object?> seeded = new(StringComparer.Ordinal)
            {
                ["schema"] = document.RootElement.GetProperty("schema").GetString(),
                ["id"] = "seeded",
                ["modules"] = document.RootElement.GetProperty("modules"),
                ["nativeTests"] = JsonDocument.Parse(nativeTests).RootElement,
            };
            File.WriteAllText(Path.Combine(repository, "profile.json"), JsonSerializer.Serialize(seeded));
            string seededManifestPath = Path.Combine(repository, "hexalith.module-manifest.v1.json");
            ModuleManifest seededManifest = manifest with
            {
                Profiles = new Dictionary<string, ModuleProfile>(StringComparer.Ordinal)
                {
                    ["full"] = new ModuleProfile("profile.json", ["persisted-boundary", "restart", "two-instance"]),
                },
            };

            return PersistedProfileLoader.TryLoad(seededManifest, seededManifestPath, "full", null);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string ManifestPath() =>
        Path.Combine(CompositionTestFiles.RepositoryRoot(), "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
}