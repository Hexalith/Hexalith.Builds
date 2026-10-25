// <copyright file="SelectedCleanupAcceptanceTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Evidence;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Acceptance requires verified selected-environment cleanup even when the first test outcome passed.</summary>
public sealed class SelectedCleanupAcceptanceTests
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter<ToolPhase>(), new JsonStringEnumConverter<ToolFailureCategory>(), new JsonStringEnumConverter<ToolExitCode>() },
    };

    /// <summary>Both acceptance consumers reject a reader-valid completed run unless its owned resources were removed.</summary>
    /// <param name="scenario">The observed cleanup state.</param>
    /// <returns>The schema-valid local evidence and actual acceptance decisions.</returns>
    [Theory]
    [InlineData("removed")]
    [InlineData("unknown")]
    [InlineData("workspace")]
    [InlineData("unverified")]
    public async Task CompletedSelectedEvidenceRequiresVerifiedCleanupAsync(string scenario)
    {
        string root = Path.Combine(Path.GetTempPath(), "hexalith-selected-acceptance-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, ".git"), "fixture repository marker", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string fixtures = Path.GetDirectoryName(EvidenceFixturePath.Get("positive"))!;
            CopyDirectory(fixtures, Path.Combine(root, "test", "fixtures", "evidence"));
            string moduleFixtures = Path.Combine(Path.GetDirectoryName(fixtures)!, "module");
            CopyDirectory(Path.Combine(moduleFixtures, "positive"), Path.Combine(root, "test", "fixtures", "module", "positive"));
            CopyDirectory(Path.Combine(moduleFixtures, "descriptors"), Path.Combine(root, "test", "fixtures", "module", "descriptors"));
            CopyDirectory(Path.Combine(moduleFixtures, "profiles"), Path.Combine(root, "test", "fixtures", "module", "profiles"));
            string acceptancePath = Path.Combine(root, "test", "fixtures", "evidence", "acceptance", "positive", "p0-acceptance.json");
            string readinessPath = Path.Combine(root, "test", "fixtures", "evidence", "positive", "readiness.yaml");
            (await G4P0AcceptanceValidator.ValidateAsync(acceptancePath, TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            (await ReadinessEvidenceValidator.ValidateAsync(readinessPath, TestContext.Current.CancellationToken).ConfigureAwait(true)).Outcome.ExitCode.ShouldBe(ToolExitCode.Success);

            JsonNode acceptance = JsonNode.Parse(await File.ReadAllTextAsync(acceptancePath, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
            JsonNode run = acceptance["runs"]![0]!;
            string boundArtifact = Path.Combine(root, run["evidencePath"]!.GetValue<string>());
            byte[] boundBytes = AttachSelected(await File.ReadAllBytesAsync(boundArtifact, TestContext.Current.CancellationToken).ConfigureAwait(true), scenario);
            await File.WriteAllBytesAsync(boundArtifact, boundBytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            run["evidenceSha256"] = Convert.ToHexString(SHA256.HashData(boundBytes));
            await File.WriteAllTextAsync(acceptancePath, acceptance.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);

            string readinessArtifact = Path.Combine(root, "test", "fixtures", "evidence", "positive", "evidence", "release-passed.json");
            byte[] original = await File.ReadAllBytesAsync(readinessArtifact, TestContext.Current.CancellationToken).ConfigureAwait(true);
            byte[] readinessBytes = AttachSelected(original, scenario);
            await File.WriteAllBytesAsync(readinessArtifact, readinessBytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string readiness = await File.ReadAllTextAsync(readinessPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            readiness = readiness.Replace(Convert.ToHexString(SHA256.HashData(original)), Convert.ToHexString(SHA256.HashData(readinessBytes)), StringComparison.OrdinalIgnoreCase);
            await File.WriteAllTextAsync(readinessPath, readiness, TestContext.Current.CancellationToken).ConfigureAwait(true);

            ToolCommandResult accepted = await G4P0AcceptanceValidator.ValidateAsync(acceptancePath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            ToolCommandResult ready = await ReadinessEvidenceValidator.ValidateAsync(readinessPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (scenario == "removed")
            {
                accepted.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(accepted));
                ready.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, JsonSerializer.Serialize(ready));
            }
            else
            {
                accepted.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXE205");
                ToolDiagnostic cleanup = ready.Diagnostics.First(diagnostic => diagnostic.RuleId == "HXE149");
                cleanup.Message.ShouldContain("for a selected run, verified environment cleanup");
                cleanup.Hint.ShouldNotBeNull().ShouldContain("verified cleanup");
                cleanup.Hint.ShouldContain("removed resources and an empty remainder");
            }
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static byte[] AttachSelected(byte[] bytes, string scenario)
    {
        JsonNode artifact = JsonNode.Parse(bytes)!;
        JsonNode invocation = artifact["invocation"]!;
        string runId = artifact["runId"]!.GetValue<string>();
        DateTimeOffset requestedAt = artifact["timestamps"]!["startedUtc"]!.GetValue<DateTimeOffset>();
        string reportHash = artifact["artifactHashes"]!.AsObject().FirstOrDefault().Value?.GetValue<string>() ?? new string('A', 64);
        artifact["artifactHashes"]!["native.trx"] = reportHash;
        JsonNode counts = artifact["testCounts"]!;
        ModuleTestSelection selection = new("module-a", invocation["profile"]!.GetValue<string>(), "vstest", "test/Native.csproj", invocation["manifestHash"]!.GetValue<string>(), new string('B', 64), 600, 5, requestedAt, requestedAt.AddSeconds(600), requestedAt.AddSeconds(600), "source");
        CompositionRunResources remainder = new([], [], scenario == "unverified") { WorkspaceIds = scenario == "workspace" ? ["workspace:" + runId] : [] };
        ModuleTestRunRecord selected = new(runId, runId, selection, null, null, null, null, null, false, [], "completed", ToolOutcome.Passed(), artifact["timestamps"]!["completedUtc"]!.GetValue<DateTimeOffset>(), scenario == "removed" ? "removed" : "unknown", remainder)
        {
            ReportHash = reportHash,
            TestCounts = new ModuleRunTestCounts(true, counts["total"]!.GetValue<int>(), counts["passed"]!.GetValue<int>(), 0, 0),
            CleanupDisposition = scenario == "removed" ? "complete" : "incomplete",
        };
        ModuleRunEvidence evidence = artifact.Deserialize<ModuleRunEvidence>(_jsonOptions).ShouldNotBeNull();
        byte[] amended = ModuleRunEvidenceWriter.SerializeCanonical(evidence with { SelectedTestRun = selected });
        ModuleRunEvidenceArtifactValidator.TryValidate(amended, out ModuleRunEvidenceArtifactSummary? summary).ShouldBeTrue(Encoding.UTF8.GetString(amended));
        summary.ShouldNotBeNull().CleanupVerified.ShouldBe(scenario == "removed");
        return amended;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, path));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(path, target);
        }
    }
}
