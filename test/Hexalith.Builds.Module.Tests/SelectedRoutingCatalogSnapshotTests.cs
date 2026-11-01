// <copyright file="SelectedRoutingCatalogSnapshotTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks pre-child held routing proof ownership without starting infrastructure.</summary>
public sealed class SelectedRoutingCatalogSnapshotTests
{
    /// <summary>Requires the original descriptor identity and safe bounded workspace files.</summary>
    /// <param name="scenario">The identity, source or storage mutation.</param>
    /// <returns>The bounded ownership and retained-byte assertions.</returns>
    /// <exception cref="ArgumentNullException">The scenario is absent.</exception>
    [Theory]
    [InlineData("valid")]
    [InlineData("near-limit-valid")]
    [InlineData("missing-activation")]
    [InlineData("missing-preparation")]
    [InlineData("missing-metadata")]
    [InlineData("missing-closure")]
    [InlineData("missing-host")]
    [InlineData("wrong-closure-run")]
    [InlineData("failed-preparation")]
    [InlineData("wrong-environment")]
    [InlineData("wrong-instance")]
    [InlineData("wrong-builds-commit")]
    [InlineData("file-link")]
    [InlineData("broken-file-link")]
    [InlineData("directory-link")]
    [InlineData("oversized")]
    [InlineData("aggregate-size")]
    [InlineData("too-many-hosts")]
    [InlineData("invalid-host-name")]
    [InlineData("credential")]
    [InlineData("credential-key")]
    [InlineData("credential-assignment")]
    [InlineData("credential-container-private-key-array")]
    [InlineData("credential-container-private-key-object")]
    [InlineData("credential-container-access-token-array")]
    [InlineData("credential-container-access-token-object")]
    [InlineData("credential-container-client-secret-object")]
    [InlineData("known-value")]
    [InlineData("known-key")]
    [InlineData("known-schema-value")]
    [InlineData("schema-properties")]
    [InlineData("cancelled")]
    public async Task CapturedRoutingProofRequiresTheValidatedOwnedInstanceAsync(string scenario)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        if (OperatingSystem.IsWindows() && scenario is "file-link" or "broken-file-link" or "directory-link")
        {
            return;
        }

