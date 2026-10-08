// <copyright file="PlatformManifestValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

using Json.Schema;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the complete local enrollment schema, atomic results, safeguards and nested diagnostics.
/// </summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class PlatformManifestValidationTests
{
    /// <summary>Verifies a complete declaration passes both validators and materializes effective defaults.</summary>
    [Fact]
    public void CompleteFixturePassesSchemaAndEnrollmentWithDefaults()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string manifest = workspace.Save();
        using JsonDocument input = JsonDocument.Parse(File.ReadAllText(manifest));
        PublishedSchema().Evaluate(input.RootElement).IsValid.ShouldBeTrue();
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        result.Diagnostics.ShouldBeEmpty();
        JsonElement module = result.Declarations.ShouldNotBeNull().Single().Declaration;
        module.GetProperty("runtime").GetProperty("resources")[0].GetProperty("replicas").GetInt32().ShouldBe(1);
        module.GetProperty("lifecycle").GetProperty("startup").GetProperty("defaultTimeoutSeconds").GetInt32().ShouldBe(600);
        SupportedPlatformManifestSchemas.Eligible.ShouldBe(["hexalith.module-manifest.v2"]);
    }

    /// <summary>Verifies nested schema errors carry complete paths and match the published contract.</summary>
    /// <param name="path">The mutated declaration path.</param>
    /// <param name="json">The replacement JSON or missing sentinel.</param>
    /// <param name="field">The expected complete diagnostic path.</param>
    [Theory]
    [InlineData("modules.0.identity.moduleId", "7", "modules[0].identity.moduleId")]
    [InlineData("modules.0.identity.moduleId", "\"sample\\n\"", "modules[0].identity.moduleId")]
    [InlineData("modules.0.runtime.dapr.0.configurationKey", "\"Dapr:EventsComponent\\n\"", "modules[0].runtime.dapr[0].configurationKey")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"/sample\\n\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"/sample\\u0000route\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"/sample\\u0085route\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/data\\n\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/data\\u0000path\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/data\\u0085path\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.runtime.dapr.0.role", "<missing>", "modules[0].runtime.dapr[0].role")]
    [InlineData("modules.0.runtime.dapr.0.configurationKey", "<missing>", "modules[0].runtime.dapr[0].configurationKey")]
    [InlineData("modules.0.runtime.dapr.0.componentName", "\"literal-store\"", "modules[0].runtime.dapr[0].componentName")]
    [InlineData("modules.0.runtime.dapr.0.recoveryClass", "\"backup\"", "modules[0].runtime.dapr[0].recoveryClass")]
    [InlineData("modules.0.lifecycle.tasks.0.scope", "\"always\"", "modules[0].lifecycle.tasks[0].scope")]
    [InlineData("modules.0.lifecycle.tasks.0.authorityClass", "<missing>", "modules[0].lifecycle.tasks[0].authorityClass")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "<missing>", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"/health\\n\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"/health\\nready\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"/health\\tready\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"/health\\u0000ready\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"/health\\u0085ready\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":0,\"justification\":\"needs more time\"}", "modules[0].lifecycle.startup.override.timeoutSeconds")]
    [InlineData("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":700,\"justification\":\"  \"}", "modules[0].lifecycle.startup.override.justification")]
    [InlineData("modules.0.runtime.resources.0.replicas", "1.5", "modules[0].runtime.resources[0].replicas")]
    [InlineData("modules.0.integration.workers.0.disableControl", "null", "modules[0].integration.workers[0].disableControl")]
    [InlineData("modules.0.integration.topics.0.deadLetter.topic", "<missing>", "modules[0].integration.topics[0].deadLetter.topic")]
    [InlineData("modules.0.surfaces.interfaces.0.Protocol", "\"http\"", "modules[0].surfaces.interfaces[0].Protocol")]
    [InlineData("modules.0.integration.secrets.0.value", "\"literal\"", "modules[0].integration.secrets[0].value")]
    [InlineData("", "\"unknown\"", "$[\"\"]")]
    [InlineData("modules.0.identity.", "\"unknown\"", "modules[0].identity[\"\"]")]
    [InlineData("modules.0.runtime.resources.0.requests.cpu", "0", "modules[0].runtime.resources[0].requests.cpu")]
    [InlineData("modules.0.runtime.resources.0.requests.cpu", "-1", "modules[0].runtime.resources[0].requests.cpu")]
    [InlineData("modules.0.runtime.resources.0.limits.cpu", "0", "modules[0].runtime.resources[0].limits.cpu")]
    [InlineData("modules.0.runtime.resources.0.limits.cpu", "-1", "modules[0].runtime.resources[0].limits.cpu")]
    [InlineData("modules.0.runtime.resources.0.requests.memoryMiB", "0", "modules[0].runtime.resources[0].requests.memoryMiB")]
    [InlineData("modules.0.runtime.resources.0.requests.memoryMiB", "-1", "modules[0].runtime.resources[0].requests.memoryMiB")]
    [InlineData("modules.0.runtime.resources.0.limits.memoryMiB", "0", "modules[0].runtime.resources[0].limits.memoryMiB")]
    [InlineData("modules.0.runtime.resources.0.limits.memoryMiB", "-1", "modules[0].runtime.resources[0].limits.memoryMiB")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.sizeMiB", "0", "modules[0].runtime.resources[0].volumes[0].sizeMiB")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.sizeMiB", "-1", "modules[0].runtime.resources[0].volumes[0].sizeMiB")]
    [InlineData("modules.0.lifecycle.readiness.0.endpoint", "\"//provider.example/health\"", "modules[0].lifecycle.readiness[0].endpoint")]
    [InlineData("modules.0.lifecycle.readiness.0.executable", "\"probe\\n\"", "modules[0].lifecycle.readiness[0].executable")]
    [InlineData("modules.0.lifecycle.readiness.0.executable", "\"probe\\u0000\"", "modules[0].lifecycle.readiness[0].executable")]
    [InlineData("modules.0.lifecycle.readiness.0.executable", "\"probe\\u0085\"", "modules[0].lifecycle.readiness[0].executable")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"/api/../admin//x\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"/./sample\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.surfaces.interfaces.0.routePrefix", "\"//sample\"", "modules[0].surfaces.interfaces[0].routePrefix")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/api/../admin//x\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/./data\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.runtime.resources.0.volumes.0.mountPath", "\"//data\"", "modules[0].runtime.resources[0].volumes[0].mountPath")]
    [InlineData("modules.0.surfaces.interfaces.0.surfaceClass", "\"gateway\"", "modules[0].surfaces.interfaces[0].surfaceClass")]
    [InlineData("modules.0.surfaces.interfaces.0.surfaceClass", "\"mcp\"", "modules[0].surfaces.interfaces[0].surfaceClass")]
    [InlineData("modules.0.lifecycle.classification.changeClass", "\"patch\"", "modules[0].lifecycle.classification.changeClass")]
    [InlineData("modules.0.lifecycle.readiness.0.service", "\"health\"", "modules[0].lifecycle.readiness[0].service")]
    [InlineData("modules.0.lifecycle.readiness.0.arguments", "[]", "modules[0].lifecycle.readiness[0].arguments")]
    [InlineData("modules.0.lifecycle.readiness.0.executable", "\"README.md\"", "modules[0].lifecycle.readiness[0].executable")]
    [InlineData("modules.0.integration.topics.0.deadLetter.strategy", "\"none\"", "modules[0].integration.topics[0].deadLetter.topic")]
    public void InvalidFieldsMatchSchemaAndReportCompletePaths(string path, string json, string field)
    {
        ArgumentNullException.ThrowIfNull(path);
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change(path, json);
        string manifest = workspace.Save();
        using JsonDocument input = JsonDocument.Parse(File.ReadAllText(manifest));
        PublishedSchema().Evaluate(input.RootElement).IsValid.ShouldBeFalse();
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == field && diagnostic.Source!.EndsWith("manifest.json", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(diagnostic.Message));
    }

    /// <summary>Verifies all supported task scopes and recovery classes can enroll without running tasks.</summary>
    /// <param name="scope">A supported lifecycle scope.</param>
    [Theory]
    [InlineData("per-start-verify")]
    [InlineData("once-per-environment-creation")]
    [InlineData("recovery")]
    [InlineData("operator-only")]
    public void SupportedLifecycleScopesPass(string scope)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.scope", JsonSerializer.Serialize(scope));
        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies all three recovery classes are admitted as logical Dapr capabilities.</summary>
    /// <param name="recoveryClass">The declared recovery class.</param>
    [Theory]
    [InlineData("authoritative-restore")]
    [InlineData("rebuild-only")]
    [InlineData("live-authority-only")]
    public void SupportedRecoveryClassesPass(string recoveryClass)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.runtime.dapr.0.recoveryClass", JsonSerializer.Serialize(recoveryClass));
        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies unused capabilities are represented by empty collections.</summary>
    [Fact]
    public void EmptyCapabilityCollectionsRemainValid()
    {
        using PlatformManifestTestWorkspace workspace = new();
        foreach (string path in new[]
        {
            "identity.servers", "runtime.dapr", "runtime.extensions", "runtime.resources", "surfaces.interfaces",
            "integration.topics", "integration.secrets", "integration.dynamicSecretNamespaces", "integration.identityNeeds",
            "integration.egress", "integration.providers", "integration.workers", "lifecycle.readiness", "lifecycle.tasks",
            "lifecycle.recoveryHooks", "lifecycle.fenceHooks", "lifecycle.criticalFlows", "lifecycle.smokeSurfaces", "lifecycle.recoveryInventory",
            "runtime.providerExceptions",
        })
        {
            workspace.Change($"modules.0.{path}", "[]");
        }

        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies distinct valid files produce the entire declaration set.</summary>
    [Fact]
    public void DistinctValidFilesReturnAllModules()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string first = workspace.Save("a.json");
        workspace.Change("modules.0.identity.moduleId", "\"second\"");
        workspace.Change("modules.0.identity.servers.0.appId", "\"second-api\"");
        workspace.Change("modules.0.identity.servers.0.resourceId", "\"second-api-host\"");
        string second = workspace.Save("b.json");
        PlatformManifestValidator.Validate([first, second], TestContext.Current.CancellationToken).Declarations.ShouldNotBeNull().Count.ShouldBe(2);
    }

    /// <summary>Verifies the file limit accepts a complete boundary set and rejects overflow atomically.</summary>
    [Fact]
    public void ManifestFileLimitAccepts256AndRejects257()
    {
        using PlatformManifestTestWorkspace workspace = new();
        List<string> manifests = [];
        for (int index = 0; index < 257; index++)
        {
            workspace.Change("modules.0.identity.moduleId", JsonSerializer.Serialize($"module{index}"));
            workspace.Change("modules.0.identity.servers.0.appId", JsonSerializer.Serialize($"app{index}"));
            workspace.Change("modules.0.identity.servers.0.resourceId", JsonSerializer.Serialize($"resource{index}"));
            manifests.Add(workspace.Save($"module{index}.json"));
        }

        PlatformManifestValidationResult boundary = PlatformManifestValidator.Validate(manifests.Take(256), TestContext.Current.CancellationToken);
        boundary.Diagnostics.ShouldBeEmpty();
        boundary.Declarations.ShouldNotBeNull().Count.ShouldBe(256);
        PlatformManifestValidationResult overflow = PlatformManifestValidator.Validate(manifests, TestContext.Current.CancellationToken);
        overflow.Declarations.ShouldBeNull();
        overflow.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP013" && diagnostic.Field == "manifest");
    }

    /// <summary>Verifies missing readiness cannot yield a partial set.</summary>
    [Fact]
    public void RequiredServerMustHaveUsableReadiness()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.readiness", "[]");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP020" && diagnostic.Field == "modules[0].lifecycle.readiness");
    }

    /// <summary>Verifies overflow cannot bypass the finite startup bound.</summary>
    [Fact]
    public void NonFiniteStartupOverrideFailsLocally()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        string json = File.ReadAllText(path).Replace("\"startup\":{}", "\"startup\":{\"override\":{\"timeoutSeconds\":1e999,\"justification\":\"test\"}}", StringComparison.Ordinal);
        File.WriteAllText(path, json);
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].lifecycle.startup.override.timeoutSeconds");
    }

    /// <summary>Verifies positive finite startup overrides preserve values at floating-point bounds.</summary>
    /// <param name="seconds">The positive finite declared duration.</param>
    [Theory]
    [InlineData(700.5)]
    [InlineData(1e308)]
    [InlineData(1e-300)]
    public void PositiveFiniteStartupOverridesPass(double seconds)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.startup.override", JsonSerializer.Serialize(new { timeoutSeconds = seconds, justification = "Declared local budget" }));
        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies every MCP exposure is rejected by AD-11.</summary>
    /// <param name="exposure">The declared exposure.</param>
    [Theory]
    [InlineData("public")]
    [InlineData("internal-only")]
    [InlineData("disabled")]
    public void EnrolledHostCannotMapMcpForAnyExposure(string exposure)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.surfaces.interfaces.0.protocol", "\"mcp\"");
        workspace.Change("modules.0.surfaces.interfaces.0.exposure", JsonSerializer.Serialize(exposure));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP023" && diagnostic.Message.Contains("AD-11", StringComparison.Ordinal));
    }

    /// <summary>Verifies v1 remains outside the bounded Platform major window.</summary>
    /// <param name="schema">The unsupported schema identity.</param>
    [Theory]
    [InlineData("hexalith.module-manifest.v1")]
    [InlineData("hexalith.module-manifest.v3")]
    [InlineData("hexalith.module-manifest.v0")]
    public void UnsupportedSchemaExplainsEligibility(string schema)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("schema", JsonSerializer.Serialize(schema));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "schema" && diagnostic.Message.Contains("major 2", StringComparison.Ordinal) && diagnostic.Message.Contains("eligible schemas: hexalith.module-manifest.v2", StringComparison.Ordinal));
    }

    /// <summary>Verifies multiple file errors and all duplicate identities aggregate independently of input order.</summary>
    [Fact]
    public void CrossFileDuplicatesAndRecoverableErrorsAggregateDeterministically()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.runtime.dapr.0.role", "<missing>");
        string first = workspace.Save("a.json");
        workspace.Change("modules.0.lifecycle.tasks.0.authorityClass", "<missing>");
        string second = workspace.Save("b.json");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([second, first], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldBe(PlatformManifestValidator.Validate([first, second], TestContext.Current.CancellationToken).Diagnostics);
        ToolDiagnostic[] duplicateDiagnostics = [.. result.Diagnostics.Where(diagnostic => diagnostic.RuleId == "HXP003")];
        duplicateDiagnostics.Length.ShouldBe(6);
        duplicateDiagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].identity.servers[0].appId");
        duplicateDiagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].identity.servers[0].resourceId");
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].runtime.dapr[0].role");
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].lifecycle.tasks[0].authorityClass");
    }

    /// <summary>Verifies server identities remain unique within their owning module.</summary>
    [Fact]
    public void DuplicateServerIdsCannotShareReadiness()
    {
        using PlatformManifestTestWorkspace workspace = new();
        JsonArray servers = workspace.Document["modules"]![0]!["identity"]!["servers"]!.AsArray();
        JsonNode duplicate = servers[0]!.DeepClone();
        duplicate["appId"] = "second-api";
        duplicate["resourceId"] = "second-api-host";
        servers.Add(duplicate);
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP003" && diagnostic.Field == "modules[0].identity.servers[1].id");
        result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.Field!.EndsWith(".appId", StringComparison.Ordinal) || diagnostic.Field.EndsWith(".resourceId", StringComparison.Ordinal));
    }

    /// <summary>Verifies invalid Unicode is diagnosed before schema evaluation and later files still validate.</summary>
    /// <param name="propertyName">Whether the invalid surrogate appears in the property name.</param>
    /// <param name="escapedSurrogate">The escaped unpaired surrogate.</param>
    [Theory]
    [InlineData(false, "\\uD800")]
    [InlineData(false, "\\uDC00")]
    [InlineData(true, "\\uD800")]
    [InlineData(true, "\\uDC00")]
    public void InvalidUnicodeDoesNotAbortLaterFiles(bool propertyName, string escapedSurrogate)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string invalid = workspace.Save("invalid.json");
        string marker = propertyName ? "\"reason\"" : "\"Initial sample enrollment\"";
        File.WriteAllText(invalid, File.ReadAllText(invalid).Replace(marker, $"\"{escapedSurrogate}\"", StringComparison.Ordinal));
        workspace.Change("modules.0.lifecycle.tasks.0.authorityClass", "<missing>");
        string later = workspace.Save("later.json");
        string field = propertyName
            ? "modules[0].lifecycle.classification[\"<invalid Unicode property name at index 1>\"]"
            : "modules[0].lifecycle.classification.reason";
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([invalid, later], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP011" && diagnostic.Field == field && diagnostic.Source!.EndsWith("invalid.json", StringComparison.Ordinal));
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].lifecycle.tasks[0].authorityClass" && diagnostic.Source!.EndsWith("later.json", StringComparison.Ordinal));
        JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain(escapedSurrogate);
    }

    /// <summary>Verifies duplicate nested JSON keys retain their complete location.</summary>
    [Fact]
    public void DuplicateKeysFailWithNestedLocation()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"role\":\"events\"", "\"role\":\"events\",\"role\":\"events\"", StringComparison.Ordinal));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP012" && diagnostic.Field == "modules[0].runtime.dapr[0].role");
    }

    /// <summary>Verifies one valid file cannot leak declarations when another file fails.</summary>
    [Fact]
    public void MalformedMissingAndOversizedFilesAggregateAtomically()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string valid = workspace.Save();
        string malformed = Path.Combine(workspace.Root, "malformed.json");
        File.WriteAllText(malformed, "{\"schema\":");
        string oversized = Path.Combine(workspace.Root, "oversized.json");
        File.WriteAllText(oversized, new string(' ', 1_048_577));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([valid, malformed, oversized, Path.Combine(workspace.Root, "missing.json")], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldBe(["HXP011", "HXP005", "HXP013"]);
    }

    /// <summary>Verifies secret and path safeguards remain metadata-only.</summary>
    [Fact]
    public void CredentialsAndEscapingExecutablePathsAreRejectedWithoutDisclosure()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"../escape.sh\"");
        workspace.Change("modules.0.lifecycle.classification.reason", "\"Bearer fixture-secret-control\"");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXM004");
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXM007");
        JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain("fixture-secret-control");
    }

    /// <summary>Verifies invalid UTF-8 and empty input return diagnostics.</summary>
    [Fact]
    public void InvalidEncodingAndEmptySetFailClosed()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = Path.Combine(workspace.Root, "encoding.json");
        File.WriteAllBytes(path, [(byte)'{', 0xFF, (byte)'}']);
        PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken).Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP011");
        PlatformManifestValidator.Validate([], TestContext.Current.CancellationToken).Declarations.ShouldBeNull();
        PlatformManifestValidator.Validate([], TestContext.Current.CancellationToken).Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP015");
        Encoding.UTF8.GetByteCount(File.ReadAllText(workspace.Save())).ShouldBeLessThan(1_048_576);
    }

    /// <summary>Verifies the byte limit accepts exactly 1 MiB and rejects the next byte atomically.</summary>
    [Fact]
    public void ManifestByteLimitAcceptsExactBoundary()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        string input = File.ReadAllText(path);
        string boundary = input + new string(' ', 1_048_576 - Encoding.UTF8.GetByteCount(input));
        File.WriteAllText(path, boundary, new UTF8Encoding(false));
        new FileInfo(path).Length.ShouldBe(1_048_576);
        PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        File.AppendAllText(path, " ");
        PlatformManifestValidationResult overflow = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
        overflow.Declarations.ShouldBeNull();
        overflow.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP013" && diagnostic.Field == "$");
    }

    /// <summary>Verifies syntax at depth 64 reaches schema validation and depth 65 fails parsing.</summary>
    /// <param name="depth">The JSON nesting depth.</param>
    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    public void JsonDepthBoundaryReturnsStructuredDiagnostics(int depth)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        string input = "{\"schema\":\"hexalith.module-manifest.v2\",\"modules\":[],\"unknown\":"
            + new string('[', depth - 1) + "0" + new string(']', depth - 1) + "}";
        File.WriteAllText(path, input);
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        if (depth == 64)
        {
            result.Diagnostics.ShouldNotContain(diagnostic => diagnostic.RuleId == "HXP011");
            result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "unknown" && diagnostic.RuleId == "HXP002");
        }
        else
        {
            result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP011" && !string.IsNullOrWhiteSpace(diagnostic.Field));
        }
    }

    /// <summary>Verifies later stories can declare provider exceptions, flow checks and tenant-lifecycle markers.</summary>
    /// <param name="surfaceClass">A realm-contract surface class.</param>
    /// <param name="changeClass">A module-intake change class.</param>
    /// <param name="exceptionName">A named AD-9 exception.</param>
    /// <param name="transitional">Whether the exception is transitional.</param>
    [Theory]
    [InlineData("ui", "none", "memories-falkordb-graph", false)]
    [InlineData("agent", "additive", "memories-redis-search-vector", false)]
    [InlineData("service", "breaking", "memories-set-nx-preflight-dedup", false)]
    [InlineData("agent", "none", "memories-redis-coordination", true)]
    public void StoryOwnedDeclarationFieldsPass(string surfaceClass, string changeClass, string exceptionName, bool transitional)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.surfaces.interfaces.0.surfaceClass", JsonSerializer.Serialize(surfaceClass));
        workspace.Change("modules.0.lifecycle.classification.changeClass", JsonSerializer.Serialize(changeClass));
        workspace.Change("modules.0.lifecycle.criticalFlows.0.e2eChecks", "[]");
        workspace.Change("modules.0.lifecycle.criticalFlows.0.tenantLifecycle", "true");
        workspace.Change("modules.0.runtime.providerExceptions", JsonSerializer.Serialize(new[]
        {
            new
            {
                name = exceptionName,
                capability = "named provider capability",
                owner = "memories",
                surface = "adapter",
                transitional,
            },
        }));
        workspace.Change("modules.0.integration.topics.0.deadLetter", "{\"strategy\":\"none\"}");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Diagnostics.ShouldBeEmpty();
        result.Declarations.ShouldNotBeNull().Count.ShouldBe(1);
    }

    /// <summary>Verifies a required server is not covered by a usable probe bound to another server.</summary>
    [Fact]
    public void RequiredServerIgnoresReadinessBoundToAnotherServer()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Document["modules"]![0]!["identity"]!["servers"]!.AsArray().Add(JsonNode.Parse("""
            {
              "id": "worker",
              "appId": "sample-worker",
              "resourceId": "sample-worker-host",
              "enabled": true,
              "required": true
            }
            """));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP020" && diagnostic.Field == "modules[0].lifecycle.readiness" && diagnostic.Message.Contains("servers[1].id", StringComparison.Ordinal));
    }

    /// <summary>Verifies a disabled optional server can enroll without readiness.</summary>
    [Fact]
    public void DisabledOptionalServerCanOmitReadiness()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.identity.servers.0.required", "false");
        workspace.Change("modules.0.identity.servers.0.enabled", "false");
        workspace.Change("modules.0.lifecycle.readiness", "[]");
        PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken).Diagnostics.ShouldBeEmpty();
    }

    /// <summary>Verifies an enabled optional server enrolls when its readiness list is empty.</summary>
    [Fact]
    public void EnabledOptionalServerCanOmitReadiness()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.identity.servers.0.required", "false");
        workspace.Change("modules.0.lifecycle.readiness", "[]");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Diagnostics.ShouldBeEmpty();
        result.Declarations.ShouldNotBeNull().Count.ShouldBe(1);
    }

    /// <summary>Verifies dot segments and repeated slashes are not usable readiness for a required server.</summary>
    /// <param name="endpoint">The rejected readiness endpoint.</param>
    [Theory]
    [InlineData("/health/../admin")]
    [InlineData("/health//ready")]
    public void RequiredServerRejectsEscapingReadinessEndpoints(string endpoint)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.readiness.0.endpoint", JsonSerializer.Serialize(endpoint));
        string manifest = workspace.Save();
        using JsonDocument input = JsonDocument.Parse(File.ReadAllText(manifest));
        PublishedSchema().Evaluate(input.RootElement).IsValid.ShouldBeFalse();
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP020" && diagnostic.Field == "modules[0].lifecycle.readiness");
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Field == "modules[0].lifecycle.readiness[0].endpoint");
    }

    /// <summary>Verifies a scheme-relative credential URI cannot enroll.</summary>
    [Fact]
    public void SchemeRelativeCredentialUriIsRejected()
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", "[\"--endpoint=//user:pw@host.example\"]");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXM007" && diagnostic.Field == "modules[0].lifecycle.tasks[0].arguments[0]");
        JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain("user:pw");
    }

    /// <summary>Verifies a percent-encoded userinfo separator cannot enroll or appear in diagnostics.</summary>
    /// <param name="argument">The credential-bearing argument.</param>
    [Theory]
    [InlineData("//user:pw%40host.example")]
    [InlineData("https://user:pw%40host.example")]
    public void PercentEncodedUserInfoSeparatorIsRejected(string argument)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", JsonSerializer.Serialize(new[] { argument }));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXM007" && diagnostic.Field == "modules[0].lifecycle.tasks[0].arguments[0]");
        string rendered = JsonSerializer.Serialize(result.Diagnostics);
        rendered.ShouldNotContain("user:pw");
        rendered.ShouldNotContain("%40");
    }

    /// <summary>Verifies placeholder and credential paths keep the value inspection diagnostic only.</summary>
    /// <param name="executable">The rejected executable path.</param>
    /// <param name="ruleId">The single expected safeguard rule.</param>
    [Theory]
    [InlineData("$TOOLS/run.sh", "HXM006")]
    [InlineData("token=fixture-secret-value", "HXM007")]
    public void ExecutableSafeguardsAreNotDuplicated(string executable, string ruleId)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.executable", JsonSerializer.Serialize(executable));
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([workspace.Save()], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.Where(diagnostic => diagnostic.RuleId == ruleId).ShouldHaveSingleItem().Hint.ShouldBeNull();
        JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain("invoking the runner");
        JsonSerializer.Serialize(result.Diagnostics).ShouldNotContain("fixture-secret-value");
    }

    /// <summary>Verifies a UTF-8 BOM does not reject an otherwise valid declaration.</summary>
    [Fact]
    public void Utf8BomPrefixedManifestRemainsValid()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        byte[] content = File.ReadAllBytes(path);
        byte[] prefixed = new byte[content.Length + 3];
        prefixed[0] = 0xEF;
        prefixed[1] = 0xBB;
        prefixed[2] = 0xBF;
        content.CopyTo(prefixed, 3);
        File.WriteAllBytes(path, prefixed);
        PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken).Diagnostics.ShouldBeEmpty();
    }

    /// <summary>Verifies JSON syntax failures retain the parser line and column.</summary>
    [Fact]
    public void JsonSyntaxErrorsIncludeLocation()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        const string content = "{\n\"schema\":";
        File.WriteAllText(path, content);
        JsonException? parseError = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            _ = document.RootElement;
        }
        catch (JsonException exception)
        {
            parseError = exception;
        }

        string location = $"{parseError!.LineNumber! + 1}:{parseError.BytePositionInLine! + 1}";
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
        result.Declarations.ShouldBeNull();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP011" && diagnostic.Field == "$" && diagnostic.Location == location);
    }

    /// <summary>Verifies a non-seekable manifest returns a structured read failure.</summary>
    /// <returns>A task that completes after validation.</returns>
    [Fact(Timeout = 15000)]
    public async Task NonSeekableManifestReturnsStructuredFailureAsync()
    {
        await PlatformManifestTestWorkspace.UseNonSeekableManifestAsync(path =>
        {
            PlatformManifestValidationResult result = PlatformManifestValidator.Validate([path], TestContext.Current.CancellationToken);
            result.Declarations.ShouldBeNull();
            result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP005" && diagnostic.Field == "manifest");
            return Task.CompletedTask;
        }).ConfigureAwait(true);
    }

    /// <summary>Verifies a deleted working directory returns a structured path failure.</summary>
    /// <returns>A task that completes after the isolated probe.</returns>
    [Fact(Timeout = 120000)]
    public async Task DeletedWorkingDirectoryReturnsStructuredPathFailure()
    {
        if (!await PlatformManifestTestWorkspace.IsIsolatedDirectoryProbeAsync(nameof(DeletedWorkingDirectoryReturnsStructuredPathFailure)).ConfigureAwait(true))
        {
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        string manifest = workspace.Save();
        string original = Directory.GetCurrentDirectory();
        string deleted = Path.Combine(workspace.Root, "deleted-cwd");
        _ = Directory.CreateDirectory(deleted);
        try
        {
            Directory.SetCurrentDirectory(deleted);
            Directory.Delete(deleted);
            PlatformManifestValidationResult result = PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken);
            result.Declarations.ShouldBeNull();
            result.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXP004" && diagnostic.Field == "manifest" && diagnostic.Source == "manifest");
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    private static JsonSchema PublishedSchema() => JsonSchema.FromFile(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "schemas/hexalith.module-manifest.v2.json"), new BuildOptions { SchemaRegistry = new SchemaRegistry() });
}