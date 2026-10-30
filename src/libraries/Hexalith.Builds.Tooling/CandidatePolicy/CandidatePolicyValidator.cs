// <copyright file="CandidatePolicyValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CandidatePolicy;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

/// <summary>Applies the published selection and acceptance policy without treating JSON claims as authority.</summary>
public static class CandidatePolicyValidator
{
    private static readonly string[] _immutableFields = ["run", "candidate", "decision", "reuseLineage", "tool"];
    private static readonly string[] _environmentFields = ["composition", "mode", "executionContext", "catalog", "isolation", "resources"];
    private static readonly string[] _qualifications = ["buildsAdoption", "platformAcceptance", "integrationQualification"];
    private static readonly string[] _consumers = ["composed-host", "mcpcli", "tests"];
    private static readonly string[] _countFields = ["required", "executed", "completed", "passed"];

    /// <summary>Checks the selected suite and actual execution before provisioning.</summary>
    /// <param name="binding">The authenticated candidate.</param>
    /// <param name="moduleId">The selected declaration owner.</param>
    /// <param name="mode">The selected artifact mode.</param>
    /// <param name="context">The actual execution context.</param>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    public static void ValidateSelection(CandidatePolicyBinding binding, string moduleId, string mode, JsonElement context)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.Candidate.GetProperty("moduleId").GetString() != moduleId)
        {
            throw new CandidatePolicyException("candidate-module-mismatch");
        }

        if (mode != "package")
        {
            throw new CandidatePolicyException("package-release-ci-required");
        }

