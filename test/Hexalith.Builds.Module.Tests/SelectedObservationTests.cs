// <copyright file="SelectedObservationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;

using Shouldly;

using Xunit;

/// <summary>Offline contract checks; fixture identities provide no running-service evidence.</summary>
public sealed class SelectedObservationTests
{
    /// <summary>Checks exact OCI content linkage, substitution and missing layers without Docker.</summary>
    [Fact]
    public void ImmutableRuntimeRequiresMatchingImageLayersAndNativeBytes()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            byte[] executable = "offline fixture binary"u8.ToArray();
            byte[] layer;
            using (MemoryStream content = new())
            {
                using (TarWriter writer = new(content, leaveOpen: true))
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "daprd") { DataStream = new MemoryStream(executable) });
                }

                layer = content.ToArray();
            }

            string layerHash = Hash(layer);
            byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new { layers = new[] { new { digest = "sha256:" + layerHash } } });
            string manifestHash = Hash(manifest);
            byte[] index = JsonSerializer.SerializeToUtf8Bytes(new { manifests = new[] { new { digest = "sha256:" + manifestHash, platform = new { os = "linux", architecture = "amd64" } } } });
            string indexHash = Hash(index);
            string archive = Path.Combine(root, "runtime.oci.tar");
            using (FileStream output = File.Create(archive))
            using (TarWriter writer = new(output))
            {
                foreach (byte[] bytes in new[] { layer, manifest, index })
                {
                    writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "blobs/sha256/" + Hash(bytes)) { DataStream = new MemoryStream(bytes) });
                }
            }

            string binary = Path.Combine(root, "daprd");
            File.WriteAllBytes(binary, executable);
            CompositionRuntimeArtifactVerifier.Verify(archive, indexHash, manifestHash, binary).ShouldBeTrue();
            File.WriteAllText(binary, "substituted runtime");
            CompositionRuntimeArtifactVerifier.Verify(archive, indexHash, manifestHash, binary).ShouldBeFalse();
            File.WriteAllBytes(binary, executable);
            CompositionRuntimeArtifactVerifier.Verify(archive, new string('f', 64), manifestHash, binary).ShouldBeFalse();
            CompositionRuntimeArtifactVerifier.Verify(archive, indexHash, new string('f', 64), binary).ShouldBeFalse();
            CompositionRuntimeArtifactVerifier.Verify(Path.Combine(root, "missing.tar"), indexHash, manifestHash, binary).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Checks short gate limits, immutable expiry and immediate owner withdrawal cleanup.</summary>
    /// <returns>The offline gate assertions.</returns>
    [Fact]
    public async Task SelectedGateClosesOnOwnerWithdrawalWithoutExtendingDeadlineAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                SelectedTest = new("memories", "functional-retention", "python3", "tools/verify-access-telemetry-lifecycle.py", new string('a', 64), new string('b', 64), 14400, 900, now, now.AddMinutes(7), now.AddMinutes(7), "source"),
            };
            string path = Path.Combine(plan.Workspace, "platform-components", "retention", "gate.json");
            CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"state\":\"disabled\",\"profileSha256\":\"\",\"expiresUtcMs\":0}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionTestObservation observation = new()
            {
                RunId = plan.RunId,
                ModuleId = "memories",
                ProfileHash = new string('a', 64),
                ProfileJson = "offline fixture",
                ObservedCandidateHash = new string('a', 64),
                ObservedBackendIdentity = "offline fixture",
                ObservedRuntimeHash = new string('d', 64),
                RuntimeArtifactVerified = true,
                LoadedArtifactHashes = new Dictionary<string, string> { ["offline"] = new string('a', 64) },
                ServiceRetentionSeconds = new Dictionary<string, int> { ["server"] = 3600, ["clock"] = 3600, ["telemetry"] = 3600 },
                Endpoints = Enumerable.Range(0, 6).ToDictionary(index => index.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => new Uri("http://127.0.0.1:1234/")),
                PostgresqlContainerId = new string('c', 64),
                GatePath = path,
            };
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:1234/"), null, []) { TestObservation = observation };
            using Process host = new();
            using CancellationTokenSource owner = new();
            CompositionSelectedTestGate gate = new(new CompositionRunSession(plan, readiness, "offline-fixture", host), owner.Token);
            gate.Start();
            using (JsonDocument enabled = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)))
            {
                enabled.RootElement.GetProperty("state").GetString().ShouldBe("enabled");
                enabled.RootElement.GetProperty("expiresUtcMs").GetInt64().ShouldBeLessThanOrEqualTo(plan.SelectedTest.ExecutionDeadlineUtc.ToUnixTimeMilliseconds());
            }

            await owner.CancelAsync().ConfigureAwait(true);
            gate.Token.IsCancellationRequested.ShouldBeTrue();
            await gate.DisposeAsync().ConfigureAwait(true);
            using JsonDocument disabled = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
            disabled.RootElement.GetProperty("state").GetString().ShouldBe("disabled");
            disabled.RootElement.GetProperty("expiresUtcMs").GetInt64().ShouldBe(0);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Rejects newly handed-off credentials in passing, failing and cancelled-child diagnostics.</summary>
    /// <param name="outcome">The child report outcome.</param>
    /// <param name="tokenName">The exact private credential name.</param>
    /// <returns>The bounded report assertions.</returns>
    [Theory]
    [InlineData("Passed", "APP_API_TOKEN")]
    [InlineData("Failed", "DAPR_API_TOKEN")]
    [InlineData("Aborted", "APP_API_TOKEN")]
    public async Task AccessCredentialsCannotSurviveAnyReportOutcomeAsync(string outcome, string tokenName)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string access = Path.Combine(root, "platform-components", "retention", "access.json");
            CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(access)!);
            await File.WriteAllTextAsync(access, JsonSerializer.Serialize(Enumerable.Range(0, 3).ToDictionary(index => "service" + index, _ => new { APP_API_TOKEN = "fixture-app-credential", DAPR_API_TOKEN = "fixture-dapr-credential" })), TestContext.Current.CancellationToken).ConfigureAwait(true);
            IReadOnlyList<string> secrets = (await SelectedNativeTestReport.ReadAccessSecretsAsync(access, root, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            string path = Path.Combine(root, "native.trx");
            string secret = tokenName == "APP_API_TOKEN" ? "fixture-app-credential" : "fixture-dapr-credential";
            await File.WriteAllTextAsync(path, "<TestRun><UnitTestResult outcome=\"" + outcome + "\"><Output><StdOut>" + secret + "</StdOut></Output></UnitTestResult></TestRun>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            (NativeTestReportLoadResult report, byte[]? bytes) = await SelectedNativeTestReport.ReadAsync(path, root, secrets, TestContext.Current.CancellationToken).ConfigureAwait(true);
            report.Diagnostic.ShouldNotBeNull().RuleId.ShouldBe("HXT008");
            bytes.ShouldBeNull();
            File.Exists(path).ShouldBeFalse();
            (await File.ReadAllTextAsync(access, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldContain(secret);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}