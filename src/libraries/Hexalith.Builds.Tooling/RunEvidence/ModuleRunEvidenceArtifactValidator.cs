// <copyright file="ModuleRunEvidenceArtifactValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

using System.Globalization;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Validates the complete metadata-only module-run evidence contract before it can support a readiness claim.
/// </summary>
internal static class ModuleRunEvidenceArtifactValidator
{
    private const string _schema = "hexalith.module-run-evidence.v1";

    private static readonly HashSet<string> _artifactHashProperties = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _environmentProperties = new(StringComparer.Ordinal)
    {
        "repositoryRevision",
        "repositoryDirtyMarker",
        "sdkVersion",
        "operatingSystem",
        "toolVersion",
    };
    private static readonly HashSet<string> _invocationProperties = new(StringComparer.Ordinal)
    {
        "command",
        "manifestPath",
        "manifestHash",
        "profile",
        "fixturePath",
        "fixtureHash",
        "filterHash",
    };
    private static readonly HashSet<string> _moduleProperties = new(StringComparer.Ordinal)
    {
        "id",
        "domain",
        "applicationId",
        "resourceId",
        "descriptorAssembly",
    };
    private static readonly HashSet<string> _outcomeProperties = new(StringComparer.Ordinal)
    {
        "exitCode",
        "phase",
        "category",
        "ruleId",
    };
    private static readonly HashSet<string> _phaseOutcomeProperties = new(StringComparer.Ordinal)
    {
        "phase",
        "category",
        "ruleId",
    };
    private static readonly HashSet<string> _platformProperties = new(StringComparer.Ordinal)
    {
        "eventStoreVersion",
        "daprRuntimeVersion",
        "daprSdkVersion",
        "frontComposerVersion",
    };
    private static readonly HashSet<string> _rootProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "runId",
        "timestamps",
        "environment",
        "invocation",
        "topology",
        "phaseOutcomes",
        "testCounts",
        "artifactHashes",
        "finalStatus",
        "outcome",
        "volatileFields",
    };
    private static readonly HashSet<string> _testCountProperties = new(StringComparer.Ordinal)
    {
        "reported",
        "total",
        "passed",
        "failed",
        "skipped",
    };
    private static readonly HashSet<string> _timestampProperties = new(StringComparer.Ordinal)
    {
        "startedUtc",
        "completedUtc",
    };
    private static readonly HashSet<string> _topologyProperties = new(StringComparer.Ordinal)
    {
        "modules",
        "platform",
    };

    /// <summary>
    /// Validates a complete canonical run-evidence artifact and exposes only its non-secret completion summary.
    /// </summary>
    /// <param name="artifactBytes">The raw JSON artifact bytes.</param>
    /// <param name="summary">The safe result summary when validation succeeds.</param>
    /// <returns><see langword="true"/> when the artifact is a schema-valid canonical run-evidence document.</returns>
    public static bool TryValidate(
        ReadOnlyMemory<byte> artifactBytes,
        out ModuleRunEvidenceArtifactSummary? summary)
    {
        summary = null;
        if (artifactBytes.IsEmpty || artifactBytes.Span[^1] != (byte)'\n')
        {
            return false;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(artifactBytes);
            JsonElement root = document.RootElement;
            if (!HasExactProperties(root, _rootProperties)
                || !IsString(root, "schema", _schema)
                || !IsLowerHex(root, "runId", 32)
                || !IsTimestamps(root)
                || !IsEnvironment(root)
                || !IsInvocation(root)
                || !IsTopology(root)
                || !IsPhaseOutcomes(root)
                || !IsTestCounts(root)
                || !IsArtifactHashes(root)
                || !IsFinalStatus(root)
                || !IsOutcome(root, out int exitCode)
                || !IsVolatileFields(root))
            {
                return false;
            }

            summary = new ModuleRunEvidenceArtifactSummary(
                root.GetProperty("finalStatus").GetString()!,
                exitCode);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, IReadOnlySet<string> properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!properties.Contains(property.Name) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return seen.SetEquals(properties);
    }

    private static bool IsArtifactHashes(JsonElement root)
    {
        JsonElement artifactHashes = root.GetProperty("artifactHashes");
        if (artifactHashes.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (JsonProperty artifactHash in artifactHashes.EnumerateObject())
        {
            if (!IsUpperHex(artifactHash.Value, 64))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEnvironment(JsonElement root)
    {
        JsonElement environment = root.GetProperty("environment");
        return HasExactProperties(environment, _environmentProperties)
            && IsMetadataString(environment, "repositoryRevision")
            && IsOneOf(environment, "repositoryDirtyMarker", "clean", "dirty", "unknown")
            && IsMetadataString(environment, "sdkVersion")
            && IsMetadataString(environment, "operatingSystem")
            && IsMetadataString(environment, "toolVersion");
    }

    private static bool IsFinalStatus(JsonElement root) =>
        IsOneOf(root, "finalStatus", "completed", "failed", "unavailable", "cancelled");

    private static bool IsInvocation(JsonElement root)
    {
        JsonElement invocation = root.GetProperty("invocation");
        return HasExactProperties(invocation, _invocationProperties)
            && IsMetadataString(invocation, "command")
            && IsNullableMetadataString(invocation, "manifestPath")
            && IsNullableMetadataString(invocation, "manifestHash")
            && IsNullableMetadataString(invocation, "profile")
            && IsNullableMetadataString(invocation, "fixturePath")
            && IsNullableMetadataString(invocation, "fixtureHash")
            && IsNullableMetadataString(invocation, "filterHash");
    }

    private static bool IsLowerHex(JsonElement root, string propertyName, int length)
    {
        JsonElement value = root.GetProperty(propertyName);
        return value.ValueKind == JsonValueKind.String
            && value.GetString() is string text
            && text.Length == length
            && text.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool IsMetadataString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
        && root.GetProperty(propertyName).GetString() is string value
        && !ManifestSecretDetector.ContainsSecret(value);

    private static bool IsModule(JsonElement module) =>
        HasExactProperties(module, _moduleProperties)
        && IsMetadataString(module, "id")
        && IsMetadataString(module, "domain")
        && IsMetadataString(module, "applicationId")
        && IsMetadataString(module, "resourceId")
        && IsMetadataString(module, "descriptorAssembly");

    private static bool IsNullableMetadataString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.Null
        || IsMetadataString(root, propertyName);

    private static bool IsOneOf(JsonElement root, string propertyName, params string[] values) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
        && root.GetProperty(propertyName).GetString() is string value
        && values.Contains(value, StringComparer.Ordinal);

    private static bool IsOutcome(JsonElement root, out int exitCode)
    {
        exitCode = 0;
        JsonElement outcome = root.GetProperty("outcome");
        if (!HasExactProperties(outcome, _outcomeProperties)
            || outcome.GetProperty("exitCode").ValueKind != JsonValueKind.Number
            || !outcome.GetProperty("exitCode").TryGetInt32(out exitCode)
            || !new[] { 0, 1, 2, 3, 4, 5, 6, 130 }.Contains(exitCode)
            || !IsMetadataString(outcome, "phase")
            || !IsMetadataString(outcome, "category"))
        {
            return false;
        }

        JsonElement ruleId = outcome.GetProperty("ruleId");
        return ruleId.ValueKind == JsonValueKind.Null
            || (ruleId.ValueKind == JsonValueKind.String
                && ruleId.GetString() is string ruleIdValue
                && !ManifestSecretDetector.ContainsSecret(ruleIdValue));
    }

    private static bool IsPhaseOutcomes(JsonElement root)
    {
        JsonElement phaseOutcomes = root.GetProperty("phaseOutcomes");
        if (phaseOutcomes.ValueKind != JsonValueKind.Array || phaseOutcomes.GetArrayLength() == 0)
        {
            return false;
        }

        foreach (JsonElement phaseOutcome in phaseOutcomes.EnumerateArray())
        {
            if (!HasExactProperties(phaseOutcome, _phaseOutcomeProperties)
                || !IsMetadataString(phaseOutcome, "phase")
                || !IsMetadataString(phaseOutcome, "category"))
            {
                return false;
            }

            JsonElement ruleId = phaseOutcome.GetProperty("ruleId");
            if (ruleId.ValueKind != JsonValueKind.Null
                && (ruleId.ValueKind != JsonValueKind.String
                    || ruleId.GetString() is not string ruleIdValue
                    || ManifestSecretDetector.ContainsSecret(ruleIdValue)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsPlatform(JsonElement platform) =>
        HasExactProperties(platform, _platformProperties)
        && IsMetadataString(platform, "eventStoreVersion")
        && IsMetadataString(platform, "daprRuntimeVersion")
        && IsMetadataString(platform, "daprSdkVersion")
        && IsMetadataString(platform, "frontComposerVersion");

    private static bool IsTestCounts(JsonElement root)
    {
        JsonElement testCounts = root.GetProperty("testCounts");
        if (!HasExactProperties(testCounts, _testCountProperties)
            || testCounts.GetProperty("reported").ValueKind is not JsonValueKind.True and not JsonValueKind.False
            || !TryGetNonNegativeInteger(testCounts, "total", out int total)
            || !TryGetNonNegativeInteger(testCounts, "passed", out int passed)
            || !TryGetNonNegativeInteger(testCounts, "failed", out int failed)
            || !TryGetNonNegativeInteger(testCounts, "skipped", out int skipped))
        {
            return false;
        }

        bool reported = testCounts.GetProperty("reported").GetBoolean();
        return reported
            ? passed + failed + skipped == total
            : total == 0 && passed == 0 && failed == 0 && skipped == 0;
    }

    private static bool IsTimestamps(JsonElement root)
    {
        JsonElement timestamps = root.GetProperty("timestamps");
        return HasExactProperties(timestamps, _timestampProperties)
            && IsTimestamp(timestamps, "startedUtc")
            && IsTimestamp(timestamps, "completedUtc");
    }

    private static bool IsTimestamp(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            root.GetProperty(propertyName).GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out _);

    private static bool IsTopology(JsonElement root)
    {
        JsonElement topology = root.GetProperty("topology");
        if (!HasExactProperties(topology, _topologyProperties)
            || topology.GetProperty("modules").ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (JsonElement module in topology.GetProperty("modules").EnumerateArray())
        {
            if (!IsModule(module))
            {
                return false;
            }
        }

        JsonElement platform = topology.GetProperty("platform");
        return platform.ValueKind == JsonValueKind.Null || IsPlatform(platform);
    }

    private static bool IsUpperHex(JsonElement value, int length) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is string text
        && text.Length == length
        && text.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool IsVolatileFields(JsonElement root)
    {
        JsonElement volatileFields = root.GetProperty("volatileFields");
        if (volatileFields.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        HashSet<string> fields = new(StringComparer.Ordinal);
        foreach (JsonElement field in volatileFields.EnumerateArray())
        {
            if (field.ValueKind != JsonValueKind.String
                || field.GetString() is not string fieldName
                || ManifestSecretDetector.ContainsSecret(fieldName)
                || !fields.Add(fieldName))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGetNonNegativeInteger(JsonElement root, string propertyName, out int value) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.Number
        && root.GetProperty(propertyName).TryGetInt32(out value)
        && value >= 0;
}

/// <summary>
/// Represents the safe completion facts extracted from a schema-valid run-evidence artifact.
/// </summary>
/// <param name="FinalStatus">The reported completion status.</param>
/// <param name="ExitCode">The reported stable process exit code.</param>
internal sealed record ModuleRunEvidenceArtifactSummary(string FinalStatus, int ExitCode);
