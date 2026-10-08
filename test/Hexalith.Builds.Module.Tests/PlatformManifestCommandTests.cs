// <copyright file="PlatformManifestCommandTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies public validation output, repeatable manifests, cancellation and absence of lifecycle effects.
/// </summary>
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
                result.RootElement.GetProperty("status").GetString().ShouldBe("failed");
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
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exitCode = await ModuleCommandApplication.InvokeAsync(
                ["validate", "--manifest", manifest, "--output", format], output, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
            exitCode.ShouldBe(1, $"Expected a local configuration failure for {field}.");
            output.ToString().ShouldContain(Path.GetFileName(manifest));
            output.ToString().ShouldContain(field);
            output.ToString().ShouldNotContain("declarations");
            if (ruleId is not null)
            {
                output.ToString().ShouldContain(ruleId);
            }

            if (format == "json")
            {
                using JsonDocument result = JsonDocument.Parse(output.ToString());
                foreach (JsonElement diagnostic in result.RootElement.GetProperty("diagnostics").EnumerateArray())
                {
                    diagnostic.GetProperty("source").GetString().ShouldNotBeNullOrWhiteSpace();
                    diagnostic.GetProperty("field").GetString().ShouldNotBeNullOrWhiteSpace();
                    diagnostic.GetProperty("message").GetString().ShouldNotBeNullOrWhiteSpace();
                }
            }
        }
    }
}