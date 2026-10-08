// <copyright file="PlatformManifestCommandTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies public validation output, repeatable manifests, cancellation and absence of lifecycle effects.
/// </summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class PlatformManifestCommandTests
{
    /// <summary>Verifies valid enrollment leaves all input bytes intact and creates no runtime files.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task ValidationSucceedsWithoutExecutingLifecycleAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string sentinel = Path.Combine(workspace.Root, "executed");
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "never-run.sh"), $"touch '{sentinel}'");
        workspace.Change("modules.0.lifecycle.tasks.0.executable", "\"never-run.sh\"");
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", JsonSerializer.Serialize(new[] { string.Empty, "  " }));
        string manifest = workspace.Save();
        string[] files = [.. Directory.GetFiles(workspace.Root).Order(StringComparer.Ordinal)];
        string input = await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", manifest, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(0);
            output.ToString().ShouldContain("validated");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertPassedOutcome(result.RootElement);
            }

            File.Exists(sentinel).ShouldBeFalse();
            Directory.GetFiles(workspace.Root).Order(StringComparer.Ordinal).ShouldBe(files);
            (await File.ReadAllTextAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(input);
        }
    }

    /// <summary>Verifies repeatable manifests aggregate duplicate identities in both output formats.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task RepeatableManifestsReportSourceFieldAndReasonAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string first = workspace.Save("a.json");
        workspace.Change("modules.0.lifecycle.tasks.0.authorityClass", "<missing>");
        string second = workspace.Save("b.json");
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", first, "--manifest", second, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("a.json");
            output.ToString().ShouldContain("b.json");
            output.ToString().ShouldContain("modules[0].identity.servers[0].appId");
            output.ToString().ShouldContain("modules[0].lifecycle.tasks[0].authorityClass");
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").GetArrayLength().ShouldBeGreaterThanOrEqualTo(7);
            }
        }
    }

    /// <summary>Verifies every local rejection category through both public diagnostic formats.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after all invalid commands.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task InvalidEnrollmentMatrixReportsActionableDiagnosticsAsync(string format)
    {
        (string Path, string Json, string Field)[] cases =
        [
            ("modules.0.runtime.resources.0.requests.cpu", "0", "modules[0].runtime.resources[0].requests.cpu"),
            ("modules.0.runtime.resources.0.requests.cpu", "-1", "modules[0].runtime.resources[0].requests.cpu"),
            ("modules.0.runtime.resources.0.limits.cpu", "0", "modules[0].runtime.resources[0].limits.cpu"),
            ("modules.0.runtime.resources.0.limits.cpu", "-1", "modules[0].runtime.resources[0].limits.cpu"),
            ("modules.0.runtime.resources.0.requests.memoryMiB", "0", "modules[0].runtime.resources[0].requests.memoryMiB"),
            ("modules.0.runtime.resources.0.requests.memoryMiB", "-1", "modules[0].runtime.resources[0].requests.memoryMiB"),
            ("modules.0.runtime.resources.0.limits.memoryMiB", "0", "modules[0].runtime.resources[0].limits.memoryMiB"),
            ("modules.0.runtime.resources.0.limits.memoryMiB", "-1", "modules[0].runtime.resources[0].limits.memoryMiB"),
            ("modules.0.runtime.resources.0.volumes.0.sizeMiB", "0", "modules[0].runtime.resources[0].volumes[0].sizeMiB"),
            ("modules.0.runtime.resources.0.volumes.0.sizeMiB", "-1", "modules[0].runtime.resources[0].volumes[0].sizeMiB"),
            ("modules.0.lifecycle.readiness.0.endpoint", "\"//provider.example/health\"", "modules[0].lifecycle.readiness[0].endpoint"),
            ("modules.0.lifecycle.readiness.0.executable", "\"probe\\n\"", "modules[0].lifecycle.readiness[0].executable"),
            ("modules.0.lifecycle.readiness.0.executable", "\"probe\\u0000\"", "modules[0].lifecycle.readiness[0].executable"),
            ("modules.0.lifecycle.readiness.0.executable", "\"probe\\u0085\"", "modules[0].lifecycle.readiness[0].executable"),
            ("schema", "\"hexalith.module-manifest.v1\"", "schema"),
            ("schema", "\"hexalith.module-manifest.v3\"", "schema"),
            ("schema", "<missing>", "schema"),
            ("modules.0.identity", "<missing>", "modules[0].identity"),
            ("modules.0.identity.moduleId", "17", "modules[0].identity.moduleId"),
            ("modules.0.runtime.dapr.0.role", "<missing>", "modules[0].runtime.dapr[0].role"),
            ("modules.0.runtime.dapr.0.recoveryClass", "<missing>", "modules[0].runtime.dapr[0].recoveryClass"),
            ("modules.0.runtime.dapr.0.recoveryClass", "\"unknown\"", "modules[0].runtime.dapr[0].recoveryClass"),
            ("modules.0.runtime.dapr.0.configurationKey", "<missing>", "modules[0].runtime.dapr[0].configurationKey"),
            ("modules.0.runtime.dapr.0.componentName", "\"literal-component\"", "modules[0].runtime.dapr[0].componentName"),
            ("modules.0.lifecycle.readiness", "[]", "modules[0].lifecycle.readiness"),
            ("modules.0.lifecycle.readiness.0.endpoint", "<missing>", "modules[0].lifecycle.readiness[0].endpoint"),
            ("modules.0.lifecycle.readiness.0.endpoint", "\"\"", "modules[0].lifecycle.readiness[0].endpoint"),
            ("modules.0.lifecycle.tasks.0.scope", "\"always\"", "modules[0].lifecycle.tasks[0].scope"),
            ("modules.0.lifecycle.tasks.0.authorityClass", "<missing>", "modules[0].lifecycle.tasks[0].authorityClass"),
            ("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":0,\"justification\":\"test\"}", "modules[0].lifecycle.startup.override.timeoutSeconds"),
            ("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":-1,\"justification\":\"test\"}", "modules[0].lifecycle.startup.override.timeoutSeconds"),
            ("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":null,\"justification\":\"test\"}", "modules[0].lifecycle.startup.override.timeoutSeconds"),
            ("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":1e-999,\"justification\":\"test\"}", "modules[0].lifecycle.startup.override.timeoutSeconds"),
            ("modules.0.lifecycle.startup.override", "{\"timeoutSeconds\":700,\"justification\":\"  \"}", "modules[0].lifecycle.startup.override.justification"),
            ("modules.0.integration.topics.0.deadLetter.topic", "<missing>", "modules[0].integration.topics[0].deadLetter.topic"),
            ("modules.0.surfaces.interfaces.0.protocol", "\"mcp\"", "modules[0].surfaces.interfaces[0].protocol"),
            ("modules.0.surfaces.interfaces.0.Protocol", "\"http\"", "modules[0].surfaces.interfaces[0].Protocol"),
            ("modules.0.surfaces.interfaces.0.routePrefix", "\"/sample\\n\"", "modules[0].surfaces.interfaces[0].routePrefix"),
            ("modules.0.surfaces.interfaces.0.routePrefix", "\"/sample\\u0000route\"", "modules[0].surfaces.interfaces[0].routePrefix"),
            ("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/data\\u0000path\"", "modules[0].runtime.resources[0].volumes[0].mountPath"),
            ("modules.0.surfaces.interfaces.0.routePrefix", "\"/api/../admin//x\"", "modules[0].surfaces.interfaces[0].routePrefix"),
            ("modules.0.surfaces.interfaces.0.routePrefix", "\"/./sample\"", "modules[0].surfaces.interfaces[0].routePrefix"),
            ("modules.0.surfaces.interfaces.0.routePrefix", "\"//sample\"", "modules[0].surfaces.interfaces[0].routePrefix"),
            ("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/api/../admin//x\"", "modules[0].runtime.resources[0].volumes[0].mountPath"),
            ("modules.0.runtime.resources.0.volumes.0.mountPath", "\"/./data\"", "modules[0].runtime.resources[0].volumes[0].mountPath"),
            ("modules.0.surfaces.interfaces.0.surfaceClass", "\"gateway\"", "modules[0].surfaces.interfaces[0].surfaceClass"),
            ("modules.0.surfaces.interfaces.0.surfaceClass", "\"mcp\"", "modules[0].surfaces.interfaces[0].surfaceClass"),
            ("modules.0.lifecycle.classification.changeClass", "\"patch\"", "modules[0].lifecycle.classification.changeClass"),
            ("modules.0.lifecycle.readiness.0.service", "\"health\"", "modules[0].lifecycle.readiness[0].service"),
            ("modules.0.lifecycle.readiness.0.arguments", "[]", "modules[0].lifecycle.readiness[0].arguments"),
            ("modules.0.integration.topics.0.deadLetter.strategy", "\"none\"", "modules[0].integration.topics[0].deadLetter.topic"),
        ];
        foreach ((string path, string json, string field) in cases)
        {
            await AssertMutationAsync(path, json, field, format).ConfigureAwait(true);
        }

        using PlatformManifestTestWorkspace rawWorkspace = new();
        string duplicate = rawWorkspace.Save("duplicate.json");
        await File.WriteAllTextAsync(duplicate, (await File.ReadAllTextAsync(duplicate, TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("\"role\":\"events\"", "\"role\":\"events\",\"role\":\"events\"", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(duplicate, "modules[0].runtime.dapr[0].role", format).ConfigureAwait(true);
        string overflow = rawWorkspace.Save("overflow.json");
        await File.WriteAllTextAsync(overflow, (await File.ReadAllTextAsync(overflow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("\"startup\":{}", "\"startup\":{\"override\":{\"timeoutSeconds\":1e999,\"justification\":\"test\"}}", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(overflow, "modules[0].lifecycle.startup.override.timeoutSeconds", format).ConfigureAwait(true);
        string invalidJson = Path.Combine(rawWorkspace.Root, "syntax.json");
        await File.WriteAllTextAsync(invalidJson, "{\"schema\": NaN}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(invalidJson, "$", format).ConfigureAwait(true);
    }

    /// <summary>Verifies AD-11 rejects host MCP endpoints under every exposure in both formats.</summary>
    /// <param name="exposure">The declared exposure.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("public", "human")]
    [InlineData("public", "json")]
    [InlineData("internal-only", "human")]
    [InlineData("internal-only", "json")]
    [InlineData("disabled", "human")]
    [InlineData("disabled", "json")]
    public async Task McpEndpointsFailUnderEveryExposureAsync(string exposure, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.surfaces.interfaces.0.protocol", "\"mcp\"");
        workspace.Change("modules.0.surfaces.interfaces.0.exposure", JsonSerializer.Serialize(exposure));
        await AssertInvalidAsync(workspace.Save(), "modules[0].surfaces.interfaces[0].protocol", format).ConfigureAwait(true);
    }

    /// <summary>Verifies a required server cannot be disabled in either diagnostic format.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task RequiredDisabledServerReturnsLocalFailureAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.identity.servers.0.enabled", "false");
        await AssertInvalidAsync(workspace.Save(), "modules[0].identity.servers[0].enabled", format, "HXP020").ConfigureAwait(true);
    }

    /// <summary>Verifies command-readiness probes must reference an existing local file.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task MissingCommandReadinessExecutableReturnsLocalFailureAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.readiness.0.kind", "\"command\"");
        workspace.Change("modules.0.lifecycle.readiness.0.executable", "\"probes/missing-ready.sh\"");
        await AssertInvalidAsync(workspace.Save(), "modules[0].lifecycle.readiness[0].executable", format, "HXM005").ConfigureAwait(true);
    }

    /// <summary>Verifies required servers enroll with either supported non-HTTP readiness kind without execution.</summary>
    /// <param name="kind">The supported readiness kind.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("grpc", "human")]
    [InlineData("grpc", "json")]
    [InlineData("command", "human")]
    [InlineData("command", "json")]
    public async Task SupportedReadinessKindsEnrollAsync(string kind, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string sentinel = Path.Combine(workspace.Root, "executed");
        CompositionTestFiles.WriteScript(Path.Combine(workspace.Root, "ready.sh"), $"touch '{sentinel}'");
        string probe = kind == "grpc"
            ? JsonSerializer.Serialize(new { server = "api", kind, service = "sample.Readiness" })
            : JsonSerializer.Serialize(new { server = "api", kind, executable = "ready.sh", arguments = new[] { string.Empty, "  " } });
        workspace.Change("modules.0.lifecycle.readiness", $"[{probe}]");
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(0);
            File.Exists(sentinel).ShouldBeFalse();
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertPassedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldBeEmpty();
            }
            else
            {
                output.ToString().ShouldContain("validated");
                output.ToString().ShouldNotContain("HXP");
                output.ToString().ShouldNotContain("HXM");
            }
        }
    }

    /// <summary>Verifies excessive repeated inputs retain the file-bound diagnostic in both formats.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task ManifestFileLimitReturnsLocalFailureAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string manifest = workspace.Save();
        List<string> arguments = ["validate", "--output", format];
        for (int index = 0; index < 257; index++)
        {
            arguments.AddRange(["--manifest", manifest]);
        }

        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                [.. arguments], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("HXP013");
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("ruleId").GetString() == "HXP013" && diagnostic.GetProperty("field").GetString() == "manifest");
            }
        }
    }

    /// <summary>Verifies credentials in URI userinfo are rejected without disclosing the credential.</summary>
    /// <param name="destination">The credential-bearing URI.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("https://user:fixture-password@example.com", "human")]
    [InlineData("https://user:fixture-password@example.com", "json")]
    [InlineData("https://user:fixture%2Dpassword@example.com", "human")]
    [InlineData("https://user:fixture%2Dpassword@example.com", "json")]
    public async Task UriCredentialsFailWithoutDisclosureAsync(string destination, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.integration.egress.0.destination", JsonSerializer.Serialize(destination));
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("HXM007");
            output.ToString().ShouldContain("modules[0].integration.egress[0].destination");
            output.ToString().ShouldNotContain(destination);
            output.ToString().ShouldNotContain("fixture-password");
            output.ToString().ShouldNotContain("fixture%2Dpassword");
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
            }
        }
    }

    /// <summary>Verifies unusual object names keep one escaped property path rather than fictitious nesting.</summary>
    /// <param name="name">The unknown object property.</param>
    /// <param name="field">The expected escaped field path.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("123", "modules[0].identity[\"123\"]", "human")]
    [InlineData("123", "modules[0].identity[\"123\"]", "json")]
    [InlineData("bad.key", "modules[0].identity[\"bad.key\"]", "human")]
    [InlineData("bad.key", "modules[0].identity[\"bad.key\"]", "json")]
    [InlineData("bad[0]", "modules[0].identity[\"bad[0]\"]", "human")]
    [InlineData("bad[0]", "modules[0].identity[\"bad[0]\"]", "json")]
    [InlineData("bad\nkey", "modules[0].identity[\"bad\\nkey\"]", "human")]
    [InlineData("bad\nkey", "modules[0].identity[\"bad\\nkey\"]", "json")]
    [InlineData("a/b~c", "modules[0].identity[\"a/b~c\"]", "human")]
    [InlineData("a/b~c", "modules[0].identity[\"a/b~c\"]", "json")]
    public async Task UnusualPropertyNamesHaveUnambiguousPathsAsync(string name, string field, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Document["modules"]![0]!["identity"]![name] = "unknown";
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                JsonElement diagnostics = result.RootElement.GetProperty("diagnostics");
                diagnostics.GetArrayLength().ShouldBeGreaterThan(0);
                foreach (JsonElement diagnostic in diagnostics.EnumerateArray())
                {
                    diagnostic.GetProperty("field").GetString().ShouldBe(field);
                }
            }
            else
            {
                output.ToString().ShouldContain($"field={field}");
                output.ToString().ShouldNotContain("identity.123");
                output.ToString().ShouldNotContain("identity[123]");
                output.ToString().ShouldNotContain("identity.bad.key");
            }
        }
    }

    /// <summary>Verifies an empty unknown property has an explicit escaped field in both formats.</summary>
    /// <param name="nested">Whether the empty name appears inside the identity object.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData(false, "human")]
    [InlineData(false, "json")]
    [InlineData(true, "human")]
    [InlineData(true, "json")]
    public async Task EmptyPropertyNamesHaveVisibleFieldPathsAsync(bool nested, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change(nested ? "modules.0.identity." : string.Empty, "\"unknown\"");
        string field = nested ? "modules[0].identity[\"\"]" : "$[\"\"]";
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("field").GetString() == field);
            }
            else
            {
                output.ToString().ShouldContain($"field={field}");
            }
        }
    }

    /// <summary>Verifies unpaired surrogate strings and names do not abort later files or structured output.</summary>
    /// <param name="propertyName">Whether the invalid surrogate appears in the property name.</param>
    /// <param name="escapedSurrogate">The escaped unpaired surrogate.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData(false, "\\uD800", "human")]
    [InlineData(false, "\\uD800", "json")]
    [InlineData(false, "\\uDC00", "human")]
    [InlineData(false, "\\uDC00", "json")]
    [InlineData(true, "\\uD800", "human")]
    [InlineData(true, "\\uD800", "json")]
    [InlineData(true, "\\uDC00", "human")]
    [InlineData(true, "\\uDC00", "json")]
    public async Task InvalidUnicodeRetainsFileDiagnosticsAndContinuesAsync(bool propertyName, string escapedSurrogate, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string invalid = workspace.Save("invalid.json");
        string marker = propertyName ? "\"reason\"" : "\"Initial sample enrollment\"";
        string input = await File.ReadAllTextAsync(invalid, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(invalid, input.Replace(marker, $"\"{escapedSurrogate}\"", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        workspace.Change("modules.0.lifecycle.tasks.0.authorityClass", "<missing>");
        string later = workspace.Save("later.json");
        string field = propertyName
            ? "modules[0].lifecycle.classification[\"<invalid Unicode property name at index 1>\"]"
            : "modules[0].lifecycle.classification.reason";
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", invalid, "--manifest", later, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("invalid.json");
            output.ToString().ShouldContain("later.json");
            output.ToString().ShouldContain("modules[0].lifecycle.tasks[0].authorityClass");
            output.ToString().ShouldNotContain("declarations");
            output.ToString().ShouldNotContain(escapedSurrogate);
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("ruleId").GetString() == "HXP011" && diagnostic.GetProperty("field").GetString() == field);
            }
            else
            {
                output.ToString().ShouldContain("HXP011");
                output.ToString().ShouldContain($"field={field}");
            }
        }
    }

    /// <summary>Verifies a blank later repeated manifest is rejected by the parser contract.</summary>
    /// <returns>A task that completes after the command.</returns>
    [Fact]
    public async Task BlankRepeatedManifestReturnsUsageAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--manifest", " ", "--output", "json"], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("HXC001");
        }
    }

    /// <summary>Verifies cancellation is preserved as exit 130 with structured diagnostics.</summary>
    /// <returns>A task that completes after the command.</returns>
    [Fact]
    public async Task CancellationReturnsExistingCancelledContractAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync().ConfigureAwait(true);
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", "json"], output, TextWriter.Null, cancellation.Token).ConfigureAwait(true);
            exitCode.ShouldBe((int)ToolExitCode.Cancelled);
            output.ToString().ShouldContain("HXC130");
            using JsonDocument result = JsonDocument.Parse(output.ToString());
            AssertOutcome(result.RootElement, "cancelled", ToolExitCode.Cancelled, ToolPhase.Manifest, ToolFailureCategory.Cancelled, "HXC130");
        }
    }

    /// <summary>Verifies embedded URI credentials fail without reaching command output.</summary>
    /// <param name="argument">The credential-bearing command argument.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after validation.</returns>
    [Theory]
    [InlineData("--endpoint=https://fixture-user:fixture-password@provider.example", "human")]
    [InlineData("--endpoint=https://fixture-user:fixture-password@provider.example", "json")]
    [InlineData("fetch https://fixture-user:fixture-password@provider.example", "human")]
    [InlineData("fetch https://fixture-user:fixture-password@provider.example", "json")]
    public async Task EmbeddedUriCredentialsFailWithoutDisclosureAsync(string argument, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", JsonSerializer.Serialize(new[] { argument }));
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldContain("HXM007");
            output.ToString().ShouldContain("modules[0].lifecycle.tasks[0].arguments[0]");
            output.ToString().ShouldNotContain("fixture-password");
            output.ToString().ShouldNotContain("declarations");
        }
    }

    /// <summary>Verifies credential-shaped filenames and property names are redacted in every output mode.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after validation.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task CredentialBearingDiagnosticPathsAreRedactedAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Document["modules"]![0]!["identity"]!["ghp_fixture123456"] = "unknown";
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save("ghp_fixture123456.json"), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldNotContain("ghp_fixture123456");
            output.ToString().ShouldContain("[redacted manifest path]");
            output.ToString().ShouldContain("[redacted field]");
            output.ToString().ShouldContain("HXP002");
            output.ToString().ShouldNotContain("declarations");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic =>
                    diagnostic.GetProperty("source").GetString() == "[redacted manifest path]"
                    && diagnostic.GetProperty("field").GetString() == "modules[0].identity[\"[redacted field]\"]");
            }
        }
    }

    /// <summary>Verifies invalid root strings always report an explicit root field.</summary>
    /// <param name="input">The raw JSON root string.</param>
    /// <param name="ruleId">The expected local rejection.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after validation.</returns>
    [Theory]
    [InlineData("\"Bearer fixture-root-control\"", "HXM007", "human")]
    [InlineData("\"Bearer fixture-root-control\"", "HXM007", "json")]
    [InlineData("\"${REPLACE_ME}\"", "HXM006", "human")]
    [InlineData("\"${REPLACE_ME}\"", "HXM006", "json")]
    public async Task InvalidRootStringsHaveExplicitFieldsAsync(string input, string ruleId, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        await File.WriteAllTextAsync(path, input, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(path, "$", format, ruleId).ConfigureAwait(true);
    }

    /// <summary>Verifies byte and depth boundaries preserve actionable public diagnostics.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after all boundary commands.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task ParsingBoundariesRemainActionableAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = workspace.Save();
        string input = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, input + new string(' ', 1_048_576 - Encoding.UTF8.GetByteCount(input)), TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", path, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(0);
            output.ToString().ShouldContain("validated");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertPassedOutcome(result.RootElement);
            }
        }

        await File.AppendAllTextAsync(path, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(path, "$", format, "HXP013").ConfigureAwait(true);
        string depth64 = "{\"schema\":\"hexalith.module-manifest.v2\",\"modules\":[],\"unknown\":"
            + new string('[', 63) + "0" + new string(']', 63) + "}";
        await File.WriteAllTextAsync(path, depth64, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(path, "unknown", format, "HXP002").ConfigureAwait(true);
        string depth65 = "{\"schema\":\"hexalith.module-manifest.v2\",\"modules\":[],\"unknown\":"
            + new string('[', 64) + "0" + new string(']', 64) + "}";
        await File.WriteAllTextAsync(path, depth65, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await AssertInvalidAsync(path, "$", format, "HXP011").ConfigureAwait(true);
    }

    /// <summary>Verifies readiness binding and the required-server gate in both formats.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after both commands.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task RequiredServerReadinessBindingIsEnforcedAsync(string format)
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
        await AssertInvalidAsync(workspace.Save(), "modules[0].lifecycle.readiness", format, "HXP020").ConfigureAwait(true);

        using PlatformManifestTestWorkspace optional = new();
        optional.Change("modules.0.identity.servers.0.required", "false");
        optional.Change("modules.0.identity.servers.0.enabled", "false");
        optional.Change("modules.0.lifecycle.readiness", "[]");
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", optional.Save(), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(0);
            output.ToString().ShouldContain("validated");
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertPassedOutcome(result.RootElement);
            }
        }
    }

    /// <summary>Verifies scheme-relative credential URIs fail in both formats without disclosure.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task SchemeRelativeCredentialUriFailsAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change("modules.0.lifecycle.tasks.0.arguments", "[\"--endpoint=//user:pw@host.example\"]");
        await AssertInvalidAsync(workspace.Save(), "modules[0].lifecycle.tasks[0].arguments[0]", format, "HXM007").ConfigureAwait(true);
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", workspace.Save("again.json"), "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            output.ToString().ShouldNotContain("user:pw");
        }
    }

    /// <summary>Verifies JSON syntax failures expose a location in both formats.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory]
    [InlineData("human")]
    [InlineData("json")]
    public async Task JsonSyntaxErrorsExposeLocationAsync(string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string path = Path.Combine(workspace.Root, "syntax.json");
        const string content = "{\n\"schema\":";
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonException? parseError = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            _ = document;
        }
        catch (JsonException exception)
        {
            parseError = exception;
        }

        string location = $"{parseError!.LineNumber! + 1}:{parseError.BytePositionInLine! + 1}";
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", path, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1);
            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic =>
                    diagnostic.GetProperty("field").GetString() == "$"
                    && diagnostic.GetProperty("location").GetString() == location);
            }
            else
            {
                output.ToString().ShouldContain("failed");
                output.ToString().ShouldContain("field=$ ");
                output.ToString().ShouldContain($"location={location}");
            }
        }
    }

    /// <summary>Verifies a non-seekable manifest stays structured.</summary>
    /// <param name="format">The diagnostic output format.</param>
    /// <returns>A task that completes after the command.</returns>
    [Theory(Timeout = 15000)]
    [InlineData("human")]
    [InlineData("json")]
    public async Task UnreadableManifestInputsReturnStructuredFailuresAsync(string format)
    {
        await PlatformManifestTestWorkspace.UseNonSeekableManifestAsync(async path =>
        {
            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["validate", "--manifest", path, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
                exitCode.ShouldBe(1);
                output.ToString().ShouldContain("HXP005");
                output.ToString().ShouldContain("failed");
                if (format == "json")
                {
                    using JsonDocument result = JsonDocument.Parse(output.ToString());
                    AssertFailedOutcome(result.RootElement);
                }
            }
        }).ConfigureAwait(true);
    }

    /// <summary>Verifies a deleted working directory stays a structured command failure.</summary>
    /// <returns>A task that completes after the isolated probe.</returns>
    [Fact(Timeout = 120000)]
    public async Task DeletedWorkingDirectoryCommandReturnsStructuredFailureAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows cannot delete a process's current directory.");
        }

        if (!await PlatformManifestTestWorkspace.IsIsolatedDirectoryProbeAsync(nameof(DeletedWorkingDirectoryCommandReturnsStructuredFailureAsync)).ConfigureAwait(true))
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
            foreach (string format in new[] { "human", "json" })
            {
                StringWriter output = new();
                await using (output.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        ["validate", "--manifest", manifest, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
                    exitCode.ShouldBe(1);
                    output.ToString().ShouldContain("HXP004");
                    output.ToString().ShouldContain("failed");
                    output.ToString().ShouldNotContain("DirectoryNotFoundException");
                    if (format == "json")
                    {
                        using JsonDocument result = JsonDocument.Parse(output.ToString());
                        AssertFailedOutcome(result.RootElement);
                    }
                }
            }
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
        }
    }

    private static async Task AssertMutationAsync(string path, string json, string field, string format)
    {
        using PlatformManifestTestWorkspace workspace = new();
        workspace.Change(path, json);
        await AssertInvalidAsync(workspace.Save(), field, format).ConfigureAwait(true);
    }

    private static async Task AssertInvalidAsync(string manifest, string field, string format, string? ruleId = null)
    {
        string expectedSource = Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(manifest)).Replace('\\', '/');
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", manifest, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string rendered = output.ToString();
            exitCode.ShouldBe(1, $"Expected a local configuration failure for {field}.{Environment.NewLine}{rendered}");
            if (ruleId is not null)
            {
                rendered.ShouldContain(ruleId);
            }

            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(rendered);
                AssertFailedOutcome(result.RootElement);
                result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic =>
                    diagnostic.GetProperty("field").GetString() == field
                    && diagnostic.GetProperty("source").GetString() == expectedSource
                    && (ruleId == null || diagnostic.GetProperty("ruleId").GetString() == ruleId));
            }
            else
            {
                rendered.ShouldContain("failed");
                ShouldContainLabel(rendered, "source", expectedSource);
                ShouldContainLabel(rendered, "field", field);
            }
        }
    }

    private static void AssertPassedOutcome(JsonElement root) =>
        AssertOutcome(root, "validated", ToolExitCode.Success, ToolPhase.None, ToolFailureCategory.None, null);

    private static void AssertFailedOutcome(JsonElement root)
    {
        string? ruleId = root.GetProperty("diagnostics").EnumerateArray().First().GetProperty("ruleId").GetString();
        AssertOutcome(root, "failed", ToolExitCode.UsageOrManifest, ToolPhase.Manifest, ToolFailureCategory.Manifest, ruleId);
    }

    private static void AssertOutcome(JsonElement root, string status, ToolExitCode exitCode, ToolPhase phase, ToolFailureCategory category, string? ruleId)
    {
        root.GetProperty("status").GetString().ShouldBe(status);
        JsonElement outcome = root.GetProperty("outcome");
        outcome.GetProperty("exitCode").GetString().ShouldBe(exitCode.ToString());
        outcome.GetProperty("phase").GetString().ShouldBe(phase.ToString());
        outcome.GetProperty("category").GetString().ShouldBe(category.ToString());
        JsonElement rule = outcome.GetProperty("ruleId");
        if (ruleId is null)
        {
            rule.ValueKind.ShouldBe(JsonValueKind.Null);
        }
        else
        {
            rule.GetString().ShouldBe(ruleId);
        }
    }

    private static void ShouldContainLabel(string rendered, string name, string value)
    {
        bool labelled = rendered.Contains($"{name}={value} ", StringComparison.Ordinal)
            || rendered.Contains($"{name}={value}:", StringComparison.Ordinal);
        labelled.ShouldBeTrue($"Expected {name}={value} in:{Environment.NewLine}{rendered}");
    }
}