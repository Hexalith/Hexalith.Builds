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
            ModuleTestSelection active = plan.SelectedTest;
            plan = plan with { SelectedTest = active with { RequestedAtUtc = active.RequestedAtUtc.AddHours(-2), ExecutionDeadlineUtc = active.ExecutionDeadlineUtc.AddHours(-2), CredentialExpiryUtc = active.CredentialExpiryUtc.AddHours(-2) }, ExecutionTiming = ModuleTestExecutionTiming.Start(active, active.RequestedAtUtc) };
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
                enabled.RootElement.GetProperty("expiresUtcMs").GetInt64().ShouldBeLessThanOrEqualTo(plan.ExecutionTiming.DeadlineUtc.ToUnixTimeMilliseconds());
            }

            await owner.CancelAsync().ConfigureAwait(true);
            gate.Token.IsCancellationRequested.ShouldBeTrue();
            using JsonDocument immediate = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
            immediate.RootElement.GetProperty("state").GetString().ShouldBe("disabled");
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

    /// <summary>Advances renewal UTC explicitly and checks foreign ownership or expiry stops renewal without overwriting it.</summary>
    /// <param name="foreign">Whether the final tick sees foreign ownership instead of absolute expiry.</param>
    /// <returns>The controlled renewal assertions.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RenewalAdvancesCapsAndFailsClosedAsync(bool foreign)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            AdjustableClock clock = new();
            DateTimeOffset start = clock.GetUtcNow();
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { SelectedTest = new("memories", "functional-retention", "python3", "entry.py", new string('a', 64), new string('b', 64), 1620, 900, start, start.AddMinutes(27), start.AddMinutes(27), "source") };
            ModuleTestSelection active = plan.SelectedTest;
            plan = plan with { SelectedTest = active with { RequestedAtUtc = active.RequestedAtUtc.AddHours(-2), ExecutionDeadlineUtc = active.ExecutionDeadlineUtc.AddHours(-2), CredentialExpiryUtc = active.CredentialExpiryUtc.AddHours(-2) }, ExecutionTiming = ModuleTestExecutionTiming.Start(active, active.RequestedAtUtc) };
            string path = Path.Combine(plan.Workspace, "platform-components", "retention", "gate.json");
            CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "{\"schemaVersion\":1,\"state\":\"disabled\",\"profileSha256\":\"\",\"expiresUtcMs\":0}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionTestObservation observation = new() { RunId = plan.RunId, ModuleId = "memories", ProfileHash = new string('a', 64), ProfileJson = "offline", ObservedCandidateHash = "offline", ObservedRuntimeHash = "offline", ObservedBackendIdentity = "offline", RuntimeArtifactVerified = true, LoadedArtifactHashes = new Dictionary<string, string> { ["offline"] = "offline" }, ServiceRetentionSeconds = new Dictionary<string, int> { ["a"] = 3600, ["b"] = 3600, ["c"] = 3600 }, Endpoints = Enumerable.Range(0, 6).ToDictionary(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => new Uri("http://localhost:1234")), PostgresqlContainerId = new string('a', 64), GatePath = path };
            using Process host = new();
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://localhost:1234"), null, []) { TestObservation = observation };
            using SemaphoreSlim ticks = new(0);
            CompositionSelectedTestGate gate = new(new CompositionRunSession(plan, readiness, "offline", host), TestContext.Current.CancellationToken, clock, ticks.WaitAsync);
            gate.Start();
            clock.Now = start.AddMinutes(5);
            _ = ticks.Release();
            await UntilAsync(() => Expiry(path) == start.AddMinutes(15).ToUnixTimeMilliseconds()).ConfigureAwait(true);
            foreach (int minutes in new[] { 10, 15, 20 })
            {
                clock.Now = start.AddMinutes(minutes);
                _ = ticks.Release();
                long expected = Math.Min(start.AddMinutes(minutes + 10).ToUnixTimeMilliseconds(), plan.ExecutionTiming.DeadlineUtc.ToUnixTimeMilliseconds());
                await UntilAsync(() => Expiry(path) == expected).ConfigureAwait(true);
            }

            string? foreignBytes = null;
            if (foreign)
            {
                foreignBytes = "{\"schemaVersion\":1,\"state\":\"enabled\",\"profileSha256\":\"foreign\",\"expiresUtcMs\":1}";
                await File.WriteAllTextAsync(path, foreignBytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            else
            {
                clock.Now = start.AddMinutes(27);
            }

            _ = ticks.Release();
            await UntilAsync(() => gate.Token.IsCancellationRequested).ConfigureAwait(true);
            gate.Failed.ShouldBeTrue();
            await gate.DisposeAsync().ConfigureAwait(true);
            if (foreign)
            {
                (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(foreignBytes);
            }
            else
            {
                Expiry(path).ShouldBe(0);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Exact handoff credentials are forbidden in JSON names as well as values.</summary>
    /// <returns>The custody refusal assertion.</returns>
    [Fact]
    public async Task ObservationPropertyNamesCannotRetainRandomCredentialAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            string path = Path.Combine(root, "functional-observations.json");
            Dictionary<string, object> details = new()
            {
                ["runId"] = "owned-run",
                ["status"] = "functional-observations-passed",
                ["qualificationCredit"] = false,
                ["safe-prefix-" + token + "-safe-suffix"] = "ordinary-value",
            };
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize(details), TestContext.Current.CancellationToken).ConfigureAwait(true);
            (await SelectedNativeTestReport.ReadObservationsAsync(path, root, "owned-run", [token], TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBeNull();
            File.Exists(path).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Malformed, expired, withdrawn and non-regular authority cancels at a controlled renewal tick without reopening it.</summary>
    /// <param name="replacement">The authority replacement after opening.</param>
    /// <returns>The cancellation and preservation assertions.</returns>
    [Theory]
    [InlineData("expired")]
    [InlineData("schema-overflow")]
    [InlineData("expiry-overflow")]
    [InlineData("expiry-string")]
    [InlineData("expiry-missing")]
    [InlineData("negative")]
    [InlineData("extended")]
    [InlineData("disabled")]
    [InlineData("malformed")]
    [InlineData("fifo")]
    public async Task RenewalRefusesReplacedAuthorityWithoutReopeningAsync(string replacement)
    {
        if (replacement == "fifo" && OperatingSystem.IsWindows())
        {
            Assert.Skip("mkfifo is POSIX-only.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            AdjustableClock clock = new();
            DateTimeOffset start = clock.GetUtcNow();
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with { SelectedTest = new("memories", "functional-retention", "python3", "entry.py", new string('a', 64), new string('b', 64), 3600, 900, start, start.AddHours(1), start.AddHours(1), "source") };
            ModuleTestSelection active = plan.SelectedTest;
            plan = plan with { SelectedTest = active with { RequestedAtUtc = active.RequestedAtUtc.AddHours(-2), ExecutionDeadlineUtc = active.ExecutionDeadlineUtc.AddHours(-2), CredentialExpiryUtc = active.CredentialExpiryUtc.AddHours(-2) }, ExecutionTiming = ModuleTestExecutionTiming.Start(active, active.RequestedAtUtc) };
            string path = Path.Combine(plan.Workspace, "platform-components", "retention", "gate.json");
            CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(path)!);
            const string disabled = "{\"schemaVersion\":1,\"state\":\"disabled\",\"profileSha256\":\"\",\"expiresUtcMs\":0}";
            await File.WriteAllTextAsync(path, disabled, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionTestObservation observation = new() { RunId = plan.RunId, ModuleId = "memories", ProfileHash = new string('a', 64), ProfileJson = "offline", ObservedCandidateHash = "offline", ObservedRuntimeHash = "offline", ObservedBackendIdentity = "offline", RuntimeArtifactVerified = true, LoadedArtifactHashes = new Dictionary<string, string> { ["offline"] = "offline" }, ServiceRetentionSeconds = new Dictionary<string, int> { ["a"] = 3600, ["b"] = 3600, ["c"] = 3600 }, Endpoints = Enumerable.Range(0, 6).ToDictionary(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), _ => new Uri("http://localhost:1234")), PostgresqlContainerId = new string('a', 64), GatePath = path };
            using Process host = new();
            CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://localhost:1234"), null, []) { TestObservation = observation };
            using SemaphoreSlim ticks = new(0);
            CompositionSelectedTestGate gate = new(new CompositionRunSession(plan, readiness, "offline", host), TestContext.Current.CancellationToken, clock, ticks.WaitAsync);
            gate.Start();
            clock.Now = start.AddMinutes(5);
            string authority = JsonSerializer.Serialize(new { schemaVersion = 1, state = "enabled", profileSha256 = observation.ProfileHash, expiresUtcMs = start.AddMinutes(10).ToUnixTimeMilliseconds() });
            authority = replacement switch
            {
                "expired" => authority.Replace(start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), clock.Now.AddSeconds(-1).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "schema-overflow" => authority.Replace("\"schemaVersion\":1", "\"schemaVersion\":9223372036854775808", StringComparison.Ordinal),
                "expiry-overflow" => authority.Replace(start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), "9223372036854775808", StringComparison.Ordinal),
                "expiry-string" => authority.Replace(start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), "\"forever\"", StringComparison.Ordinal),
                "expiry-missing" => authority.Replace(",\"expiresUtcMs\":" + start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), string.Empty, StringComparison.Ordinal),
                "negative" => authority.Replace(start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), "-1", StringComparison.Ordinal),
                "extended" => authority.Replace(start.AddMinutes(10).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), start.AddMinutes(30).ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal),
                "disabled" => disabled,
                "malformed" => "{",
                _ => authority,
            };
            if (replacement == "fifo")
            {
                File.Delete(path);
                using Process fifo = Process.Start(new ProcessStartInfo("mkfifo", [path]) { UseShellExecute = false })!;
                await fifo.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                fifo.ExitCode.ShouldBe(0);
            }
            else
            {
                await File.WriteAllTextAsync(path, authority, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            _ = ticks.Release();
            await UntilAsync(() => gate.Token.IsCancellationRequested).ConfigureAwait(true);
            gate.Failed.ShouldBeTrue();
            await gate.DisposeAsync().ConfigureAwait(true);
            if (replacement == "fifo")
            {
                FileStream held = SelectedNativeTestReport.OpenWithoutBlocking(path);
                await using (held.ConfigureAwait(true))
                {
                    held.CanSeek.ShouldBeFalse();
                }
            }
            else if (replacement is "expired" or "extended")
            {
                Expiry(path).ShouldBe(0);
            }
            else
            {
                (await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(authority);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Internal renewal failure retains resources; owner withdrawal retains its original cancelled cause.</summary>
    [Fact]
    public void NativeInterruptionDistinguishesOwnerFromGateFailure()
    {
        NativeTestExecutionResult internalFailure = NativeTestExecutor.SelectedInterruption(false);
        internalFailure.Result.Status.ShouldBe("failed");
        ((int)internalFailure.Result.Outcome.ExitCode).ShouldBe(3);
        internalFailure.Result.Outcome.RuleId.ShouldBe("HXT009");
        NativeTestExecutionResult withdrawn = NativeTestExecutor.SelectedInterruption(true);
        withdrawn.Result.Status.ShouldBe("withdrawn");
        ((int)withdrawn.Result.Outcome.ExitCode).ShouldBe(130);
        withdrawn.Result.Outcome.RuleId.ShouldBe("HXC130");
    }

    private static long Expiry(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.GetProperty("expiresUtcMs").GetInt64();
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        condition().ShouldBeTrue();
    }

    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class AdjustableClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}