// <copyright file="PersistedFixtureAssetTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the deterministic P0 persisted-fixture contract assets.
/// </summary>
public sealed class PersistedFixtureAssetTests
{
    /// <summary>
    /// Verifies that the P0 fixture has a valid two-module manifest with all supported profile classes.
    /// </summary>
    [Fact]
    public void LoadP0TwoModuleManifestReturnsValidContract()
    {
        ManifestLoadResult result = ModuleManifestLoader.Load(FixturePath(Path.Combine("positive", "hexalith.module-manifest.v1.json")));

        result.IsValid.ShouldBeTrue();
        ModuleManifest manifest = result.Manifest.ShouldNotBeNull();
        manifest.Modules.Count.ShouldBe(2);
        manifest.Modules[1].Dependencies.ShouldBe(["p0-fixture-orders"]);
        manifest.Profiles["full"].Classes.Count.ShouldBe(ModuleProfileClasses.All.Count);
    }

    /// <summary>
    /// Verifies that each manifest negative control fails with its stable rule identifier.
    /// </summary>
    /// <param name="fileName">The negative manifest file name.</param>
    [Theory]
    [InlineData("unknown-schema.json")]
    [InlineData("unknown-field.json")]
    [InlineData("duplicate-id.json")]
    [InlineData("absolute-path.json")]
    [InlineData("path-escape.json")]
    [InlineData("missing-descriptor.json")]
    [InlineData("placeholder.json")]
    [InlineData("secret-bearing.json")]
    [InlineData("malformed-dependency.json")]
    [InlineData("invalid-profile.json")]
    [InlineData("duplicate-json-key.json")]
    [InlineData("unsupported-profile-class.json")]
    [InlineData("missing-required-value.json")]
    [InlineData("tampered-platform-pin.json")]
    public void LoadManifestNegativeControlReturnsStableRule(string fileName)
    {
        ManifestLoadResult result = ModuleManifestLoader.Load(FixturePath(Path.Combine("negative", fileName)));
        string expectedPath = FixturePath(Path.Combine("negative", $"{Path.GetFileNameWithoutExtension(fileName)}.expected.json"));
        using JsonDocument expected = JsonDocument.Parse(File.ReadAllText(expectedPath));
        int expectedExitCode = expected.RootElement.GetProperty("exitCode").GetInt32();
        string expectedRuleId = expected.RootElement.GetProperty("ruleId").GetString() ?? string.Empty;

        result.IsValid.ShouldBeFalse();
        expectedExitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
        expectedRuleId.ShouldNotBeNullOrWhiteSpace();
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain(expectedRuleId);

        string diagnosticText = string.Join('|', result.Diagnostics.Select(diagnostic => diagnostic.Message));
        diagnosticText.ShouldNotContain("fixture-redaction-control");
    }

    /// <summary>
    /// Verifies that static assets explicitly remain non-passing live-runner contracts.
    /// </summary>
    [Fact]
    public void PersistedFixtureDeclaresNonPassingLiveControls()
    {
        string fixturePath = FixturePath(Path.Combine("profiles", "p0-two-module-full.fixture.json"));
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(fixturePath));

        JsonElement root = document.RootElement;
        root.GetProperty("executionStatus").GetString().ShouldBe("requires-supported-platform");
        root.GetProperty("staticAssetLimitation").GetString().ShouldNotBeNullOrWhiteSpace();
        root.GetProperty("runIdentity").GetProperty("scope").GetString().ShouldBe("unique-per-invocation");
        root.GetProperty("requiredLiveAssertions").GetArrayLength().ShouldBe(8);
        root.GetProperty("negativeControls").GetArrayLength().ShouldBe(7);

        foreach (JsonElement control in root.GetProperty("negativeControls").EnumerateArray())
        {
            string relativeControlPath = control.GetString() ?? string.Empty;
            relativeControlPath.ShouldNotBeNullOrWhiteSpace();
            string controlPath = FixturePath(relativeControlPath);
            File.Exists(controlPath).ShouldBeTrue();

            using JsonDocument controlDocument = JsonDocument.Parse(File.ReadAllText(controlPath));
            controlDocument.RootElement.GetProperty("requiresLiveRunner").GetBoolean().ShouldBeTrue();
            controlDocument.RootElement.GetProperty("expectedStatus").GetString().ShouldNotBe("passed");
        }
    }

    private static string FixturePath(string relativePath) =>
        Path.Combine(RepositoryRoot(), "test", "fixtures", "module", relativePath);

    private static string RepositoryRoot()
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

        throw new DirectoryNotFoundException("Could not locate the Hexalith.Builds repository root.");
    }
}