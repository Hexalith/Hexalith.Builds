// <copyright file="TestResultWriter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestEnvironment;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Publishes validated terminal snapshots outside disposable owner workspaces.</summary>
public static class TestResultWriter
{
    /// <summary>Writes classified diagnostics followed by an atomic validated result. Call under the owner's outcome lock.</summary>
    /// <param name="directory">The durable owner diagnostic directory.</param>
    /// <param name="state">The original durable owner identities.</param>
    /// <param name="record">The immutable first outcome and evolving disposition.</param>
    /// <param name="cancellationToken">The existing recording budget.</param>
    /// <returns>The absolute result location.</returns>
    /// <exception cref="InvalidDataException">The known observations cannot form a valid contract.</exception>
    /// <exception cref="IOException">The destination no longer satisfies the owned-path policy.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Published content identities require lowercase SHA-256 hexadecimal.")]
    public static async Task<string> WriteAsync(string directory, CompositionRunState state, ModuleTestRunRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(record);
        string ownedResult = Path.GetFullPath(Path.Combine(directory, "test-result.json"));
        if (!SelectedNativeTestReport.IsOwnedParent(ownedResult, directory) || new FileInfo(ownedResult).LinkTarget is not null)
        {
            throw new IOException("The terminal result destination is outside the owned path policy.");
        }

        if (state.RunId != record.RunId || state.AttemptId is null || state.EnvironmentId is null || state.CreatedAt is null)
        {
            throw new InvalidDataException("Original run identities are unavailable.");
        }

        JsonNode bindings = record.EnvironmentBindings is { } snapshot
            ? JsonNode.Parse(snapshot.GetRawText())!
            : PartialBindings(state, record);
        if (bindings is JsonObject current)
        {
            // Holds are runner-owned and evolve after the first outcome, so they are always read from the durable records.
            current["attachments"] = CompositionAttachmentStore.ReadContractBinding(directory, state.RunId, DateTimeOffset.UtcNow);
        }

        if (bindings is not JsonObject || bindings["run"] is not JsonObject run
            || !Matches(run["runId"], state.RunId) || !Matches(run["attemptId"], state.AttemptId)
            || !Matches(run["environmentId"], state.EnvironmentId))
        {
            throw new InvalidDataException("The result does not match the original owner identities.");
        }

        static bool Matches(JsonNode? value, string identity) => value is JsonValue scalar
            && scalar.TryGetValue(out string? observed) && observed == identity;

        // Only the runner's original activation advances; no timing file is read after test execution.
        if (record.ExecutionTiming is { ActualDurationSeconds: not null } timing)
        {
            if (bindings["deadlines"] is not JsonObject deadlines || deadlines["execution"] is not JsonObject execution
                || execution["state"] is not JsonValue population || !population.TryGetValue(out string? stateValue))
            {
                throw new InvalidDataException("The result execution deadline bindings cannot be verified.");
            }

            if (stateValue == "populated")
            {
                execution["actualDurationSeconds"] = timing.ActualDurationSeconds;
            }
        }

        string outcome = (record.TerminalOutcome.ExitCode == Diagnostics.ToolExitCode.Success, record.TerminalOutcome.ExitCode == Diagnostics.ToolExitCode.Cancelled) switch
        {
            (true, _) => "passed",
            (_, true) => "cancelled",
            _ => "failed",
        };
        string cause = record.TerminalCause ?? Cause(record);
        string now = Utc(DateTimeOffset.UtcNow);
        string[] leftovers = [.. record.RemainingResources.ResourceIds.Distinct(StringComparer.Ordinal)];
        string[] observed = [.. record.Resources.Select(resource => "resource:" + resource.Name).Where(SafeIdentity)];
        string[] owned = [.. observed.Concat(leftovers).Distinct(StringComparer.Ordinal)];
        bool removed = record.CleanupDisposition == "complete" && record.RemainingResources.IsEmpty;
        bool retained = record.FailureRetained && !removed;
        string retention = (retained, record.FailureRetained && removed) switch
        {
            (true, _) => "failure-retained",
            (_, true) => "released",
            _ => "not-retained",
        };
        string retentionReason = retention switch
        {
            "failure-retained" => "local-failure",
            "released" => "owned-cleanup-verified",
            _ => "local-failure-retention-not-established",
        };
        string[] diagnosticIds = ["lifecycle"];
        string cleanup = record.CleanupDisposition;
        if (cleanup == "complete" && !record.RemainingResources.IsEmpty)
        {
            cleanup = "incomplete";
        }

        // Fixed metadata intentionally excludes exception messages, child text, credentials and supplied paths.
        byte[] diagnosticBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            runId = record.RunId,
            outcome,
            cause,
            cleanup,
            discoveryUnverified = record.RemainingResources.Unverified,
            discoveryFailures = record.RemainingResources.DiscoveryFailures.Where(category => category is "containers" or "processes" or "cleanup-verification" or "runtime-resources" or "workspaces-or-claims" or "claim-release" or "claims" or "ownership" or "state-finalization").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ruleIds = record.DiagnosticRuleIds.Concat(record.LaterDiagnosticRuleIds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            terminalRuleIds = record.DiagnosticRuleIds,
        });

