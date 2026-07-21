// <copyright file="ManifestValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies strict module-manifest loading and validation.
/// </summary>
public sealed class ManifestValidationTests
{
    private static readonly string[] _pureDomainClasses = ["pure-domain"];

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

    /// <summary>
    /// Verifies the schema, runtime identifier validator, and profile-key validator share one contract.
    /// </summary>
    [Fact]
    public void IdentifierSchemaAndRuntimeValidationRemainInParity()
    {
        string repositoryRoot = FindRepositoryRoot();
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(repositoryRoot, "schemas", "hexalith.module-manifest.v1.json")));
        JsonElement identifier = schema.RootElement.GetProperty("$defs").GetProperty("identifier");
        string pattern = identifier.GetProperty("pattern").GetString() ?? string.Empty;
        int maximumLength = identifier.GetProperty("maxLength").GetInt32();
        schema.RootElement.GetProperty("properties").GetProperty("profiles")
            .GetProperty("propertyNames").GetProperty("$ref").GetString()
            .ShouldBe("#/$defs/identifier");

        (string Value, bool IsValid)[] cases =
        [
            ("module", true),
            ("module-1", true),
            ($"a{new string('b', 62)}", true),
            ("1-module", false),
            ("module-", false),
            ("module--part", false),
            ($"a{new string('b', 63)}", false),
            ("Module", false),
        ];

        foreach ((string value, bool expected) in cases)
        {
            bool schemaAccepted = value.Length <= maximumLength && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant);
            schemaAccepted.ShouldBe(expected, $"schema acceptance for '{value}'");

            string directory = CreateFixtureDirectory();
            try
            {
                string rootIdJson = ValidManifestJson().Replace("\"id\": \"p0-two-module\"", $"\"id\": \"{value}\"", StringComparison.Ordinal);
                ModuleManifestLoader.Load(WriteManifest(directory, rootIdJson)).IsValid.ShouldBe(expected, $"root id acceptance for '{value}'");

                string profileJson = ValidManifestJson().Replace("\"full\": {", $"\"{value}\": {{", StringComparison.Ordinal);
                ModuleManifestLoader.Load(WriteManifest(directory, profileJson)).IsValid.ShouldBe(expected, $"profile id acceptance for '{value}'");
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }

    /// <summary>
    /// Verifies portable canonical path syntax remains aligned between schema and runtime validation.
    /// </summary>
    [Fact]
    public void RepositoryPathSchemaAndRuntimeValidationRemainInParity()
    {
        using JsonDocument schema = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "schemas", "hexalith.module-manifest.v1.json")));
        string pattern = schema.RootElement.GetProperty("$defs").GetProperty("repositoryPath")
            .GetProperty("pattern").GetString() ?? string.Empty;
        (string Value, bool IsValid)[] cases =
        [
            ("fixtures/full.json", true),
            ("coverage-100%.json", true),
            ("C:/outside/descriptor.json", false),
            ("//server/share/descriptor.json", false),
            ("fixtures//full.json", false),
            ("fixtures/", false),
            ("../outside.json", false),
            ("fixtures\\full.json", false),
        ];

        string directory = CreateFixtureDirectory();
        try
        {
            foreach ((string value, bool expected) in cases)
            {
                Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant).ShouldBe(expected, $"schema acceptance for '{value}'");
                List<ToolDiagnostic> diagnostics = [];
                _ = ManifestPathValidator.ValidateExistingFile(value, directory, "fixture", diagnostics);
                bool runtimeAcceptedSyntax = diagnostics.All(diagnostic => diagnostic.RuleId != "HXM004");
                runtimeAcceptedSyntax.ShouldBe(expected, $"runtime syntax acceptance for '{value}'");
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies placeholder detection recognizes supported forms without rejecting ordinary percent characters.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <param name="expected">The expected placeholder decision.</param>
    [Theory]
    [InlineData("coverage-100%.json", false)]
    [InlineData("fixtures/%PROFILE%/data.json", true)]
    [InlineData("fixtures/$PROFILE/data.json", true)]
    [InlineData("fixtures/${PROFILE}/data.json", true)]
    [InlineData("fixtures/{{PROFILE}}/data.json", true)]
    public void PlaceholderDetectionUsesPreciseSupportedForms(string value, bool expected) =>
        ManifestPathValidator.ContainsPlaceholder(value).ShouldBe(expected);

    /// <summary>
    /// Verifies ordinary deterministic metadata is not classified as credential material.
    /// </summary>
    /// <param name="value">The safe value.</param>
    [Theory]
    [InlineData("honeyjar")]
    [InlineData("api-key-management")]
    [InlineData("assemblies/monkeyjungle.dll")]
    public void SecretDetectionAllowsOrdinaryMetadata(string value) =>
        ManifestSecretDetector.ContainsSecret(value).ShouldBeFalse();

    /// <summary>
    /// Verifies representative authorization, token, connection-string, and private-key shapes fail closed.
    /// </summary>
    /// <param name="value">The credential-bearing value.</param>
    [Theory]
    [InlineData("bearer abcdefghijklmnop")]
    [InlineData("Authorization=Basic dXNlcjpwYXNz")]
    [InlineData("fixtures/data.json?sig=abcdef0123456789")]
    [InlineData("AccountKey=abcdef0123456789")]
    [InlineData("aws_access_key_id=AKIAIOSFODNN7EXAMPLE")]
    [InlineData("AKIAIOSFODNN7EXAMPLE")]
    [InlineData("ghp_abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("-----BEGIN PRIVATE KEY-----")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.signature123")]
    public void SecretDetectionRejectsCredentialShapes(string value) =>
        ManifestSecretDetector.ContainsSecret(value).ShouldBeTrue();

    /// <summary>
    /// Verifies invalid identifier values never become retained diagnostic field identities.
    /// </summary>
    [Fact]
    public void SecretBearingIdentifierIsNotRetainedInDiagnostics()
    {
        const string secret = "password=SUPERSECRET_8472";
        string directory = CreateFixtureDirectory();
        try
        {
            string json = ValidManifestJson()
                .Replace("\"id\": \"module-a\"", $"\"id\": \"{secret}\"", StringComparison.Ordinal)
                .Replace("\"domain\": \"module-a\"", "\"domain\": \"\"", StringComparison.Ordinal);
            ManifestLoadResult result = ModuleManifestLoader.Load(WriteManifest(directory, json));

            result.IsValid.ShouldBeFalse();
            JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain(secret);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies callers cannot cast the supported profile-class contract to a mutable set.
    /// </summary>
    [Fact]
    public void SupportedProfileClassesAreImmutable()
    {
        ISet<string> set = ModuleProfileClasses.All.ShouldBeAssignableTo<ISet<string>>();

        _ = Should.Throw<NotSupportedException>(() => set.Add("unsupported-profile"));
        ModuleProfileClasses.All.ShouldNotContain("unsupported-profile");
    }

    /// <summary>
    /// Verifies duplicate properties are rejected inside nested objects and array elements.
    /// </summary>
    /// <param name="original">The nested source fragment.</param>
    /// <param name="replacement">The duplicate-property mutation.</param>
    [Theory]
    [InlineData("\"domain\": \"module-a\"", "\"domain\": \"module-a\", \"domain\": \"module-a\"")]
    [InlineData("\"fixture\": \"fixtures/full.json\"", "\"fixture\": \"fixtures/full.json\", \"fixture\": \"fixtures/full.json\"")]
    public void LoadManifestWithNestedDuplicateJsonKeyReturnsStableRule(string original, string replacement)
    {
        string directory = CreateFixtureDirectory();
        try
        {
            string path = WriteManifest(directory, ValidManifestJson().Replace(original, replacement, StringComparison.Ordinal));
            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXM012");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies self-dependencies, duplicate edges, and multi-node cycles fail closed.
    /// </summary>
    /// <param name="original">The dependency fragment to replace.</param>
    /// <param name="replacement">The invalid dependency graph fragment.</param>
    [Theory]
    [InlineData("\"dependencies\": []", "\"dependencies\": [\"module-a\"]")]
    [InlineData("\"dependencies\": [\"module-a\"]", "\"dependencies\": [\"module-a\", \"module-a\"]")]
    [InlineData("\"dependencies\": []", "\"dependencies\": [\"module-b\"]")]
    public void LoadManifestWithInvalidDependencyGraphReturnsStableRule(string original, string replacement)
    {
        string directory = CreateFixtureDirectory();
        try
        {
            string path = WriteManifest(directory, ValidManifestJson().Replace(original, replacement, StringComparison.Ordinal));
            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXM008");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies manifest diagnostics retain their deterministic complete ordering.
    /// </summary>
    [Fact]
    public void LoadManifestWithMultipleErrorsReturnsDeterministicDiagnosticOrder()
    {
        string directory = CreateFixtureDirectory();
        try
        {
            string json = ValidManifestJson()
                .Replace("hexalith.module-manifest.v1", "hexalith.module-manifest.v2", StringComparison.Ordinal)
                .Replace("\"id\": \"p0-two-module\"", "\"id\": \"Invalid\"", StringComparison.Ordinal)
                .Replace("assemblies/module-a.dll", "assemblies/missing.dll", StringComparison.Ordinal);
            ManifestLoadResult result = ModuleManifestLoader.Load(WriteManifest(directory, json));

            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXM001", "HXM005", "HXM010"]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies files that cannot be opened for reading fail manifest validation.
    /// </summary>
    [Fact]
    public void LoadManifestWithUnreadableDescriptorReturnsStableRule()
    {
        string directory = CreateFixtureDirectory();
        try
        {
            using FileStream lockedDescriptor = File.Open(
                Path.Combine(directory, "assemblies", "module-a.dll"),
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None);
            ManifestLoadResult result = ModuleManifestLoader.Load(WriteManifest(directory, ValidManifestJson()));

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXM005");
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies oversized manifests fail before unbounded content allocation or JSON parsing.
    /// </summary>
    [Fact]
    public void LoadOversizedManifestReturnsStableLimitRule()
    {
        string directory = CreateFixtureDirectory();
        try
        {
            string path = WriteManifest(directory, new string(' ', 1_048_577));
            ManifestLoadResult result = ModuleManifestLoader.Load(path);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXM013"]);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies a long acyclic dependency chain is validated without recursive stack growth.
    /// </summary>
    [Fact]
    public void LoadLongAcyclicDependencyChainReturnsValidManifest()
    {
        const int moduleCount = 2_048;
        string directory = CreateFixtureDirectory();
        try
        {
            object[] modules = [.. Enumerable.Range(0, moduleCount)
                .Select(index => (object)new
                {
                    id = $"module-{index}",
                    descriptorAssembly = "assemblies/module-a.dll",
                    dependencies = index == 0 ? Array.Empty<string>() : [$"module-{index - 1}"],
                    domain = $"domain-{index}",
                    applicationId = $"application-{index}",
                    resourceId = $"resource-{index}",
                })];
            string json = JsonSerializer.Serialize(new
            {
                schema = "hexalith.module-manifest.v1",
                id = "long-chain",
                modules,
                platform = new
                {
                    eventStoreVersion = "3.70.1",
                    daprRuntimeVersion = "1.18.0",
                    daprSdkVersion = "1.18.4",
                    frontComposerVersion = "4.0.1",
                },
                profiles = new
                {
                    full = new
                    {
                        fixture = "fixtures/full.json",
                        classes = _pureDomainClasses,
                    },
                },
            });

            ModuleManifestLoader.Load(WriteManifest(directory, json)).IsValid.ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(directory, true);
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

        throw new DirectoryNotFoundException("Could not locate the Hexalith.Builds repository root.");
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
            "eventStoreVersion": "3.70.1",
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