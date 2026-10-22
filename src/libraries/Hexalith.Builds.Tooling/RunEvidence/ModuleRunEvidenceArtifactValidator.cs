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
    private static readonly JsonSerializerOptions _selectedTestJsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly Json.Schema.JsonSchema _selectedTestSchema = ReadSelectedTestSchema();

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

    /// <summary>
    /// Additive v1 fields accepted when present so the schema can carry persisted assertions
    /// and expected sequences without a breaking version bump, but not required, so existing
    /// artifacts remain valid.
    /// </summary>
    private static readonly HashSet<string> _optionalRootProperties = new(StringComparer.Ordinal)
    {
        "persistedAssertions",
        "expectedSequences",
        "selectedTestRun",
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
            if (!HasRequiredWithOptionalProperties(root, _rootProperties, _optionalRootProperties)
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
                || !IsSelectedTestRun(root))
            {
                return false;
            }

            if (!IsOutcome(root, out int exitCode)
                || !IsVolatileFields(root)
                || !IsOptionalMetadataStringArray(root, "persistedAssertions")
                || !IsOptionalMetadataStringArray(root, "expectedSequences"))
            {
                return false;
            }

            JsonElement invocation = root.GetProperty("invocation");
            JsonElement profileElement = invocation.GetProperty("profile");
            JsonElement manifestPathElement = invocation.GetProperty("manifestPath");
            JsonElement manifestHashElement = invocation.GetProperty("manifestHash");
            JsonElement fixturePathElement = invocation.GetProperty("fixturePath");
            JsonElement fixtureHashElement = invocation.GetProperty("fixtureHash");
            JsonElement filterHashElement = invocation.GetProperty("filterHash");
            JsonElement testCounts = root.GetProperty("testCounts");
            summary = new ModuleRunEvidenceArtifactSummary(
                root.GetProperty("finalStatus").GetString()!,
                exitCode,
                invocation.GetProperty("command").GetString()!,
                manifestPathElement.ValueKind == JsonValueKind.String ? manifestPathElement.GetString() : null,
                manifestHashElement.ValueKind == JsonValueKind.String ? manifestHashElement.GetString() : null,
                profileElement.ValueKind == JsonValueKind.String ? profileElement.GetString() : null,
                fixturePathElement.ValueKind == JsonValueKind.String ? fixturePathElement.GetString() : null,
                fixtureHashElement.ValueKind == JsonValueKind.String ? fixtureHashElement.GetString() : null,
                filterHashElement.ValueKind == JsonValueKind.String ? filterHashElement.GetString() : null,
                testCounts.GetProperty("reported").GetBoolean(),
                testCounts.GetProperty("passed").GetInt32(),
                testCounts.GetProperty("failed").GetInt32());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, HashSet<string> properties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        JsonProperty[] actualProperties = [.. element.EnumerateObject()];
        return actualProperties.Length == properties.Count
            && actualProperties.All(property => properties.Contains(property.Name))
            && actualProperties.Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() == actualProperties.Length;
    }

    private static bool HasRequiredWithOptionalProperties(
        JsonElement element,
        HashSet<string> required,
        HashSet<string> optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string[] names = [.. element.EnumerateObject().Select(property => property.Name)];
        return names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.All(name => required.Contains(name) || optional.Contains(name))
            && required.All(name => names.Contains(name, StringComparer.Ordinal));
    }

    private static bool IsOptionalMetadataStringArray(JsonElement root, string propertyName) =>
        !root.TryGetProperty(propertyName, out JsonElement value)
        || (value.ValueKind == JsonValueKind.Array
            && value.EnumerateArray().All(item =>
                item.ValueKind == JsonValueKind.String
                && item.GetString() is string text
                && !ManifestSecretDetector.ContainsSecret(text)));

    private static bool IsArtifactHashes(JsonElement root)
    {
        JsonElement artifactHashes = root.GetProperty("artifactHashes");
        return artifactHashes.ValueKind == JsonValueKind.Object
            && artifactHashes.EnumerateObject().All(artifactHash => IsUpperHex(artifactHash.Value, 64));
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
        IsOneOf(root, "finalStatus", "completed", "failed", "unavailable", "cancelled", "withdrawn");

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
            && text.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    private static bool IsMetadataString(JsonElement root, string propertyName) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
        && root.GetProperty(propertyName).GetString() is string value
        && !ManifestSecretDetector.ContainsSecret(value);

    private static bool IsString(JsonElement root, string propertyName, string expected) =>
        root.GetProperty(propertyName).ValueKind == JsonValueKind.String
        && string.Equals(root.GetProperty(propertyName).GetString(), expected, StringComparison.Ordinal);

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
        return phaseOutcomes.ValueKind == JsonValueKind.Array
            && phaseOutcomes.GetArrayLength() > 0
            && phaseOutcomes.EnumerateArray().All(IsPhaseOutcome);
    }

    private static bool IsPhaseOutcome(JsonElement phaseOutcome)
    {
        if (!HasExactProperties(phaseOutcome, _phaseOutcomeProperties)
            || !IsMetadataString(phaseOutcome, "phase")
            || !IsMetadataString(phaseOutcome, "category"))
        {
            return false;
        }

        JsonElement ruleId = phaseOutcome.GetProperty("ruleId");
        return ruleId.ValueKind == JsonValueKind.Null
            || (ruleId.ValueKind == JsonValueKind.String
                && ruleId.GetString() is string ruleIdValue
                && !ManifestSecretDetector.ContainsSecret(ruleIdValue));
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
            || testCounts.GetProperty("reported").ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !TryGetNonNegativeInteger(testCounts, "total", out int total)
            || !TryGetNonNegativeInteger(testCounts, "passed", out int passed)
            || !TryGetNonNegativeInteger(testCounts, "failed", out int failed)
            || !TryGetNonNegativeInteger(testCounts, "skipped", out int skipped))
        {
            return false;
        }

        bool reported = testCounts.GetProperty("reported").GetBoolean();
        return reported
            ? (long)passed + failed + skipped == total
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

        if (!topology.GetProperty("modules").EnumerateArray().All(IsModule))
        {
            return false;
        }

        JsonElement platform = topology.GetProperty("platform");
        return platform.ValueKind == JsonValueKind.Null || IsPlatform(platform);
    }

    private static bool IsUpperHex(JsonElement value, int length) =>
        value.ValueKind == JsonValueKind.String
        && value.GetString() is string text
        && text.Length == length
        && text.All(character => character is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    private static bool IsVolatileFields(JsonElement root)
    {
        JsonElement volatileFields = root.GetProperty("volatileFields");
        if (volatileFields.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        JsonElement[] fieldElements = [.. volatileFields.EnumerateArray()];
        if (fieldElements.Any(field => field.ValueKind != JsonValueKind.String))
        {
            return false;
        }

        string[] fields = [.. fieldElements.Select(field => field.GetString()!)];
        return fields.All(field => !ManifestSecretDetector.ContainsSecret(field))
            && fields.Distinct(StringComparer.Ordinal).Count() == fields.Length;
    }

    private static bool TryGetNonNegativeInteger(JsonElement root, string propertyName, out int value)
    {
        value = 0;
        JsonElement property = root.GetProperty(propertyName);
        return property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value)
            && value >= 0;
    }

    private static Json.Schema.JsonSchema ReadSelectedTestSchema()
    {
        using Stream schemaStream = typeof(ModuleRunEvidenceArtifactValidator).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.RunEvidence.hexalith.module-run-evidence.v1.json")!;
        using StreamReader reader = new(schemaStream);
        return Json.Schema.JsonSchema.FromText(reader.ReadToEnd(), new Json.Schema.BuildOptions
        {
            SchemaRegistry = new Json.Schema.SchemaRegistry
            {
                Fetch = (_, _) => throw new InvalidOperationException("Run evidence validation cannot fetch remote schemas."),
            },
        });
    }

    private static bool IsSelectedTestRun(JsonElement root)
    {
        if (!root.TryGetProperty("selectedTestRun", out JsonElement selected))
        {
            return true;
        }

        if (!_selectedTestSchema.Evaluate(root).IsValid || !SafeValues(selected))
        {
            return false;
        }

        Runtime.ModuleTestRunRecord? record = selected.Deserialize<Runtime.ModuleTestRunRecord>(_selectedTestJsonOptions);
        bool invalid = record is null || record.RunId != root.GetProperty("runId").GetString() || record.OwnerId != record.RunId
            || record.Selection.ExecutionDeadlineUtc <= record.Selection.RequestedAtUtc
            || record.Selection.ExecutionDeadlineUtc - record.Selection.RequestedAtUtc != TimeSpan.FromSeconds(record.Selection.ExecutionTimeoutSeconds)
            || record.Selection.CredentialExpiryUtc < record.Selection.ExecutionDeadlineUtc
            || record.Selection.CredentialExpiryUtc - record.Selection.ExecutionDeadlineUtc >= TimeSpan.FromSeconds(1)
            || record.Selection.CredentialExpiryUtc.Ticks % TimeSpan.TicksPerSecond != 0
            || (record.Selection.Executor == "python3" && record.Selection.Mode != "source")
            || record.Selections.Count > 1
            || (record.Selections.Count == 1 && record.Selections[0] != record.Selection)
            || (record.ExecutionTiming is not null && !IsExecutionTimingCoherent(selected, record))
            || (record.ResourceDisposition == "removed" && !record.RemainingResources.IsEmpty)
            || selected.GetProperty("remainingResources").GetProperty("isEmpty").GetBoolean() != record.RemainingResources.IsEmpty;
        return !invalid && IsSelectedBindingCoherent(root, record!) && (root.GetProperty("finalStatus").GetString() != "completed"
            || (record!.TerminalStatus == "completed" && record.TerminalOutcome.ExitCode == Diagnostics.ToolExitCode.Success
                && record.ResourceDisposition == "removed" && record.RemainingResources.IsEmpty
                && record.ReportHash is not null && record.TestCounts is { Total: > 0, Failed: 0, Skipped: 0 } counts
                && counts.Passed == counts.Total));
    }

    private static bool IsExecutionTimingCoherent(JsonElement selected, Runtime.ModuleTestRunRecord record)
    {
        Runtime.ModuleTestExecutionTiming timing = record.ExecutionTiming!;
        IReadOnlyList<Runtime.ModuleTestSelection> selections = record.Selections.Count == 0 ? [record.Selection] : record.Selections;
        return timing.StartedAtUtc >= record.Selection.RequestedAtUtc && timing.StartedAtUtc <= record.TerminalAtUtc
            && timing.DeadlineUtc - timing.StartedAtUtc == TimeSpan.FromSeconds(timing.EffectiveSeconds)
            && selected.GetProperty("executionTiming").GetProperty("credentialExpiryUtc").GetDateTimeOffset() == timing.CredentialExpiryUtc
            && selections.Any(entry => timing.EffectiveSeconds == entry.ExecutionTimeoutSeconds
                && Runtime.ModuleTestExecutionTiming.Start(entry, timing.StartedAtUtc).ConfigurationContentIdentity == timing.ConfigurationContentIdentity)
            && selections.Select(entry => entry.Name).Distinct(StringComparer.Ordinal).Count() == selections.Count
            && selections.All(entry => entry.ManifestHash == record.Selection.ManifestHash && entry.ModuleId == record.Selection.ModuleId
                && entry.Mode == record.Selection.Mode && entry.DaprRuntimeVersion == record.Selection.DaprRuntimeVersion);
    }

    private static bool IsSelectedBindingCoherent(JsonElement root, Runtime.ModuleTestRunRecord record)
    {
        JsonElement invocation = root.GetProperty("invocation");
        JsonElement counts = root.GetProperty("testCounts");
        if (invocation.GetProperty("profile").GetString() != record.Selection.Name
            || invocation.GetProperty("manifestHash").GetString() != record.Selection.ManifestHash)
        {
            return false;
        }

        bool requiresObservations = record.Selection is { ModuleId: "memories", Name: "functional-retention" }
            && record.TerminalOutcome.ExitCode == Diagnostics.ToolExitCode.Success;
        if ((requiresObservations && record.ObservationHash is null)
            || (record.ObservationHash is not null && !root.GetProperty("artifactHashes").EnumerateObject().Any(artifact =>
                artifact.Name.EndsWith(".functional-observations.json", StringComparison.Ordinal) && artifact.Value.GetString() == record.ObservationHash)))
        {
            return false;
        }

        if (record.ReportHash is not null)
        {
            if (!root.GetProperty("artifactHashes").EnumerateObject().Any(artifact => artifact.Value.GetString() == record.ReportHash))
            {
                return false;
            }
        }
        else if (root.GetProperty("artifactHashes").EnumerateObject().Any(artifact => record.ObservationHash is null
            || !artifact.Name.EndsWith(".functional-observations.json", StringComparison.Ordinal) || artifact.Value.GetString() != record.ObservationHash))
        {
            return false;
        }

        bool countsMatch = record.TestCounts is { } reported
            ? counts.GetProperty("reported").GetBoolean() == reported.Reported
                && counts.GetProperty("total").GetInt32() == reported.Total
                && counts.GetProperty("passed").GetInt32() == reported.Passed
                && counts.GetProperty("failed").GetInt32() == reported.Failed
                && counts.GetProperty("skipped").GetInt32() == reported.Skipped
            : !counts.GetProperty("reported").GetBoolean();
        string? finalStatus = root.GetProperty("finalStatus").GetString();
        int finalExit = root.GetProperty("outcome").GetProperty("exitCode").GetInt32();
        bool terminalMatches = record.TerminalOutcome.ExitCode == Diagnostics.ToolExitCode.Success
            ? record.TerminalStatus == "completed" && ((finalStatus == "completed" && finalExit == 0) || (finalStatus == "failed" && finalExit != 0))
            : record.TerminalStatus == finalStatus && (int)record.TerminalOutcome.ExitCode == finalExit;
        return countsMatch && terminalMatches;
    }

    private static bool SafeValues(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !ManifestSecretDetector.ContainsSecret(value.GetString()!),
        JsonValueKind.Array => value.EnumerateArray().All(SafeValues),
        JsonValueKind.Object => value.EnumerateObject().All(property => !ManifestSecretDetector.ContainsSecret(property.Name) && SafeValues(property.Value)),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };
}