        // Content-addressed diagnostics remain immutable across result updates and retries.
        JsonElement diagnostic = JsonSerializer.Deserialize<JsonElement>(diagnosticBytes);
        byte[] retainedDiagnostic = System.Text.Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(diagnostic));
        string hash = Convert.ToHexStringLower(SHA256.HashData(retainedDiagnostic));
        string diagnosticIdentity = "sha256:" + hash;
        string diagnosticReference = "lifecycle-" + hash + ".json";
        await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(directory, diagnosticReference), directory, retainedDiagnostic, cancellationToken).ConfigureAwait(false);
        List<object> diagnostics = [new { id = "lifecycle", classification = "internal", redacted = true, reference = diagnosticReference, contentIdentity = diagnosticIdentity }];
        if (record.ReportHash is not null)
        {
            string reference = "native-" + record.ReportHash.ToLowerInvariant() + ".trx";
            await EnsureArtifactSnapshotAsync(directory, reference, "native.trx", record.ReportHash, cancellationToken).ConfigureAwait(false);
            diagnostics.Add(new { id = "native-report", classification = "internal", redacted = true, reference, contentIdentity = "sha256:" + record.ReportHash.ToLowerInvariant() });
        }

        if (record.ObservationHash is not null)
        {
            string reference = "functional-observations-" + record.ObservationHash.ToLowerInvariant() + ".json";
            await EnsureArtifactSnapshotAsync(directory, reference, "functional-observations.json", record.ObservationHash, cancellationToken).ConfigureAwait(false);
            diagnostics.Add(new { id = "functional-observations", classification = "internal", redacted = true, reference, contentIdentity = "sha256:" + record.ObservationHash.ToLowerInvariant() });
        }

        JsonObject cleanupNode = Node(new
        {
            state = "populated",
            disposition = cleanup,
            observedAt = now,
            ownedResourceIds = owned,
            leftoverResourceIds = leftovers,
            failures = cleanup == "incomplete" ? leftovers : [],
            diagnosticIds,
        });
        if (cleanup != "not-requested")
        {
            cleanupNode["attemptId"] = record.CleanupAttemptId ?? throw new InvalidDataException("The cleanup attempt identity is unavailable.");
        }

        JsonObject document = new()
        {
            ["schema"] = TestEnvironmentContractReader.ResultSchema,
            ["policyId"] = TestEnvironmentContractReader.PolicyId,
            ["bindings"] = bindings,
            ["terminal"] = Node(new { outcome, recordedAt = Utc(record.TerminalAtUtc), cause, diagnosticIds }),

            // The native child has stopped, but discovery alone cannot authenticate every work/access termination.
            ["termination"] = Unavailable(),
            ["retention"] = Node(new { state = "populated", disposition = retention, failureRunIds = record.FailureRetained ? new[] { record.RunId } : [], retainedResourceIds = retained ? leftovers : [], reason = retentionReason, recordedAt = now }),
            ["cleanup"] = cleanupNode,
            ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics),
        };
        string path = Path.GetFullPath(Path.Combine(directory, "test-result.json"));
        string temporary = Path.Combine(directory, ".result-validation-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await NativeTestExecutor.WriteSnapshotAsync(temporary, directory, System.Text.Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(document)), cancellationToken).ConfigureAwait(false);
            TestEnvironmentContractReadResult validated = TestEnvironmentContractReader.ReadResult(temporary, cancellationToken);
            if (!validated.IsValid)
            {
                throw new InvalidDataException("The generated terminal result failed contract validation: " + string.Join(",", validated.Diagnostics.Select(diagnostic => diagnostic.RuleId + ":" + diagnostic.Field)));
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!SelectedNativeTestReport.IsOwnedRegularPath(temporary, directory) || !SelectedNativeTestReport.IsOwnedParent(path, directory) || new FileInfo(path).LinkTarget is not null)
            {
                throw new IOException("The terminal result destination changed outside ownership.");
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (SelectedNativeTestReport.IsOwnedParent(temporary, directory) && new FileInfo(temporary).LinkTarget is null)
            {
                File.Delete(temporary);
            }
        }

        return path;
    }

    /// <summary>Maps the preserved causal observation to the published v1 cause.</summary>
    /// <param name="record">The first selected-run record.</param>
    /// <returns>The v1 cause.</returns>
    internal static string Cause(ModuleTestRunRecord record) => record switch
    {
        { TerminalOutcome.ExitCode: Diagnostics.ToolExitCode.Success } => "tests-completed",
        { TerminalOutcome.ExitCode: Diagnostics.ToolExitCode.Cancelled } => "run-cancelled",
        { ExecutionTiming: null } => "startup-failure",
        _ => "test-failure",
    };

    private static async Task EnsureArtifactSnapshotAsync(string directory, string reference, string legacy, string hash, CancellationToken cancellationToken)
    {
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit))
        {
            throw new InvalidDataException("The retained artifact identity is invalid.");
        }

        string target = Path.Combine(directory, reference);
        bool exists = File.Exists(target);
        FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(exists ? target : Path.Combine(directory, legacy));
        byte[] bytes;
        await using (stream.ConfigureAwait(false))
        {
            if (!stream.CanSeek || stream.Length is <= 0 or > 16 * 1024 * 1024)
            {
                throw new InvalidDataException("The retained artifact cannot be verified.");
            }

            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The retained artifact does not match its original identity.");
        }

        if (!exists)
        {
            await NativeTestExecutor.WriteSnapshotAsync(target, directory, bytes, cancellationToken).ConfigureAwait(false);
        }
    }

    private static JsonObject PartialBindings(CompositionRunState state, ModuleTestRunRecord record)
    {
        object startupOverride = state.EffectiveDeadlineIsOverride
            ? new { state = "declared", requestedSeconds = state.EffectiveDeadlineSeconds, justification = state.EffectiveDeadlineJustification }
            : new { state = "not-overridden" };
        JsonNode startup = state.StartupDeadlineUtc is { } expires && state.EffectiveDeadlineSeconds is { } seconds && state.CreatedAt is { } origin
            ? Node(new { state = "populated", origin = "environment-requested", originAt = Utc(origin), effectiveSeconds = seconds, expiresAt = Utc(expires), @override = startupOverride })
            : Unavailable();
        JsonNode execution = record.ExecutionTiming is { } timing
            ? Node(new { state = "populated", origin = "test-execution-started", originAt = Utc(timing.StartedAtUtc), effectiveSeconds = timing.EffectiveSeconds, expiresAt = Utc(timing.DeadlineUtc), configurationContentIdentity = timing.ConfigurationContentIdentity })
            : Unavailable();
        JsonNode context = state.ExecutionContext is { } actual ? JsonNode.Parse(actual.GetRawText())! : throw new InvalidDataException("Original execution context is unavailable.");
        return new JsonObject
        {
            ["run"] = Node(new { runId = state.RunId, attemptId = state.AttemptId, attemptSequence = 1, ownerRunId = state.RunId, environmentId = state.EnvironmentId, role = "owner" }),
            ["environment"] = new JsonObject
            {
                ["composition"] = Unavailable(),
                ["mode"] = record.Selection.Mode,
                ["executionContext"] = context,
                ["catalog"] = Unavailable(),
                ["isolation"] = Unavailable(),

                // Ownership and endpoints not validated at handoff remain explicitly unobserved.
                ["resources"] = Unavailable(),
                ["readiness"] = Unavailable(),
                ["startupTasks"] = Unavailable(),
            },
            ["tool"] = Unavailable(),
            ["candidate"] = Unavailable(),
            ["decision"] = Unavailable(),
            ["reuseLineage"] = Unavailable(),
            ["artifacts"] = new JsonObject { ["expected"] = Unavailable(), ["loaded"] = Unavailable() },
            ["suite"] = new JsonObject { ["declared"] = Unavailable(), ["completion"] = Unavailable() },
            ["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = new JsonArray() },
            ["deadlines"] = new JsonObject { ["startup"] = startup, ["execution"] = execution },
        };
    }

    private static bool SafeIdentity(string value) => value is { Length: > 0 and <= 2048 } && !value.Any(char.IsControl) && !Manifest.ManifestSecretDetector.ContainsSecret(value);

    private static JsonObject Unavailable() => new() { ["state"] = "not-yet-populated" };

    private static JsonObject Node<T>(T value) => (JsonObject)JsonSerializer.SerializeToNode(value)!;

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}