        RequireCi(context);
    }

    /// <summary>Rechecks authority and descriptor/result agreement before accepting candidate evidence.</summary>
    /// <param name="gate">The independently configured gate transport.</param>
    /// <param name="requestPath">The signed original request.</param>
    /// <param name="descriptorPath">The original descriptor.</param>
    /// <param name="resultPath">The terminal result.</param>
    /// <param name="cancellationToken">The operation cancellation.</param>
    /// <returns>The completed validation, or a stable refusal.</returns>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    public static async Task ValidateAcceptanceAsync(CandidateGateClient gate, string requestPath, string descriptorPath, string resultPath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gate);
        CandidatePolicyBinding authority = await gate.ResolveAsync(requestPath, cancellationToken).ConfigureAwait(false);
        TestEnvironmentContractReadResult descriptor = TestEnvironmentContractReader.ReadDescriptor(descriptorPath, cancellationToken);
        TestEnvironmentContractReadResult result = TestEnvironmentContractReader.ReadResult(resultPath, cancellationToken);
        if (!descriptor.IsValid || !result.IsValid)
        {
            ValidateRawPolicy(descriptorPath, cancellationToken);
            ValidateRawPolicy(resultPath, cancellationToken);
            throw new CandidatePolicyException("contract-invalid");
        }

        ValidateEvidence(authority, descriptor.Document!.Value, result.Document!.Value);
        JsonElement bindings = result.Document.Value.GetProperty("bindings");
        await gate.VerifyExecutionAsync(authority, bindings.GetProperty("environment").GetProperty("executionContext"), cancellationToken).ConfigureAwait(false);

        CandidatePolicyBinding current = await gate.ResolveAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (!Equal(current.Decision, authority.Decision))
        {
            throw new CandidatePolicyException("decision-producer-mismatch");
        }

        // No enrolled, authenticated qualification producer exists yet. JSON reference IDs cannot enroll one.
        // Story 3.9 supplies loaded identities; live tool acceptance and qualification remain independent prerequisites.
        throw new CandidatePolicyException("tool-qualification-authentication-unavailable");
    }

    /// <summary>Compares existing v1 representations and refuses missing or contradictory evidence.</summary>
    /// <param name="authority">The fresh authenticated original decision.</param>
    /// <param name="descriptor">The validated original descriptor.</param>
    /// <param name="result">The validated terminal result.</param>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    public static void ValidateEvidence(CandidatePolicyBinding authority, JsonElement descriptor, JsonElement result)
    {
        ArgumentNullException.ThrowIfNull(authority);
        try
        {
            if (S(descriptor, "policyId") != TestEnvironmentContractReader.PolicyId || S(result, "policyId") != TestEnvironmentContractReader.PolicyId)
            {
                throw new CandidatePolicyException("exact-policy-required");
            }

            JsonElement before = descriptor.GetProperty("bindings");
            JsonElement after = result.GetProperty("bindings");
            JsonElement candidate = after.GetProperty("candidate");
            if (!Populated(candidate) || S(candidate, "classification") != "candidate")
            {
                throw new CandidatePolicyException("candidate-required-diagnostics-only");
            }

            ValidateLineage(after, authority);
            JsonElement run = after.GetProperty("run");
            if (!Equal(candidate, authority.Candidate) || !Equal(after.GetProperty("decision"), authority.Decision)
                || S(run, "runId") != authority.RunId || S(run, "attemptId") != authority.AttemptId || run.GetProperty("attemptSequence").GetInt32() != authority.AttemptSequence)
            {
                throw new CandidatePolicyException("decision-producer-mismatch");
            }

            if (_immutableFields.Any(field => !Equal(before.GetProperty(field), after.GetProperty(field))))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            JsonElement environment = after.GetProperty("environment");
            JsonElement priorEnvironment = before.GetProperty("environment");
            if (_environmentFields.Any(field => !Equal(priorEnvironment.GetProperty(field), environment.GetProperty(field))))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            JsonElement priorDeadlines = before.GetProperty("deadlines");
            JsonElement currentDeadlines = after.GetProperty("deadlines");
            if (!EqualDeadline(priorDeadlines.GetProperty("startup"), currentDeadlines.GetProperty("startup"))
                || !EqualDeadline(priorDeadlines.GetProperty("execution"), currentDeadlines.GetProperty("execution")))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            ValidateStartupDeclarations(priorEnvironment.GetProperty("startupTasks"), environment.GetProperty("startupTasks"));
            ValidateHoldProgression(before.GetProperty("attachments"), after.GetProperty("attachments"));
            ValidateSelection(authority, S(candidate, "moduleId"), S(environment, "mode"), environment.GetProperty("executionContext"));
            if (S(result.GetProperty("terminal"), "outcome") != "passed")
            {
                throw new CandidatePolicyException("passed-outcome-required");
            }

            if (!Populated(currentDeadlines.GetProperty("startup")))
            {
                throw new CandidatePolicyException("passing-duration-required");
            }

            ValidatePassingDuration(currentDeadlines.GetProperty("startup"));
            ValidatePassingDuration(currentDeadlines.GetProperty("execution"));
            JsonElement readiness = environment.GetProperty("readiness");
            JsonElement tasks = environment.GetProperty("startupTasks");
            if (!Populated(readiness) || S(readiness, "status") != "ready" || !Populated(tasks)
                || tasks.GetProperty("tasks").EnumerateArray().Any(task => task.GetProperty("required").GetBoolean() && S(task, "status") != "passed")
                || !Populated(environment.GetProperty("isolation")) || !environment.GetProperty("composition").TryGetProperty("contentIdentity", out _)
                || !environment.GetProperty("catalog").TryGetProperty("commit", out _) || environment.GetProperty("resources").ValueKind != JsonValueKind.Array
                || environment.GetProperty("resources").GetArrayLength() == 0
                || readiness.GetProperty("resources").GetArrayLength() != environment.GetProperty("resources").GetArrayLength()
                || readiness.GetProperty("resources").EnumerateArray().Any(resource => S(resource, "status") != "ready"))
            {
                throw new CandidatePolicyException("ready-owned-environment-required");
            }

            if (S(run, "role") == "attached")
            {
                JsonElement holds = after.GetProperty("attachments");
                if (!Populated(holds) || !holds.GetProperty("holds").EnumerateArray().Any(hold => S(hold, "attachedRunId") == authority.RunId && S(hold, "ownerRunId") == S(run, "ownerRunId") && S(hold, "environmentId") == S(run, "environmentId")))
                {
                    throw new CandidatePolicyException("same-ci-job-required");
                }
            }

            JsonElement artifacts = after.GetProperty("artifacts");
            if (!Equal(before.GetProperty("artifacts").GetProperty("expected"), artifacts.GetProperty("expected")))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            ValidateArtifacts(authority, artifacts);
            JsonElement suite = after.GetProperty("suite");
            if (!Equal(before.GetProperty("suite").GetProperty("declared"), suite.GetProperty("declared")))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            ValidateSuite(suite);
            JsonElement tool = after.GetProperty("tool");
            if (!Populated(tool))
            {
                throw new CandidatePolicyException("tool-qualification-required");
            }

            foreach (string field in _qualifications)
            {
                JsonElement qualification = tool.GetProperty(field);
                if (!Populated(qualification) || S(qualification, "toolVersion") != S(tool, "version")
                    || S(qualification, "toolContentIdentity") != S(tool, "contentIdentity") || S(qualification, "policyId") != TestEnvironmentContractReader.PolicyId)
                {
                    throw new CandidatePolicyException("tool-qualification-required");
                }
            }
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new CandidatePolicyException("required-proof-unavailable");
        }
    }

    /// <summary>Refuses foreign evidence, including evidence for unchanged modules.</summary>
    /// <param name="bindings">The claimed result bindings.</param>
    /// <param name="authority">The independently authenticated producer.</param>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    /// <exception cref="ArgumentNullException">A required collaborator is absent.</exception>
    public static void ValidateLineage(JsonElement bindings, CandidatePolicyBinding authority)
    {
        ArgumentNullException.ThrowIfNull(authority);
        JsonElement lineage = bindings.GetProperty("reuseLineage");
        if (!Populated(lineage) || lineage.GetProperty("reuseAllowed").GetBoolean() || lineage.GetProperty("sourceRunIds").GetArrayLength() != 0
            || lineage.GetProperty("sourceDecisionIds").GetArrayLength() != 0)
        {
            throw new CandidatePolicyException("no-cross-decision-reuse");
        }

        if (S(lineage, "producingRunId") != authority.RunId || S(lineage, "producingDecisionId") != S(authority.Decision, "requestId"))
        {
            throw new CandidatePolicyException("lineage-producer-mismatch");
        }
    }

    /// <summary>Requires disposable Linux GitHub-hosted CI, independently authenticated by the gate client.</summary>
    /// <param name="context">The actual or recorded execution context.</param>
    /// <exception cref="CandidatePolicyException">A named policy prerequisite is not satisfied.</exception>
    public static void RequireCi(JsonElement context)
    {
        if (S(context, "kind") != "ci")
        {
            throw new CandidatePolicyException("local-diagnostics-only");
        }

        if (S(context, "runnerKind") != "github-hosted" || S(context, "operatingSystem") != "Linux" || !context.GetProperty("disposable").GetBoolean())
        {
            throw new CandidatePolicyException("github-hosted-linux-required");
        }
    }

    private static void ValidateArtifacts(CandidatePolicyBinding authority, JsonElement artifacts)
    {
        JsonElement expected = artifacts.GetProperty("expected");
        JsonElement loaded = artifacts.GetProperty("loaded");
        if (!Populated(expected) || !Populated(loaded) || expected.GetProperty("items").GetArrayLength() == 0 || loaded.GetProperty("items").GetArrayLength() == 0)
        {
            throw new CandidatePolicyException("artifact-identities-required");
        }

        JsonElement[] items = [.. expected.GetProperty("items").EnumerateArray()];
        JsonElement[] observed = [.. loaded.GetProperty("items").EnumerateArray()];
        string module = S(authority.Candidate, "moduleId");
        if (items.Select(item => (S(item, "artifactId"), S(item, "moduleId"))).Distinct().Count() != items.Length
            || !items.Any(item => S(item, "moduleId") == module)
            || items.Any(item => S(item, "moduleId") == module && (S(item, "configuration") != "Release" || S(item, "committedRevision") != S(authority.Candidate, "committedRevision") || S(item, "repository") != S(authority.Candidate, "repository")))
            || observed.Any(item => !items.Any(expectedItem => SameArtifact(item, expectedItem)))
            || items.Any(item => !observed.Any(actual => SameArtifact(item, actual)))
            || _consumers.Any(consumer => !observed.Any(item => S(item, "consumerKind") == consumer)))
        {
            throw new CandidatePolicyException("artifact-identity-mismatch");
        }
    }

    private static void ValidateSuite(JsonElement suite)
    {
        JsonElement declared = suite.GetProperty("declared");
        JsonElement completion = suite.GetProperty("completion");
        if (!Populated(declared) || !Populated(completion))
        {
            throw new CandidatePolicyException("complete-required-suite-proof-required");
        }

        string[] required = [.. declared.GetProperty("requiredTestIds").EnumerateArray().Select(test => test.GetString()!)];
        JsonElement[] tests = [.. completion.GetProperty("tests").EnumerateArray()];
        JsonElement counts = completion.GetProperty("counts");
        if (required.Length == 0 || required.Distinct(StringComparer.Ordinal).Count() != required.Length
            || S(declared, "suiteContentIdentity") != S(completion, "executedSuiteContentIdentity") || S(declared, "configurationContentIdentity") != S(completion, "executedConfigurationContentIdentity")
            || S(declared, "requiredTestSetContentIdentity") != S(completion, "requiredTestSetContentIdentity")
            || tests.Length != required.Length || !tests.Select(test => S(test, "testId")).Order(StringComparer.Ordinal).SequenceEqual(required.Order(StringComparer.Ordinal))
            || tests.Any(test => S(test, "outcome") != "passed")
            || _countFields.Any(field => counts.GetProperty(field).GetInt32() != required.Length)
            || counts.GetProperty("failed").GetInt32() != 0 || counts.GetProperty("skipped").GetInt32() != 0)
        {
            throw new CandidatePolicyException("complete-required-suite-proof-required");
        }
    }

    private static bool SameArtifact(JsonElement left, JsonElement right) => S(left, "artifactId") == S(right, "artifactId")
        && S(left, "moduleId") == S(right, "moduleId") && S(left, "contentIdentity") == S(right, "contentIdentity");

    private static void ValidateStartupDeclarations(JsonElement before, JsonElement after)
    {
        if (Populated(before) != Populated(after))
        {
            throw new CandidatePolicyException("descriptor-result-disagreement");
        }

        if (!Populated(before))
        {
            return;
        }

        JsonElement[] original = [.. before.GetProperty("tasks").EnumerateArray()];
        JsonElement[] current = [.. after.GetProperty("tasks").EnumerateArray()];
        if (original.Length != current.Length || original.Any(task => current.Count(item => S(item, "taskId") == S(task, "taskId") && item.GetProperty("required").GetBoolean() == task.GetProperty("required").GetBoolean()) != 1))
        {
            throw new CandidatePolicyException("descriptor-result-disagreement");
        }
    }

    private static void ValidateHoldProgression(JsonElement before, JsonElement after)
    {
        if (!Populated(before))
        {
            return;
        }

        if (!Populated(after))
        {
            throw new CandidatePolicyException("descriptor-result-disagreement");
        }

        JsonElement[] current = [.. after.GetProperty("holds").EnumerateArray()];
        foreach (JsonElement prior in before.GetProperty("holds").EnumerateArray())
        {
            JsonElement[] matching = [.. current.Where(item => S(item, "originalAttachmentRequestId") == S(prior, "originalAttachmentRequestId"))];
            if (matching.Length != 1)
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            JsonObject original = JsonNode.Parse(prior.GetRawText())!.AsObject();
            JsonObject observed = JsonNode.Parse(matching[0].GetRawText())!.AsObject();
            if (S(prior.GetProperty("end"), "state") == "ended" && !Equal(prior.GetProperty("end"), matching[0].GetProperty("end")))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }

            _ = original.Remove("end");
            _ = observed.Remove("end");
            if (!JsonNode.DeepEquals(original, observed))
            {
                throw new CandidatePolicyException("descriptor-result-disagreement");
            }
        }
    }

    private static void ValidatePassingDuration(JsonElement deadline)
    {
        if (Populated(deadline) && !deadline.TryGetProperty("actualDurationSeconds", out _))
        {
            throw new CandidatePolicyException("passing-duration-required");
        }

        if (deadline.TryGetProperty("actualDurationSeconds", out JsonElement actual) && deadline.TryGetProperty("effectiveSeconds", out JsonElement budget)
            && actual.GetDouble() > budget.GetDouble())
        {
            throw new CandidatePolicyException("passing-deadline-exceeded");
        }
    }

    private static void ValidateRawPolicy(string path, CancellationToken cancellationToken)
    {
        try
        {
            using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path, respectSharing: true);
            if (!stream.CanSeek || stream.Length > TestEnvironmentContractReader.MaximumDocumentBytes)
            {
                return;
            }

            byte[] bytes = new byte[checked((int)stream.Length)];
            stream.ReadExactly(bytes);
            cancellationToken.ThrowIfCancellationRequested();
            int offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            using JsonDocument document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset), new JsonDocumentOptions { MaxDepth = TestEnvironmentContractReader.MaximumDepth });
            JsonElement root = document.RootElement;
            if (JsonDuplicatePropertyValidator.FindDuplicateProperty(root) is not null)
            {
                return;
            }

            if (!root.TryGetProperty("policyId", out JsonElement policy) || policy.ValueKind != JsonValueKind.String || policy.GetString() != TestEnvironmentContractReader.PolicyId)
            {
                throw new CandidatePolicyException("exact-policy-required");
            }

            if (root.TryGetProperty("bindings", out JsonElement bindings) && bindings.TryGetProperty("reuseLineage", out JsonElement lineage)
                && ((lineage.TryGetProperty("reuseAllowed", out JsonElement allowed) && allowed.ValueKind == JsonValueKind.True)
                    || (lineage.TryGetProperty("sourceRunIds", out JsonElement runs) && runs.ValueKind == JsonValueKind.Array && runs.GetArrayLength() > 0)
                    || (lineage.TryGetProperty("sourceDecisionIds", out JsonElement decisions) && decisions.ValueKind == JsonValueKind.Array && decisions.GetArrayLength() > 0)))
            {
                throw new CandidatePolicyException("no-cross-decision-reuse");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException or InvalidOperationException)
        {
            // Raw inspection only names policy refusals; invalid documents still fail the strict reader.
        }
    }

    private static bool EqualDeadline(JsonElement left, JsonElement right)
    {
        JsonObject prior = JsonNode.Parse(left.GetRawText())!.AsObject();
        JsonObject current = JsonNode.Parse(right.GetRawText())!.AsObject();
        _ = prior.Remove("actualDurationSeconds");
        _ = current.Remove("actualDurationSeconds");
        return JsonNode.DeepEquals(prior, current);
    }

    private static string S(JsonElement value, string field) => value.GetProperty(field).GetString()!;

    private static bool Populated(JsonElement value) => S(value, "state") == "populated";

    private static bool Equal(JsonElement left, JsonElement right) => JsonNode.DeepEquals(JsonNode.Parse(left.GetRawText()), JsonNode.Parse(right.GetRawText()));
}
