// <copyright file="PackagedPersistedProfileTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Qualifies the installed tool against the real two-module persisted fixture and its native test reports.
/// </summary>
[Collection(LiveLane.Name)]
public sealed class PackagedPersistedProfileTests
{
    /// <summary>
    /// Runs the installed command and checks its retained, metadata-only persisted assertions and native report binding.
    /// </summary>
    /// <param name="profile">The persisted profile.</param>
    /// <param name="platform">The native test platform the profile declares.</param>
    /// <returns>A task that completes after the packaged profile and cleanup.</returns>
    /// <exception cref="InvalidOperationException">The restored package consumer is not configured.</exception>
    [Theory]
    [InlineData("full", "vstest")]
    [InlineData("full-mtp", "mtp")]
    public async Task InstalledToolPassesPersistedProfileWithNativeReportAsync(string profile, string platform)
    {
        LiveGate.SkipUnlessEnabled();
        string consumer = Environment.GetEnvironmentVariable("HEXALITH_G4_PACKAGE_CONSUMER")
            ?? throw new InvalidOperationException("Set HEXALITH_G4_PACKAGE_CONSUMER to the restored local-tool directory.");
        Directory.Exists(consumer).ShouldBeTrue();
        string evidenceDirectory = Environment.GetEnvironmentVariable("HEXALITH_G4_PACKAGED_EVIDENCE_DIRECTORY") ?? "artifacts/g4-packaged-evidence";
        string evidencePath = evidenceDirectory.TrimEnd('/') + "/packaged-" + profile + "-" + Guid.NewGuid().ToString("N") + ".json";
        string reportPath = evidencePath[..^".json".Length] + "." + platform + ".trx";
        string[] arguments =
        [
            "tool", "run", "hexalith-module", "--", "test",
            "--manifest", LiveRepository.Manifest,
            "--profile", profile, "--evidence", evidencePath, "--output", "json",
        ];
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [CompositionPrerequisiteProbe.DaprHomeVariable] = Environment.GetEnvironmentVariable(CompositionPrerequisiteProbe.DaprHomeVariable)!,
        };

        // Longer than the runner's 15-minute native test timeout so a hung native step still reaches teardown.
        CompositionProcessResult process = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", arguments, consumer, environment),
            TimeSpan.FromMinutes(30),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        process.Started.ShouldBeTrue();
        process.ExitCode.ShouldBe(0, process.Output);
        using JsonDocument result = JsonDocument.Parse(process.Output);
        result.RootElement.GetProperty("status").GetString().ShouldBe("completed");
        string runId = result.RootElement.GetProperty("runId").GetString()!;
        string fullEvidencePath = Path.Combine(LiveRepository.Root, evidencePath);
        string fullReportPath = Path.Combine(LiveRepository.Root, reportPath);
        File.Exists(fullEvidencePath).ShouldBeTrue();
        File.Exists(fullReportPath).ShouldBeTrue();
        using JsonDocument evidence = JsonDocument.Parse(await File.ReadAllTextAsync(fullEvidencePath, TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement root = evidence.RootElement;
        root.GetProperty("finalStatus").GetString().ShouldBe("completed");
        root.GetProperty("runId").GetString().ShouldBe(runId);
        root.GetProperty("topology").GetProperty("modules").GetArrayLength().ShouldBe(2);
        root.GetProperty("persistedAssertions").GetArrayLength().ShouldBe(12);
        root.GetProperty("expectedSequences").GetArrayLength().ShouldBe(2);
        JsonElement counts = root.GetProperty("testCounts");
        counts.GetProperty("reported").GetBoolean().ShouldBeTrue();
        counts.GetProperty("total").GetInt32().ShouldBe(2);
        counts.GetProperty("passed").GetInt32().ShouldBe(2);
        counts.GetProperty("failed").GetInt32().ShouldBe(0);
        byte[] report = await File.ReadAllBytesAsync(fullReportPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        root.GetProperty("artifactHashes").GetProperty(reportPath).GetString().ShouldBe(Convert.ToHexString(SHA256.HashData(report)));
        CompositionEngine engine = new(new CompositionEngineOptions(LiveRepository.AppHostAssembly, LiveRepository.DescriptorChildAssembly));
        (await engine.Scanner.FindAsync(runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
        File.Exists(engine.StateStore.PathFor(runId)).ShouldBeFalse();
        TestContext.Current.TestOutputHelper?.WriteLine("G4_RUN_ID=" + runId);
    }
}