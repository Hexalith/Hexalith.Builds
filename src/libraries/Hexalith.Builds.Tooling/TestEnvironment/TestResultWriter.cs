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
    private static readonly string[] _lifecycleDiagnosticIds = ["lifecycle"];
    private static readonly string[] _terminationDiagnosticIds = ["termination"];

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

        IReadOnlyList<CompositionAttachment> attachments = await CompositionAttachmentStore.ReadAsync(directory, state.RunId, cancellationToken).ConfigureAwait(false);
        JsonNode bindings = record.EnvironmentBindings is { } snapshot
            ? JsonNode.Parse(snapshot.GetRawText())!
            : PartialBindings(state, record);
        if (bindings is JsonObject current)
        {
            // Holds are runner-owned and evolve after the first outcome, so they are always read from the durable records.
            JsonArray holds = [];
            DateTimeOffset observedAt = DateTimeOffset.UtcNow;
            foreach (CompositionAttachment attachment in attachments)
            {
                holds.Add(attachment.ToContract(observedAt));
            }

            current["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = holds };
            PopulateOriginalObservations(current, state);
        }

        if (bindings is not JsonObject || bindings["run"] is not JsonObject run
            || !Matches(run["runId"], state.RunId) || !Matches(run["attemptId"], state.AttemptId)
            || !Matches(run["environmentId"], state.EnvironmentId))
        {
            throw new InvalidDataException("The result does not match the original owner identities.");
        }

        static bool Matches(JsonNode? value, string identity) => value is JsonValue scalar
            && scalar.TryGetValue(out string? observed) && observed == identity;

        if (state.CandidatePolicy is { } issued
            && (run["attemptSequence"]?.GetValue<int>() != issued.AttemptSequence
                || !JsonNode.DeepEquals(bindings["candidate"], JsonNode.Parse(issued.Candidate.GetRawText()))
                || !JsonNode.DeepEquals(bindings["decision"], JsonNode.Parse(issued.Decision.GetRawText()))
                || !JsonNode.DeepEquals(bindings["reuseLineage"], JsonNode.Parse(issued.ReuseLineage.GetRawText()))))
        {
            throw new InvalidDataException("The result changed its issued candidate decision or lineage.");
        }

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
        bool local = state.ExecutionContext is { ValueKind: JsonValueKind.Object } context && context.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "local";
        string[] failureRunIds = [.. (record.FailureRetained ? new[] { record.RunId } : []).Concat(attachments.Where(item => local && item.Outcome == "failed").Select(item => item.AttachedRunId))];
        bool failureRetained = failureRunIds.Length > 0;
        bool retained = failureRetained && !removed;
        string retention = (retained, failureRetained && removed) switch
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
            toolIdentityUnavailable = state.ExecutingTool is null,
            startupDurationUnavailable = state.StartupDurationSeconds is null,
            startupInterruptionObserved = state.StartupInterruptionObserved,
        });

        // Content-addressed diagnostics remain immutable across result updates and retries.
        JsonElement diagnostic = JsonSerializer.Deserialize<JsonElement>(diagnosticBytes);
        byte[] retainedDiagnostic = System.Text.Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(diagnostic));
        string hash = Convert.ToHexStringLower(SHA256.HashData(retainedDiagnostic));
        string diagnosticIdentity = "sha256:" + hash;
        string diagnosticReference = "lifecycle-" + hash + ".json";
        await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(directory, diagnosticReference), directory, retainedDiagnostic, cancellationToken).ConfigureAwait(false);
        List<object> diagnostics = [new { id = "lifecycle", classification = "internal", redacted = true, reference = diagnosticReference, contentIdentity = diagnosticIdentity }];
        if (state.ExecutingTool is { } executor)
        {
            diagnostics.Add(await executor.RetainAsync(directory, cancellationToken).ConfigureAwait(false));
        }

        CompositionOwnerTermination? termination = record.Termination;
        if (termination is not null)
        {
            byte[] terminationBytes = Canonical(new
            {
                runId = state.RunId,
                ownedWork = termination.OwnedWork,
                environmentAccess = termination.EnvironmentAccess,
                leftoverWorkIds = termination.LeftoverWorkIds,
                credentialExpiresAtUtc = termination.CredentialExpiresAtUtc,
            });
            diagnostics.Add(await RetainedDiagnosticAsync(directory, "termination", "termination", terminationBytes, cancellationToken).ConfigureAwait(false));
        }

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

        JsonObject terminationNode = Unavailable();
        if (termination is not null)
        {
            terminationNode = Node(new
            {
                state = "populated",
                ownedWork = termination.OwnedWork.Select(item => new { id = item.Id, status = item.Status, observedAt = Utc(item.ObservedAtUtc), diagnosticIds = _terminationDiagnosticIds }),
                environmentAccess = termination.EnvironmentAccess.Select(item => new { id = item.Id, status = item.Status, observedAt = Utc(item.ObservedAtUtc), diagnosticIds = _terminationDiagnosticIds }),
                leftoverWorkIds = termination.LeftoverWorkIds,
                failures = termination.Failures,
            });
        }

        JsonObject document = new()
        {
            ["schema"] = TestEnvironmentContractReader.ResultSchema,
            ["policyId"] = TestEnvironmentContractReader.PolicyId,
            ["bindings"] = bindings,
            ["terminal"] = Node(new { outcome, recordedAt = Utc(record.TerminalAtUtc), cause, diagnosticIds }),

            ["termination"] = terminationNode,
            ["retention"] = Node(new { state = "populated", disposition = retention, failureRunIds, retainedResourceIds = retained ? leftovers : [], reason = retentionReason, recordedAt = now }),
            ["cleanup"] = cleanupNode,
            ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics),
        };
        return await PublishAsync(directory, document, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Publishes a runner-owned attached run's validated result with its populated termination.</summary>
    /// <param name="stateDirectory">The runner state directory.</param>
    /// <param name="owner">The owner's durable identities.</param>
    /// <param name="attachment">The ended attachment with its first outcome, cause and recorded termination.</param>
    /// <param name="attemptId">The attached attempt identity.</param>
    /// <param name="descriptorSnapshot">The validated pre-launch attached descriptor, or null when no child was prepared.</param>
    /// <param name="timing">The attached execution clock, when execution was activated.</param>
    /// <param name="reportBytes">The inspected native report, when retained.</param>
    /// <param name="ruleIds">The classified rule identities of the first outcome.</param>
    /// <param name="cancellationToken">The publication allowance.</param>
    /// <returns>The absolute <c>diagnostics/&lt;attachedRunId&gt;/test-result.json</c> location.</returns>
    /// <exception cref="InvalidDataException">The known observations cannot form a valid contract.</exception>
    /// <exception cref="IOException">The destination no longer satisfies the owned-path policy.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "Published content identities require lowercase SHA-256 hexadecimal.")]
    internal static async Task<string> WriteAttachedAsync(string stateDirectory, CompositionRunState owner, CompositionAttachment attachment, string attemptId, byte[]? descriptorSnapshot, ModuleTestExecutionTiming? timing, byte[]? reportBytes, IReadOnlyList<string> ruleIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(ruleIds);
        if (attachment is not { Outcome: "passed" or "failed" or "cancelled" or "withdrawn", EndedAtUtc: { } recordedAt, Execution: { Cause: { } cause } execution, Termination: { } termination }
            || attachment.OwnerRunId != owner.RunId || owner.EnvironmentId is null || attachment.EnvironmentId != owner.EnvironmentId || !CompositionRunPlanFactory.IsRunId(attemptId))
        {
            throw new InvalidDataException("The attached run has no recorded first outcome, cause and termination.");
        }

        string directory = Path.GetFullPath(Path.Combine(stateDirectory, "diagnostics", attachment.AttachedRunId));
        string ownerDirectory = Path.Combine(stateDirectory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        JsonNode bindings;
        if (descriptorSnapshot is not null)
        {
            using JsonDocument descriptor = JsonDocument.Parse(descriptorSnapshot);
            bindings = JsonNode.Parse(descriptor.RootElement.GetProperty("bindings").GetRawText())!;
        }
        else
        {
            bindings = AttachedPartialBindings(owner, attachment, attemptId, timing);
        }

        if (bindings is not JsonObject current || current["run"] is not JsonObject run
            || run["role"]?.GetValue<string>() != "attached" || run["runId"]?.GetValue<string>() != attachment.AttachedRunId
            || run["ownerRunId"]?.GetValue<string>() != owner.RunId || run["attemptId"]?.GetValue<string>() != attemptId)
        {
            throw new InvalidDataException("The attached result does not match the issued attached identities.");
        }

        // Holds evolve after launch, so the result snapshots the owner's durable records, including this run's own ended hold.
        JsonArray holds = [];
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        foreach (CompositionAttachment hold in await CompositionAttachmentStore.ReadAsync(ownerDirectory, owner.RunId, cancellationToken).ConfigureAwait(false))
        {
            holds.Add(hold.ToContract(observedAt));
        }

        current["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = holds };
        PopulateOriginalObservations(current, owner);
        if (timing is { ActualDurationSeconds: { } actual } && current["deadlines"]?["execution"] is JsonObject executionDeadline
            && executionDeadline["state"]?.GetValue<string>() == "populated")
        {
            executionDeadline["actualDurationSeconds"] = actual;
        }

        bool local = owner.ExecutionContext is { ValueKind: JsonValueKind.Object } context && context.TryGetProperty("kind", out JsonElement kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "local";
        bool failureRetained = local && attachment.Outcome == "failed";
        string now = Utc(observedAt);

        // The access statement is the recorded access observation itself, so a failed item never claims "no holder".
        string accessStatement = string.Join(" ", termination.EnvironmentAccess.Select(item => item.Status + ": " + item.Detail));
        byte[] lifecycle = Canonical(new
        {
            runId = attachment.AttachedRunId,
            ownerRunId = owner.RunId,
            role = "attached",
            outcome = attachment.Outcome,
            cause,
            endReason = attachment.EndReason,
            exitCode = execution.ExitCode,
            toolIdentityUnavailable = owner.ExecutingTool is null,
            startupDurationUnavailable = owner.StartupDurationSeconds is null,
            startupInterruptionObserved = owner.StartupInterruptionObserved,
            ruleIds = ruleIds.Where(rule => rule.Length is >= 5 and <= 8 && rule.StartsWith("HX", StringComparison.Ordinal) && rule.All(char.IsAsciiLetterOrDigit)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
        });
        byte[] terminationDetail = Canonical(new
        {
            runId = attachment.AttachedRunId,
            recorder = termination.Recorder,
            ownedWork = termination.OwnedWork.Select(item => new { id = item.Id, status = item.Status, detail = item.Detail }),
            environmentAccess = termination.EnvironmentAccess.Select(item => new { id = item.Id, status = item.Status, detail = item.Detail }),
            leftoverWorkIds = termination.LeftoverWorkIds,
            access = accessStatement,
        });
        List<object> diagnostics =
        [
            await RetainedDiagnosticAsync(directory, "lifecycle", "lifecycle", lifecycle, cancellationToken).ConfigureAwait(false),
            await RetainedDiagnosticAsync(directory, "termination", "termination", terminationDetail, cancellationToken).ConfigureAwait(false),
        ];
        if (owner.ExecutingTool is { } executor)
        {
            diagnostics.Add(await executor.RetainAsync(directory, cancellationToken).ConfigureAwait(false));
        }

        if (reportBytes is { Length: > 0 })
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(reportBytes));
            string reference = "native-" + hash + ".trx";
            if (!File.Exists(Path.Combine(directory, reference)))
            {
                await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(directory, reference), directory, reportBytes, cancellationToken).ConfigureAwait(false);
            }

            diagnostics.Add(new { id = "native-report", classification = "internal", redacted = true, reference, contentIdentity = "sha256:" + hash });
        }

        string[] terminationIds = _terminationDiagnosticIds;
        JsonObject document = new()
        {
            ["schema"] = TestEnvironmentContractReader.ResultSchema,
            ["policyId"] = TestEnvironmentContractReader.PolicyId,
            ["bindings"] = bindings,
            ["terminal"] = Node(new { outcome = attachment.Outcome, recordedAt = Utc(recordedAt), cause, diagnosticIds = _lifecycleDiagnosticIds }),
            ["termination"] = Node(new
            {
                state = "populated",
                ownedWork = termination.OwnedWork.Select(item => new { id = item.Id, status = item.Status, observedAt = Utc(item.ObservedAtUtc), diagnosticIds = terminationIds }),
                environmentAccess = termination.EnvironmentAccess.Select(item => new { id = item.Id, status = item.Status, observedAt = Utc(item.ObservedAtUtc), diagnosticIds = terminationIds }),
                leftoverWorkIds = termination.LeftoverWorkIds,
                failures = termination.Failures,
            }),
            ["retention"] = Node(new
            {
                state = "populated",
                disposition = failureRetained ? "failure-retained" : "not-retained",
                failureRunIds = failureRetained ? new[] { attachment.AttachedRunId } : [],
                retainedResourceIds = Array.Empty<string>(),
                reason = RetentionReason(failureRetained, local),
                recordedAt = now,
            }),
            ["cleanup"] = Node(new { state = "populated", disposition = "not-requested", observedAt = now, ownedResourceIds = Array.Empty<string>(), leftoverResourceIds = Array.Empty<string>(), failures = Array.Empty<string>(), diagnosticIds = _lifecycleDiagnosticIds }),
            ["diagnostics"] = JsonSerializer.SerializeToNode(diagnostics),
        };
        return await PublishAsync(directory, document, cancellationToken).ConfigureAwait(false);
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

    private static async Task<string> PublishAsync(string directory, JsonObject document, CancellationToken cancellationToken)
    {
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

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1244:Floating point numbers should not be tested for equality", Justification = "Immutable startup budgets and frozen observations require exact equality; tolerance would authorize a different binding.")]
    private static void PopulateOriginalObservations(JsonObject bindings, CompositionRunState owner)
    {
        if (owner.ExecutingTool is { } executor)
        {
            JsonNode tool = JsonSerializer.SerializeToNode(executor.ToContract())!;
            if (bindings["tool"] is JsonObject existing && existing["state"]?.GetValue<string>() == "populated")
            {
                if (!JsonNode.DeepEquals(existing["name"], tool["name"]) || !JsonNode.DeepEquals(existing["version"], tool["version"])
                    || !JsonNode.DeepEquals(existing["contentIdentity"], tool["contentIdentity"]))
                {
                    throw new InvalidDataException("The result changed its original executing tool.");
                }

                // Qualifications belong to independent authorities; original snapshot references never change here.
            }
            else
            {
                bindings["tool"] = tool;
            }
        }

        if (bindings["deadlines"] is JsonObject deadlines && deadlines["startup"] is JsonObject startup
            && startup["state"]?.GetValue<string>() == "populated")
        {
            bool valid = startup["origin"]?.GetValue<string>() == "environment-requested"
                && (owner.CreatedAt is null || SameTime(startup["originAt"], owner.CreatedAt.Value))
                && (owner.StartupDeadlineUtc is null || SameTime(startup["expiresAt"], owner.StartupDeadlineUtc.Value));
            if (owner.EffectiveDeadlineSeconds is { } seconds)
            {
                JsonNode expectedOverride = owner.EffectiveDeadlineIsOverride
                    ? Node(new { state = "declared", requestedSeconds = seconds, justification = owner.EffectiveDeadlineJustification })
                    : Node(new { state = "not-overridden" });
                valid = valid && startup["effectiveSeconds"] is JsonValue budget && budget.TryGetValue(out double effective) && effective.Equals(seconds)
                    && JsonNode.DeepEquals(startup["override"], expectedOverride);
            }

            if (!valid)
            {
                throw new InvalidDataException("The result changed its original startup binding.");
            }

            if (owner.StartupDurationSeconds is { } duration)
            {
                if (startup["actualDurationSeconds"] is JsonValue actual && (!actual.TryGetValue(out double observed) || !observed.Equals(duration)))
                {
                    throw new InvalidDataException("The result changed its frozen startup duration.");
                }

                startup["actualDurationSeconds"] = duration;
            }
        }
    }

    private static bool SameTime(JsonNode? node, DateTimeOffset expected) => node is JsonValue value && value.TryGetValue(out string? text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset actual) && actual == expected;

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
            ["run"] = Node(new { runId = state.RunId, attemptId = state.AttemptId, attemptSequence = state.CandidatePolicy?.AttemptSequence ?? 1, ownerRunId = state.RunId, environmentId = state.EnvironmentId, role = "owner" }),
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
            ["candidate"] = state.CandidatePolicy is { } selected ? JsonNode.Parse(selected.Candidate.GetRawText()) : Node(new { state = "populated", classification = "baseline-only", reason = "No authenticated candidate request was selected." }),
            ["decision"] = state.CandidatePolicy is { } decision ? JsonNode.Parse(decision.Decision.GetRawText()) : Unavailable(),
            ["reuseLineage"] = state.CandidatePolicy is { } lineage ? JsonNode.Parse(lineage.ReuseLineage.GetRawText()) : Unavailable(),
            ["artifacts"] = new JsonObject { ["expected"] = Unavailable(), ["loaded"] = Unavailable() },
            ["suite"] = new JsonObject { ["declared"] = Unavailable(), ["completion"] = Unavailable() },
            ["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = new JsonArray() },
            ["deadlines"] = new JsonObject { ["startup"] = startup, ["execution"] = execution },
        };
    }

    private static async Task<object> RetainedDiagnosticAsync(string directory, string id, string prefix, byte[] content, CancellationToken cancellationToken)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(content));
        string reference = prefix + "-" + hash + ".json";
        if (!File.Exists(Path.Combine(directory, reference)))
        {
            await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(directory, reference), directory, content, cancellationToken).ConfigureAwait(false);
        }

        return new { id, classification = "internal", redacted = true, reference, contentIdentity = "sha256:" + hash };
    }

    private static byte[] Canonical<T>(T value) => System.Text.Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(JsonSerializer.SerializeToElement(value)));

    private static JsonObject AttachedPartialBindings(CompositionRunState owner, CompositionAttachment attachment, string attemptId, ModuleTestExecutionTiming? timing)
    {
        object startupOverride = owner.EffectiveDeadlineIsOverride
            ? new { state = "declared", requestedSeconds = owner.EffectiveDeadlineSeconds, justification = owner.EffectiveDeadlineJustification }
            : new { state = "not-overridden" };
        JsonNode startup = owner.StartupDeadlineUtc is { } expires && owner.EffectiveDeadlineSeconds is { } seconds && owner.CreatedAt is { } origin
            ? Node(new { state = "populated", origin = "environment-requested", originAt = Utc(origin), effectiveSeconds = seconds, expiresAt = Utc(expires), @override = startupOverride })
            : Unavailable();
        JsonNode execution = timing is not null
            ? Node(new { state = "populated", origin = "test-execution-started", originAt = Utc(timing.StartedAtUtc), effectiveSeconds = timing.EffectiveSeconds, expiresAt = Utc(timing.DeadlineUtc), configurationContentIdentity = timing.ConfigurationContentIdentity })
            : Unavailable();
        JsonNode context = owner.ExecutionContext is { } actual ? JsonNode.Parse(actual.GetRawText())! : throw new InvalidDataException("Original execution context is unavailable.");
        return new JsonObject
        {
            ["run"] = JsonSerializer.SerializeToNode(TestEnvironmentDescriptorWriter.AttachedRun(attachment.AttachedRunId, attemptId, owner.RunId, owner.EnvironmentId!)),
            ["environment"] = new JsonObject
            {
                ["composition"] = Unavailable(),
                ["mode"] = attachment.Execution!.Mode,
                ["executionContext"] = context,
                ["catalog"] = Unavailable(),
                ["isolation"] = Unavailable(),
                ["resources"] = Unavailable(),
                ["readiness"] = Unavailable(),
                ["startupTasks"] = Unavailable(),
            },
            ["tool"] = Unavailable(),
            ["candidate"] = JsonSerializer.SerializeToNode(TestEnvironmentDescriptorWriter.AttachedCandidate()),
            ["decision"] = Unavailable(),
            ["reuseLineage"] = Unavailable(),
            ["artifacts"] = new JsonObject { ["expected"] = Unavailable(), ["loaded"] = Unavailable() },
            ["suite"] = new JsonObject { ["declared"] = Unavailable(), ["completion"] = Unavailable() },
            ["attachments"] = new JsonObject { ["state"] = "populated", ["holds"] = new JsonArray() },
            ["deadlines"] = new JsonObject { ["startup"] = startup, ["execution"] = execution },
        };
    }

    private static string RetentionReason(bool failureRetained, bool local) => (failureRetained, local) switch
    {
        (true, _) => "local-attached-failure",
        (false, true) => "local-failure-retention-not-established",
        (false, false) => "ci-never-retains",
    };

    private static bool SafeIdentity(string value) => value is { Length: > 0 and <= 2048 } && !value.Any(char.IsControl) && !Manifest.ManifestSecretDetector.ContainsSecret(value);

    private static JsonObject Unavailable() => new() { ["state"] = "not-yet-populated" };

    private static JsonObject Node<T>(T value) => (JsonObject)JsonSerializer.SerializeToNode(value)!;

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}