        using NativeEndpointCredentialWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        string instance = plan.EnvironmentId + "-catalog";
        string directory = Path.Combine(plan.Workspace, "platform-components", instance);
        _ = Directory.CreateDirectory(directory);
        foreach (string name in new[] { "catalog.json", "receipt.json", "activation.json", "metadata-http.json", "preparation.json", "owner.committed.json" })
        {
            await File.WriteAllTextAsync(Path.Combine(directory, name), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        const string activation = "{ \"activation\" : \"held-original\" }\n";
        await File.WriteAllTextAsync(Path.Combine(directory, "activation.json"), activation, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "selected-closure.json"), JsonSerializer.Serialize(new { RunId = scenario == "wrong-closure-run" ? new string('f', 32) : plan.RunId, hosts = new[] { new { ResourceId = "owner", AppId = "owner" } } }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "preparation.json"), JsonSerializer.Serialize(new { plan.RunId, routingCatalogMetadataObserved = scenario != "failed-preparation" }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), "{\"method\":\"GET\",\"path\":\"/api/v1/routing/catalog\",\"status\":200}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (scenario.StartsWith("missing-", StringComparison.Ordinal))
        {
            string missing = scenario switch
            {
                "missing-activation" => "activation.json",
                "missing-preparation" => "preparation.json",
                "missing-metadata" => "metadata-http.json",
                "missing-closure" => "selected-closure.json",
                _ => "owner.committed.json",
            };
            File.Delete(Path.Combine(directory, missing));
        }

        const string canonical = """
            {
              "schemaVersion": 3, "generation": 1,
              "idempotency": [{ "secretStore": "secretstore", "logicalSecretName": "owner-key-ring",
                "secretMapGeneration": "v1", "activeDigestKeyGeneration": "v1",
                "keyMap": [{ "generation": "v1", "mapKey": "current-digest" }],
                "publicKeyId": "owner-public-v1",
                "contentDigest": "0000000000000000000000000000000000000000000000000000000000000000" }],
              "operations": [{ "requestSchema": { "type": "object", "properties": {
                "password": { "type": "string" }, "token": { "type": "string" }, "privateKey": { "type": "string" } } } }]
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog.json"), canonical, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string mapping = Path.Combine(plan.Workspace, "source-mapping");
        _ = Directory.CreateDirectory(mapping);
        await File.WriteAllTextAsync(Path.Combine(mapping, "mapping.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(mapping, "catalog-versions.json"), JsonSerializer.Serialize(new { CatalogCommit = new string(scenario == "wrong-builds-commit" ? 'b' : 'a', 40) }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string assets = Path.Combine(plan.Workspace, "platform-components", "catalog-reader", "obj");
        _ = Directory.CreateDirectory(assets);
        await File.WriteAllTextAsync(Path.Combine(assets, "project.assets.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string outside = Path.Combine(workspace.Root, "outside.json");
        await File.WriteAllTextAsync(outside, "{\"outside\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (scenario is "file-link" or "broken-file-link")
        {
            File.Delete(Path.Combine(directory, "metadata-http.json"));
            _ = File.CreateSymbolicLink(Path.Combine(directory, "metadata-http.json"), scenario == "broken-file-link" ? outside + ".missing" : outside);
        }

        if (scenario == "directory-link")
        {
            Directory.Delete(directory, recursive: true);
            string external = Path.Combine(workspace.Root, "outside-directory");
            _ = Directory.CreateDirectory(external);
            await File.WriteAllTextAsync(Path.Combine(external, "catalog.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = Directory.CreateSymbolicLink(directory, external);
        }

        if (scenario == "oversized")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), "{\"padding\":\"" + new string('x', SelectedRoutingCatalogSnapshot.MaximumFileBytes) + "\"}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario == "aggregate-size")
        {
            foreach (string name in new[] { "metadata-http.json", "preparation.json", "receipt.json", "catalog.json", "owner.committed.json" })
            {
                await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(new { padding = new string('x', 7 * 1024 * 1024) }), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }

        if (scenario == "too-many-hosts")
        {
            for (int index = 0; index < 65; index++)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, "owner-" + index + ".committed.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }

        if (scenario == "invalid-host-name")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "..committed.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario == "credential")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), JsonSerializer.Serialize(new { password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario is "credential-key" or "credential-assignment")
        {
            string material = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            Dictionary<string, string> payload = new(StringComparer.Ordinal)
            {
                [scenario == "credential-key" ? "privateKey" : "diagnostic"] = scenario == "credential-key" ? material : "password=" + material,
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), JsonSerializer.Serialize(payload), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string opaque = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        if (scenario.StartsWith("credential-container-", StringComparison.Ordinal))
        {
            string key = scenario switch
            {
                _ when scenario.Contains("private-key", StringComparison.Ordinal) => "privateKey",
                _ when scenario.Contains("access-token", StringComparison.Ordinal) => "accessToken",
                _ => "client_secret",
            };
            object payload = scenario.EndsWith("array", StringComparison.Ordinal) ? new[] { opaque } : new { material = opaque };
            await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), JsonSerializer.Serialize(new Dictionary<string, object> { [key] = payload }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario is "known-value" or "known-key" or "known-schema-value")
        {
            string content = scenario switch
            {
                "known-key" => "{\"\\u" + ((int)opaque[0]).ToString("x4", System.Globalization.CultureInfo.InvariantCulture) + opaque[1..] + "\":true}",
                "known-schema-value" => JsonSerializer.Serialize(new { requestSchema = new { properties = new { accessToken = new { type = "string", @default = opaque } } } }),
                _ => JsonSerializer.Serialize(new { observation = "prefix-" + opaque + "-suffix" }),
            };
            using JsonDocument plain = JsonDocument.Parse(content);
            SelectedMetadataSafety.IsSafe(plain.RootElement).ShouldBeTrue("The opaque run value has no credential pattern.");
            await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario == "schema-properties")
        {
            const string schemaDeclarations = """
                {"requestSchema":{"type":"object","properties":{"privateKey":{"type":"object","properties":{"bytes":{"type":"string"}}}}},
                 "resultSchema":{"type":"object","properties":{"accessToken":{"type":"array","items":{"type":"string"}},"clientSecret":{"type":"string"}}}}
                """;
            await File.WriteAllTextAsync(Path.Combine(directory, "receipt.json"), schemaDeclarations, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(new
        {
            bindings = new
            {
                run = new { environmentId = scenario == "wrong-environment" ? new string('f', 32) : plan.EnvironmentId },
                environment = new { catalog = new { instanceId = scenario == "wrong-instance" ? "another-catalog" : instance, commit = new string('a', 40) } },
            },
        });
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        if (scenario == "cancelled")
        {
            await cancellation.CancelAsync().ConfigureAwait(true);
        }

        if (scenario == "near-limit-valid")
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "metadata-http.json"), JsonSerializer.Serialize(new { method = "GET", path = "/api/v1/routing/catalog", status = 200, body = new { schema = new string('x', 4 * 1024 * 1024) } }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        if (scenario is "valid" or "near-limit-valid" or "schema-properties")
        {
            IReadOnlyDictionary<string, byte[]> captured = await SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, cancellation.Token, required: true, knownCredentials: scenario.StartsWith("known-", StringComparison.Ordinal) ? [opaque] : []).ConfigureAwait(true);
            captured.Count.ShouldBe(10);
            captured.Keys.ShouldContain("selected-closure.json");
            captured.Keys.ShouldContain("reader-assets.json");
            captured.Keys.ShouldContain("mapping.json");
            captured.Keys.ShouldContain("catalog-versions.json");
            await File.WriteAllTextAsync(Path.Combine(directory, "catalog.json"), "{\"changedByChild\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, "activation.json"), "{\"changedByChild\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            Directory.Delete(directory, recursive: true);
            System.Text.Encoding.UTF8.GetString(captured["catalog.json"]).ShouldBe(canonical);
            System.Text.Encoding.UTF8.GetString(captured["activation.json"]).ShouldBe(activation);
            captured.Keys.ShouldNotContain(NativeTestHandoff.EndpointCredentialsFileName);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            ModuleTestSelection selected = new("sample", "catalog-proof", "python3", "entry.py", new string('a', 64), new string('b', 64), 30, 5, now, now.AddSeconds(30), now.AddSeconds(31), "package");
            plan = plan with { SelectedTest = selected, ExecutionTiming = ModuleTestExecutionTiming.Start(selected, now) };
            using Process host = new();
            CompositionRunSession session = new(plan, new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://localhost:1"), null, []), Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)), host);
            NativeTestExecutionResult inspected = new(new ToolCommandResult("passed", ToolOutcome.Passed(), []), null, null) { RoutingCatalogSnapshot = captured };
            string durable = Path.Combine(workspace.Root, "durable");
            NativeTestExecutionResult archived = await NativeTestExecutor.ArchiveSelectedAsync(inspected, session, descriptor, durable, cancellation.Token).ConfigureAwait(true);
            archived.Result.Diagnostics.ShouldBeEmpty();
            string identity = Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(selected.Name)));
            foreach (KeyValuePair<string, byte[]> file in captured)
            {
                foreach (string destination in new[] { durable, Path.Combine(durable, "entries", identity), Path.Combine(plan.Workspace, "native-tests", identity) })
                {
                    (await File.ReadAllBytesAsync(Path.Combine(destination, "routing-catalog", file.Key), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(file.Value);
                }
            }
        }
        else if (scenario == "cancelled")
        {
            _ = await Should.ThrowAsync<OperationCanceledException>(() => SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, cancellation.Token, required: true, knownCredentials: scenario.StartsWith("known-", StringComparison.Ordinal) ? [opaque] : [])).ConfigureAwait(true);
        }
        else
        {
            _ = await Should.ThrowAsync<InvalidDataException>(() => SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, cancellation.Token, required: true, knownCredentials: scenario.StartsWith("known-", StringComparison.Ordinal) ? [opaque] : [])).ConfigureAwait(true);
        }

        (await File.ReadAllTextAsync(outside, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("{\"outside\":true}");
    }

    /// <summary>A catalog directory alone never enrolls an optional native run in the Parties proof requirement.</summary>
    /// <returns>The optional admission and required-profile refusal assertions.</returns>
    [Fact]
    public async Task ExistingCatalogWithoutRequiredOwnerObservationsPreservesOptionalRunsAsync()
    {
        using NativeEndpointCredentialWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        string directory = Path.Combine(plan.Workspace, "platform-components", plan.EnvironmentId + "-catalog");
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog.json"), "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await SelectedRoutingCatalogSnapshot.ReadAsync(plan, [], TestContext.Current.CancellationToken, required: false).ConfigureAwait(true)).ShouldBeEmpty();
        byte[] descriptor = JsonSerializer.SerializeToUtf8Bytes(new
        {
            bindings = new { run = new { environmentId = plan.EnvironmentId }, environment = new { catalog = new { instanceId = plan.EnvironmentId + "-catalog", commit = new string('a', 40) } } },
        });
        _ = await Should.ThrowAsync<InvalidDataException>(() => SelectedRoutingCatalogSnapshot.ReadAsync(plan, descriptor, TestContext.Current.CancellationToken, required: true)).ConfigureAwait(true);
    }

    /// <summary>Preserves the existing native handoff for runs that have no routing catalog.</summary>
    /// <returns>The unchanged empty snapshot.</returns>
    [Fact]
    public async Task ACompositionWithoutARoutingCatalogAddsNoSnapshotsAsync()
    {
        using NativeEndpointCredentialWorkspace workspace = new();
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root);
        IReadOnlyDictionary<string, byte[]> captured = await SelectedRoutingCatalogSnapshot.ReadAsync(plan, [], TestContext.Current.CancellationToken).ConfigureAwait(true);
        captured.ShouldBeEmpty();
        _ = await Should.ThrowAsync<InvalidDataException>(() => SelectedRoutingCatalogSnapshot.ReadAsync(plan, [], TestContext.Current.CancellationToken, required: true)).ConfigureAwait(true);
    }
}
