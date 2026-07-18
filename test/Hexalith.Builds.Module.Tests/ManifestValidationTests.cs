// <copyright file="ManifestValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies strict module-manifest loading and validation.
/// </summary>
public sealed class ManifestValidationTests
{
    /// <summary>
    /// Verifies a deterministic two-module manifest is accepted.
    /// </summary>
    [Fact]
    public void LoadValidTwoModuleManifestReturnsManifest()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            string path = WriteManifest(directory, ValidManifestJson());

            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeTrue();
            _ = result.Manifest.ShouldNotBeNull();
            result.Manifest.Modules.Count.ShouldBe(2);
            result.Manifest.Profiles["full"].Classes.Count.ShouldBe(8);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies fail-closed schema, field, identity, path, placeholder, and secret controls.
    /// </summary>
    /// <param name="original">The source fragment to replace.</param>
    /// <param name="replacement">The manifest mutation.</param>
    /// <param name="ruleId">The expected stable rule identifier.</param>
    [Theory]
    [InlineData("\"hexalith.module-manifest.v1\"", "\"hexalith.module-manifest.v2\"", "HXM001")]
    [InlineData("\"id\": \"p0-two-module\"", "\"id\": \"p0-two-module\", \"unknown\": true", "HXM002")]
    [InlineData("\"id\": \"module-b\"", "\"id\": \"module-a\"", "HXM003")]
    [InlineData("assemblies/module-a.dll", "../module-a.dll", "HXM004")]
    [InlineData("assemblies/module-a.dll", "assemblies/missing.dll", "HXM005")]
    [InlineData("assemblies/module-a.dll", "assemblies/bearer token=SUPERSECRET_8472.dll", "HXM007")]
    [InlineData("module-a", "${MODULE_ID}", "HXM006")]
    [InlineData("\"domain\": \"module-a\"", "\"domain\": \"module-a\", \"token\": \"SUPERSECRET_8472\"", "HXM002")]
    [InlineData("\"domain\": \"module-a\"", "\"domain\": \"bearer SUPERSECRET_8472\"", "HXM007")]
    public void LoadInvalidManifestReturnsStableRule(
        string original,
        string replacement,
        string ruleId)
    {
        string directory = CreateFixtureDirectory();

        try
        {
            string json = ValidManifestJson().Replace(original, replacement, StringComparison.Ordinal);
            string path = WriteManifest(directory, json);

            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain(ruleId);
            string.Join('|', result.Diagnostics.Select(diagnostic => diagnostic.Message))
                .ShouldNotContain("SUPERSECRET_8472");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies duplicate JSON keys are rejected before model binding.
    /// </summary>
    [Fact]
    public void LoadManifestWithDuplicateJsonKeyReturnsStableRule()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            string json = ValidManifestJson().Replace(
                "\"schema\": \"hexalith.module-manifest.v1\"",
                "\"schema\": \"hexalith.module-manifest.v1\", \"schema\": \"hexalith.module-manifest.v1\"",
                StringComparison.Ordinal);
            string path = WriteManifest(directory, json);

            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXM012");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies a repository-relative descriptor symlink cannot escape the manifest repository.
    /// </summary>
    [Fact]
    public void LoadManifestWithEscapingDescriptorSymlinkFailsClosed()
    {
        string directory = CreateFixtureDirectory();
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-external-{Guid.NewGuid():N}");

        try
        {
            _ = Directory.CreateDirectory(externalDirectory);
            File.WriteAllText(Path.Combine(externalDirectory, "module-a.dll"), string.Empty);
            File.WriteAllText(Path.Combine(externalDirectory, "module-b.dll"), string.Empty);
            File.WriteAllText(Path.Combine(externalDirectory, "ui.dll"), string.Empty);
            Directory.Delete(Path.Combine(directory, "assemblies"), recursive: true);
            _ = Directory.CreateSymbolicLink(Path.Combine(directory, "assemblies"), externalDirectory);
            string path = WriteManifest(directory, ValidManifestJson());

            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXM004");
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(externalDirectory, true);
        }
    }

    private static string CreateFixtureDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-manifest-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(Path.Combine(directory, "assemblies"));
        _ = Directory.CreateDirectory(Path.Combine(directory, "fixtures"));
        File.WriteAllText(Path.Combine(directory, "assemblies", "module-a.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "assemblies", "module-b.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "assemblies", "ui.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "fixtures", "full.json"), "{}");
        return directory;
    }

    private static string ValidManifestJson() =>
        """
        {
          "schema": "hexalith.module-manifest.v1",
          "id": "p0-two-module",
          "modules": [
            {
              "id": "module-a",
              "descriptorAssembly": "assemblies/module-a.dll",
              "dependencies": [],
              "domain": "module-a",
              "applicationId": "module-a",
              "resourceId": "module-a"
            },
            {
              "id": "module-b",
              "descriptorAssembly": "assemblies/module-b.dll",
              "dependencies": ["module-a"],
              "domain": "module-b",
              "applicationId": "module-b",
              "resourceId": "module-b"
            }
          ],
          "platform": {
            "eventStoreVersion": "3.70.0",
            "daprRuntimeVersion": "1.18.0",
            "daprSdkVersion": "1.18.4",
            "frontComposerVersion": "4.0.1"
          },
          "ui": {
            "descriptorAssembly": "assemblies/ui.dll"
          },
          "profiles": {
            "full": {
              "fixture": "fixtures/full.json",
              "classes": [
                "pure-domain",
                "host-contract",
                "persisted-boundary",
                "restart",
                "two-instance",
                "authenticated-browser",
                "authenticated-cli",
                "authenticated-mcp"
              ]
            }
          }
        }
        """;

    private static string WriteManifest(string directory, string json)
    {
        string path = Path.Combine(directory, "hexalith.module.json");
        File.WriteAllText(path, json);
        return path;
    }
}