// <copyright file="ModuleRunEvidenceWriter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Writes canonical, metadata-only module-run evidence without retaining partial artifacts.
/// </summary>
public static class ModuleRunEvidenceWriter
{
    /// <summary>
    /// Atomically writes a canonical evidence document to a repository-relative path.
    /// </summary>
    /// <param name="evidencePath">The requested repository-relative JSON artifact path.</param>
    /// <param name="manifestPath">The manifest path used to resolve the repository root.</param>
    /// <param name="evidence">The evidence document to write.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The metadata-only write outcome.</returns>
    public static async Task<ModuleRunEvidenceWriteResult> WriteAsync(
        string evidencePath,
        string manifestPath,
        ModuleRunEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(evidence);

        string? targetPath = ResolveEvidencePath(evidencePath, manifestPath);
        if (targetPath is null)
        {
            return ModuleRunEvidenceWriteResult.Failed();
        }

        string temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            targetPath = ResolveEvidencePath(evidencePath, manifestPath);
            if (targetPath is null)
            {
                return ModuleRunEvidenceWriteResult.Failed();
            }

            temporaryPath = $"{targetPath}.{Guid.NewGuid():N}.tmp";
            byte[] content = SerializeCanonical(evidence);
            await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, targetPath, true);
            return ModuleRunEvidenceWriteResult.Passed();
        }
        catch (IOException)
        {
            DeleteTemporaryFile(temporaryPath);
            return ModuleRunEvidenceWriteResult.Failed();
        }
        catch (UnauthorizedAccessException)
        {
            DeleteTemporaryFile(temporaryPath);
            return ModuleRunEvidenceWriteResult.Failed();
        }
        catch (ArgumentException)
        {
            DeleteTemporaryFile(temporaryPath);
            return ModuleRunEvidenceWriteResult.Failed();
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryFile(temporaryPath);
            throw;
        }
    }

    /// <summary>
    /// Serializes evidence to canonical UTF-8 JSON with a single final newline.
    /// </summary>
    /// <param name="evidence">The evidence document to serialize.</param>
    /// <returns>The canonical UTF-8 artifact bytes.</returns>
    public static byte[] SerializeCanonical(ModuleRunEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);

        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            WriteEvidence(writer, evidence);
        }

        byte[] payload = stream.ToArray();
        byte[] canonicalPayload = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, canonicalPayload, 0, payload.Length);
        canonicalPayload[^1] = (byte)'\n';
        return canonicalPayload;
    }

    private static void DeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch (IOException)
        {
            // A failed cleanup cannot make retained evidence appear valid.
        }
        catch (UnauthorizedAccessException)
        {
            // A failed cleanup cannot make retained evidence appear valid.
        }
    }

    private static string? ResolveEvidencePath(string evidencePath, string manifestPath)
    {
        if (Path.IsPathRooted(evidencePath) ||
            evidencePath.Contains('\\', StringComparison.Ordinal) ||
            ManifestPathValidator.ContainsPlaceholder(evidencePath) ||
            ManifestSecretDetector.ContainsSecret(evidencePath) ||
            !evidencePath.EndsWith(".json", StringComparison.Ordinal))
        {
            return null;
        }

        string[] segments = evidencePath.Split('/', StringSplitOptions.None);
        if (segments.Any(segment =>
            string.IsNullOrWhiteSpace(segment) ||
            string.Equals(segment, ".", StringComparison.Ordinal) ||
            string.Equals(segment, "..", StringComparison.Ordinal)))
        {
            return null;
        }

        try
        {
            string fullManifestPath = Path.GetFullPath(manifestPath);
            string repositoryRoot = ManifestPathValidator.FindRepositoryRoot(fullManifestPath);
            return RepositoryPathResolver.TryResolvePathWithinRoot(repositoryRoot, evidencePath, out string targetPath)
                ? targetPath
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static void WriteEvidence(Utf8JsonWriter writer, ModuleRunEvidence evidence)
    {
        writer.WriteStartObject();
        writer.WriteString("schema", evidence.Schema);
        writer.WriteString("runId", evidence.RunId);
        WriteTimestamps(writer, evidence.Timestamps);
        WriteEnvironment(writer, evidence.Environment);
        WriteInvocation(writer, evidence.Invocation);
        WriteTopology(writer, evidence.Topology);
        WritePhaseOutcomes(writer, evidence.PhaseOutcomes);
        WriteTestCounts(writer, evidence.TestCounts);
        writer.WritePropertyName("artifactHashes");
        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> artifact in evidence.ArtifactHashes.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(artifact.Key, artifact.Value);
        }

        writer.WriteEndObject();
        writer.WriteString("finalStatus", evidence.FinalStatus);
        writer.WritePropertyName("outcome");
        WriteOutcome(writer, evidence.Outcome);
        writer.WritePropertyName("volatileFields");
        writer.WriteStartArray();
        foreach (string field in evidence.VolatileFields.OrderBy(field => field, StringComparer.Ordinal))
        {
            writer.WriteStringValue(field);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEnvironment(Utf8JsonWriter writer, ModuleRunEnvironment environment)
    {
        writer.WritePropertyName("environment");
        writer.WriteStartObject();
        writer.WriteString("repositoryRevision", environment.RepositoryRevision);
        writer.WriteString("repositoryDirtyMarker", environment.RepositoryDirtyMarker);
        writer.WriteString("sdkVersion", environment.SdkVersion);
        writer.WriteString("operatingSystem", environment.OperatingSystem);
        writer.WriteString("toolVersion", environment.ToolVersion);
        writer.WriteEndObject();
    }

    private static void WriteInvocation(Utf8JsonWriter writer, ModuleRunInvocation invocation)
    {
        writer.WritePropertyName("invocation");
        writer.WriteStartObject();
        writer.WriteString("command", invocation.Command);
        writer.WriteString("manifestPath", invocation.ManifestPath);
        writer.WriteString("manifestHash", invocation.ManifestHash);
        writer.WriteString("profile", invocation.Profile);
        writer.WriteString("fixturePath", invocation.FixturePath);
        writer.WriteString("fixtureHash", invocation.FixtureHash);
        writer.WriteString("filterHash", invocation.FilterHash);
        writer.WriteEndObject();
    }

    private static void WriteOutcome(Utf8JsonWriter writer, ToolOutcome outcome)
    {
        writer.WriteStartObject();
        writer.WriteNumber("exitCode", (int)outcome.ExitCode);
        writer.WriteString("phase", outcome.Phase.ToString());
        writer.WriteString("category", outcome.Category.ToString());
        writer.WriteString("ruleId", outcome.RuleId);
        writer.WriteEndObject();
    }

    private static void WritePhaseOutcomes(Utf8JsonWriter writer, IReadOnlyList<ModuleRunPhaseOutcome> phaseOutcomes)
    {
        writer.WritePropertyName("phaseOutcomes");
        writer.WriteStartArray();
        foreach (ModuleRunPhaseOutcome phaseOutcome in phaseOutcomes)
        {
            writer.WriteStartObject();
            writer.WriteString("phase", phaseOutcome.Phase.ToString());
            writer.WriteString("category", phaseOutcome.Category.ToString());
            writer.WriteString("ruleId", phaseOutcome.RuleId);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteTestCounts(Utf8JsonWriter writer, ModuleRunTestCounts testCounts)
    {
        writer.WritePropertyName("testCounts");
        writer.WriteStartObject();
        writer.WriteBoolean("reported", testCounts.Reported);
        writer.WriteNumber("total", testCounts.Total);
        writer.WriteNumber("passed", testCounts.Passed);
        writer.WriteNumber("failed", testCounts.Failed);
        writer.WriteNumber("skipped", testCounts.Skipped);
        writer.WriteEndObject();
    }

    private static void WriteTimestamps(Utf8JsonWriter writer, ModuleRunTimestamps timestamps)
    {
        writer.WritePropertyName("timestamps");
        writer.WriteStartObject();
        writer.WriteString("startedUtc", timestamps.StartedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("completedUtc", timestamps.CompletedUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        writer.WriteEndObject();
    }

    private static void WriteTopology(Utf8JsonWriter writer, ModuleRunTopology topology)
    {
        writer.WritePropertyName("topology");
        writer.WriteStartObject();
        writer.WritePropertyName("modules");
        writer.WriteStartArray();
        foreach (ModuleRunModule module in topology.Modules.OrderBy(module => module.Id, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("id", module.Id);
            writer.WriteString("domain", module.Domain);
            writer.WriteString("applicationId", module.ApplicationId);
            writer.WriteString("resourceId", module.ResourceId);
            writer.WriteString("descriptorAssembly", module.DescriptorAssembly);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WritePropertyName("platform");
        if (topology.Platform is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("eventStoreVersion", topology.Platform.EventStoreVersion);
            writer.WriteString("daprRuntimeVersion", topology.Platform.DaprRuntimeVersion);
            writer.WriteString("daprSdkVersion", topology.Platform.DaprSdkVersion);
            writer.WriteString("frontComposerVersion", topology.Platform.FrontComposerVersion);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}