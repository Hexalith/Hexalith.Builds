// <copyright file="CandidatePolicyTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.CandidatePolicy;

using Shouldly;

using Xunit;

/// <summary>Exercises authenticated issuance, current-state changes, retries and policy refusals.</summary>
public sealed class CandidatePolicyTests
{
    private static readonly int[] _twoPages = [1, 2];
    private static readonly int[] _twoPageRechecks = [1, 2, 1, 2];

    /// <summary>Both kinds bind their own committed revision and original gate request.</summary>
    /// <param name="release">Whether this is a distinct release dispatch.</param>
    /// <returns>The exact selected bindings.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuthenticatedPendingRequestSelectsExactCommitAsync(bool release)
    {
        using CandidateGateFixture fixture = new();
        if (release)
        {
            fixture.Payload["kind"] = "release";
            _ = fixture.Payload.Remove("pr");
            _ = fixture.Payload.Remove("epoch");
        }

        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        binding.Candidate.GetProperty("committedRevision").GetString().ShouldBe(CandidateGateFixture.Merge);
        binding.Decision.GetProperty("requestId").GetString().ShouldBe("github:Hexalith/Orders:101");
        binding.ReuseLineage.GetProperty("reuseAllowed").GetBoolean().ShouldBeFalse();
        binding.ReuseLineage.GetProperty("sourceRunIds").GetArrayLength().ShouldBe(0);
        binding.ReuseLineage.GetProperty("sourceDecisionIds").GetArrayLength().ShouldBe(0);
        await fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CandidatePolicyValidator.ValidateSelection(binding, "orders", "package", fixture.Context);
    }

    /// <summary>The GitHub-signed merge context binds M while authenticated workflow-run metadata binds H.</summary>
    /// <param name="claim">The contradictory signed or API observation.</param>
    /// <param name="condition">The named refusal.</param>
    /// <returns>The refusal for an incorrectly bound execution context.</returns>
    [Theory]
    [InlineData("sha", "pr-binding-mismatch")]
    [InlineData("ref", "pr-binding-mismatch")]
    [InlineData("event", "github-event-binding-mismatch")]
    [InlineData("api-head", "pr-binding-mismatch")]
    public async Task PrMergeRequiresSignedMergeContextAndApiHeadAsync(string claim, string condition)
    {
        using CandidateGateFixture fixture = new();
        fixture.SignedSha = claim == "sha" ? CandidateGateFixture.Head : null;
        fixture.SignedRef = claim == "ref" ? "refs/pull/7/head" : null;
        fixture.SignedEvent = claim == "event" ? "workflow_dispatch" : null;
        fixture.RunHead = claim == "api-head" ? CandidateGateFixture.Merge : null;
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe(condition);
    }

    /// <summary>The approved hosted Ubuntu label authenticates Linux without a nonexistent generic API label.</summary>
    /// <param name="labels">The authenticated requested image labels.</param>
    /// <param name="group">The authenticated runner group.</param>
    /// <param name="eligible">Whether these match the approved hosted Linux job.</param>
    /// <returns>The execution hosting assertions.</returns>
    /// <exception cref="ArgumentNullException">The fixture labels are missing.</exception>
    [Theory]
    [InlineData("ubuntu-latest", "GitHub Actions", true)]
    [InlineData("Linux", "GitHub Actions", false)]
    [InlineData("windows-latest", "GitHub Actions", false)]
    [InlineData("macos-latest", "GitHub Actions", false)]
    [InlineData("ubuntu-latest", "Default", false)]
    [InlineData("self-hosted,ubuntu-latest", "GitHub Actions", false)]
    public async Task ExecutionUsesRealHostedRunnerLabelsAsync(string labels, string group, bool eligible)
    {
        ArgumentNullException.ThrowIfNull(labels);
        using CandidateGateFixture fixture = new() { JobLabels = labels.Split(','), JobRunnerGroup = group };
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (eligible)
        {
            await fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
        else
        {
            CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            refusal.Condition.ShouldBe("github-hosted-linux-required");
        }
    }

    /// <summary>Archived issuance and a token minted for an earlier nonce cannot prove a live consumer.</summary>
    /// <param name="mode">The invalid process proof.</param>
    /// <returns>The replay refusal.</returns>
    [Theory]
    [InlineData("issuance")]
    [InlineData("replay")]
    [InlineData("wrong-workflow")]
    public async Task LocalReplayAndUnapprovedConsumerProofRefuseAsync(string mode)
    {
        using CandidateGateFixture fixture = new() { ConsumerProofMode = mode };
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (mode == "replay")
        {
            await fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-authentication-required");
    }

    /// <summary>Unavailable job transport is not a caller cancellation and does not expose exception details.</summary>
    /// <param name="failure">The transport failure.</param>
    /// <returns>The stable unavailable-authority refusal.</returns>
    [Theory]
    [InlineData("timeout")]
    [InlineData("io")]
    public async Task ExecutionTransportFailuresRefuseAsUnavailableAsync(string failure)
    {
        using CandidateGateFixture fixture = new() { JobFailure = failure };
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("authority-unavailable");
        refusal.Message.ShouldNotContain("Private transport");
    }

    /// <summary>Caller cancellation remains cancellation at the consumer proof boundary.</summary>
    /// <returns>The requested cancellation assertion.</returns>
    [Fact]
    public async Task ExecutionPreservesRequestedCancellationAsync()
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        using CancellationTokenSource caller = new();
        await caller.CancelAsync().ConfigureAwait(true);
        _ = await Should.ThrowAsync<OperationCanceledException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, caller.Token)).ConfigureAwait(true);
    }

    /// <summary>A successful completion racing the final release refetch closes its request.</summary>
    /// <returns>The pending-only release refusal.</returns>
    [Fact]
    public async Task ReleaseCompletionRaceRefusesPendingDecisionAsync()
    {
        using CandidateGateFixture fixture = new() { CompleteOnFinalRefetch = true };
        fixture.Payload["kind"] = "release";
        _ = fixture.Payload.Remove("pr");
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("request-not-pending");
    }

    /// <summary>Filtered GitHub history cannot silently establish completeness at its result ceiling.</summary>
    /// <param name="mode">The missing or contradictory total-count observation.</param>
    /// <returns>The named incomplete-authority refusal.</returns>
    [Theory]
    [InlineData("missing")]
    [InlineData("ceiling")]
    [InlineData("truncated")]
    [InlineData("negative")]
    public async Task IncompleteWorkflowPaginationRefusesAsync(string mode)
    {
        using CandidateGateFixture fixture = new() { WorkflowPaginationMode = mode };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("authority-pagination-incomplete");
    }

    /// <summary>Current GitHub observations can invalidate saved authentic evidence.</summary>
    /// <param name="change">The authority change.</param>
    /// <param name="condition">The stable refusal.</param>
    /// <returns>The named policy refusal.</returns>
    [Theory]
    [InlineData("head", "pr-binding-mismatch")]
    [InlineData("base", "pr-binding-mismatch")]
    [InlineData("parents", "pr-binding-mismatch")]
    [InlineData("closed", "request-not-pending")]
    [InlineData("reopened", "fresh-request-required")]
    [InlineData("attempt", "latest-issued-attempt-required")]
    [InlineData("pending-successor", "latest-issued-attempt-required")]
    [InlineData("approval", "approval-unavailable")]
    [InlineData("review", "approval-unavailable")]
    [InlineData("retargeted", "fresh-request-required")]
    [InlineData("cancelled-successor", "latest-issued-attempt-required")]
    [InlineData("failed-successor", "latest-issued-attempt-required")]
    [InlineData("authentication", "authority-unavailable")]
    [InlineData("issuer", "issuer-authentication-required")]
    public async Task SavedIssuanceNeverOverridesCurrentAuthorityAsync(string change, string condition)
    {
        using CandidateGateFixture fixture = new();
        fixture.FabricatedIssuer = change == "issuer";
        string path = fixture.Sign();
        if (change != "issuer")
        {
            _ = await fixture.Gate.ResolveAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        fixture.ChangedHead = change == "head";
        fixture.ChangedBase = change == "base";
        fixture.WrongParents = change == "parents";
        fixture.PrState = change == "closed" ? "closed" : "open";
        fixture.ReopenEpoch = change is "reopened" or "retargeted" ? "9001" : null;
        fixture.EpochEvent = change == "retargeted" ? "base_ref_changed" : "reopened";
        fixture.CurrentAttempt = change is "attempt" or "cancelled-successor" or "failed-successor" ? 2 : 1;
        fixture.RunStatus = change is "cancelled-successor" or "failed-successor" ? "completed" : "in_progress";
        fixture.Conclusion = change == "cancelled-successor" ? "cancelled" : "failure";
        fixture.NewerRequest = change == "pending-successor";
        fixture.Approved = change != "approval";
        fixture.ReviewApproved = change != "review";
        fixture.Unavailable = change == "authentication";
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(path, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe(condition);
    }

    /// <summary>A retry racing the final authenticated lookup supersedes an earlier observation.</summary>
    /// <returns>The final refetch refusal.</returns>
    [Fact]
    public async Task AttemptIssuanceRaceIsRecheckedAsync()
    {
        using CandidateGateFixture fixture = new() { AdvanceOnFinalRefetch = true };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("latest-issued-attempt-required");
    }

    /// <summary>Retries preserve the pending decision but produce distinct signed attempts.</summary>
    /// <returns>The retry and supersession assertions.</returns>
    [Fact]
    public async Task RetryKeepsDecisionAndSupersedesEarlierPassingAttemptAsync()
    {
        using CandidateGateFixture fixture = new();
        string firstPath = fixture.Sign();
        CandidatePolicyBinding first = await fixture.Gate.ResolveAsync(firstPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.CurrentAttempt = 2;
        fixture.Payload["attemptSequence"] = 2;
        fixture.Payload["runId"] = Guid.NewGuid().ToString("N");
        fixture.Payload["attemptId"] = Guid.NewGuid().ToString("N");
        CandidatePolicyBinding retry = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        retry.Decision.GetProperty("requestId").GetString().ShouldBe(first.Decision.GetProperty("requestId").GetString());
        retry.AttemptSequence.ShouldBe(2);
        retry.RunId.ShouldNotBe(first.RunId);
        retry.AttemptId.ShouldNotBe(first.AttemptId);
        CandidatePolicyException superseded = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(firstPath, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        superseded.Condition.ShouldBe("latest-issued-attempt-required");
    }

    /// <summary>Separate release dispatches have different decisions even at identical bytes.</summary>
    /// <returns>The separate decision assertions.</returns>
    [Fact]
    public async Task NewReleaseDispatchRequiresFreshDecisionAsync()
    {
        using CandidateGateFixture fixture = new();
        fixture.Payload["kind"] = "release";
        _ = fixture.Payload.Remove("pr");
        CandidatePolicyBinding first = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.GateRunId = "102";
        fixture.Payload["gateRunId"] = "102";
        fixture.Payload["runId"] = Guid.NewGuid().ToString("N");
        fixture.Payload["attemptId"] = Guid.NewGuid().ToString("N");
        CandidatePolicyBinding second = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        second.Decision.GetProperty("requestId").GetString().ShouldNotBe(first.Decision.GetProperty("requestId").GetString());
        fixture.RunStatus = "completed";
        CandidatePolicyException completed = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        completed.Condition.ShouldBe("request-not-pending");
    }

    /// <summary>Unchanged modules receive no exception to the v1 no-reuse rule.</summary>
    /// <param name="foreignProducer">Whether the claimed producer is foreign instead of the source list.</param>
    /// <returns>The named refusal and original unmodified lineage.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignOrUnchangedModuleEvidenceRefusesReuseAsync(bool foreignProducer)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonObject lineage = JsonNode.Parse(binding.ReuseLineage.GetRawText())!.AsObject();
        if (foreignProducer)
        {
            lineage["producingRunId"] = "another-run";
        }
        else
        {
            lineage["sourceDecisionIds"] = new JsonArray("another-decision-with-identical-bytes");
        }

        JsonElement claims = JsonSerializer.SerializeToElement(new JsonObject { ["reuseLineage"] = lineage });
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateLineage(claims, binding)).Condition.ShouldBe(foreignProducer ? "lineage-producer-mismatch" : "no-cross-decision-reuse");
        binding.ReuseLineage.GetProperty("sourceDecisionIds").GetArrayLength().ShouldBe(0);
    }

    /// <summary>Local and source-mode execution cannot start a candidate environment.</summary>
    /// <returns>The diagnostic-only selection refusals.</returns>
    [Fact]
    public async Task LocalAndSourceRunsRemainDiagnosticOnlyAsync()
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateSelection(binding, "orders", "source", fixture.Context)).Condition.ShouldBe("package-release-ci-required");
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateSelection(binding, "orders", "package", JsonSerializer.SerializeToElement(new { kind = "local" }))).Condition.ShouldBe("local-diagnostics-only");
        Should.Throw<CandidatePolicyException>(() => CandidatePolicyValidator.ValidateSelection(binding, "another-module", "package", fixture.Context)).Condition.ShouldBe("candidate-module-mismatch");
    }

    /// <summary>Signed issuance authentication refuses individual key, header, issuer and payload substitutions.</summary>
    /// <param name="mutation">The isolated authentication violation.</param>
    /// <returns>The stable authentication refusal.</returns>
    [Theory]
    [InlineData("foreign")]
    [InlineData("tampered")]
    [InlineData("unknown")]
    [InlineData("non-rsa")]
    [InlineData("alg")]
    [InlineData("iss")]
    [InlineData("job_workflow_sha")]
    [InlineData("environment")]
    [InlineData("payload")]
    public async Task IssuanceAuthenticationRefusesSubstitutionsAsync(string mutation)
    {
        using CandidateGateFixture fixture = new();
        if (mutation is "foreign" or "tampered" or "unknown" or "non-rsa")
        {
            fixture.KeyMode = mutation;
        }
        else if (mutation == "alg")
        {
            fixture.Algorithm = "HS256";
        }
        else if (mutation != "payload")
        {
            fixture.IssuanceClaims = claims => claims[mutation] = "foreign-value";
        }

        string path = fixture.Sign();
        if (mutation == "payload")
        {
            JsonObject envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true))!.AsObject();
            fixture.Payload["moduleId"] = "substituted";
            envelope["payload"] = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(fixture.Payload));
            await File.WriteAllTextAsync(path, envelope.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(path, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-authentication-required");
    }

    /// <summary>A single mismatched consumer claim cannot authenticate the executing process.</summary>
    /// <param name="claim">The substituted claim.</param>
    /// <returns>The stable consumer authentication refusal.</returns>
    [Theory]
    [InlineData("repository")]
    [InlineData("run_id")]
    [InlineData("run_attempt")]
    [InlineData("sha")]
    [InlineData("runner_environment")]
    [InlineData("workflow_ref")]
    [InlineData("event_name")]
    [InlineData("ref")]
    [InlineData("stale")]
    public async Task ConsumerProofBindsEveryExecutionClaimAsync(string claim)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.ConsumerClaims = claims =>
        {
            if (claim == "stale")
            {
                claims["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 120;
            }
            else
            {
                claims[claim] = "foreign-value";
                if (claim == "ref")
                {
                    claims["workflow_ref"] = "Hexalith/Orders/.github/workflows/ci.yml@foreign-value";
                }
            }
        };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe(claim == "stale" ? "issuer-authentication-required" : "consumer-authentication-required");
    }

    /// <summary>Every actual CI job coordinate is bound independently of the signed request.</summary>
    /// <param name="field">The substituted context field.</param>
    /// <returns>The stable same-job refusal.</returns>
    [Theory]
    [InlineData("repository")]
    [InlineData("workflowRunId")]
    [InlineData("workflowAttempt")]
    [InlineData("jobId")]
    public async Task ExecutionContextBindsEveryJobCoordinateAsync(string field)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonObject context = JsonNode.Parse(fixture.Context.GetRawText())!.AsObject();
        context[field] = field == "workflowAttempt" ? JsonValue.Create(2) : JsonValue.Create("foreign-value");
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, JsonSerializer.SerializeToElement(context), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("same-ci-job-required");
    }

    /// <summary>The caller's authenticated path, ref and bytes must match the enrolled workflow.</summary>
    /// <param name="mutation">The caller workflow substitution.</param>
    /// <returns>The named workflow refusal.</returns>
    [Theory]
    [InlineData("bytes")]
    [InlineData("path")]
    [InlineData("ref")]
    public async Task CallerWorkflowSubstitutionsRefuseAsync(string mutation)
    {
        using CandidateGateFixture fixture = new() { ChangedCallerBytes = mutation == "bytes" };
        if (mutation == "path")
        {
            fixture.CallerPath = ".github/workflows/other.yml";
        }
        else if (mutation == "ref")
        {
            fixture.IssuanceClaims = claims => claims["workflow_ref"] = "Hexalith/Orders/.github/workflows/ci.yml@refs/heads/foreign";
        }

        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-workflow-mismatch");
    }

    /// <summary>Duplicate JSON request keys are rejected without selecting an interpretation.</summary>
    /// <returns>The duplicate-field refusal.</returns>
    [Fact]
    public async Task DuplicateRequestKeysRefuseAsync()
    {
        using CandidateGateFixture fixture = new();
        string path = fixture.Sign();
        string original = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, "{\"token\":\"foreign\"," + original[1..], TestContext.Current.CancellationToken).ConfigureAwait(true);
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(path, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("duplicate-request-field");
    }

    /// <summary>Release authority is restricted to the authenticated dispatch of main.</summary>
    /// <param name="signedRef">The signed dispatch ref.</param>
    /// <param name="apiBranch">The authenticated dispatch branch.</param>
    /// <returns>The release authority refusal.</returns>
    [Theory]
    [InlineData("refs/heads/feature", "main")]
    [InlineData("refs/tags/v1", "main")]
    [InlineData("refs/heads/main", "feature")]
    public async Task ReleaseDispatchRequiresMainAsync(string signedRef, string apiBranch)
    {
        using CandidateGateFixture fixture = new() { SignedRef = signedRef, DispatchBranch = apiBranch };
        fixture.Payload["kind"] = "release";
        _ = fixture.Payload.Remove("pr");
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("release-authority-mismatch");
    }

    /// <summary>Successor lookup is bounded to relevant creation times with pages below the response cap.</summary>
    /// <returns>The exact API query assertions.</returns>
    [Fact]
    public async Task SuccessorLookupStartsAtGateCreationAsync()
    {
        using CandidateGateFixture fixture = new();
        _ = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        Uri.UnescapeDataString(fixture.SuccessorQuery!).ShouldContain("created=>=2026-10-10T00:00:00Z");
        fixture.SuccessorQuery!.ShouldContain("per_page=30");
    }

    /// <summary>Full successor pages are traversed before selecting or rejecting the exact issued identity.</summary>
    /// <param name="successor">Whether page two contains a superseding request.</param>
    /// <returns>The exact candidate identity or successor refusal after both pages were observed.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessorHistoryTraversesFullPageAndTerminalPageAsync(bool successor)
    {
        using CandidateGateFixture fixture = new() { WorkflowPaginationMode = successor ? "paged-successor" : "paged" };
        string request = fixture.Sign();
        if (successor)
        {
            CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            refusal.Condition.ShouldBe("latest-issued-attempt-required");
            fixture.WorkflowPaginationMode = "paged";
        }

        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        binding.RunId.ShouldBe(fixture.Payload["runId"]!.GetValue<string>());
        binding.AttemptId.ShouldBe(fixture.Payload["attemptId"]!.GetValue<string>());
        binding.Candidate.GetProperty("committedRevision").GetString().ShouldBe(CandidateGateFixture.Merge);
        binding.Decision.GetProperty("requestId").GetString().ShouldBe("github:Hexalith/Orders:101");
        fixture.WorkflowPages.ShouldBe(successor ? _twoPageRechecks : _twoPages);
    }

    /// <summary>Ambiguous signing keys and malformed RSA parameters are authentication refusals.</summary>
    /// <param name="keyMode">The isolated signing-key violation.</param>
    /// <returns>The stable signing-key authentication refusal.</returns>
    [Theory]
    [InlineData("duplicate")]
    [InlineData("missing-modulus")]
    [InlineData("malformed-modulus")]
    [InlineData("malformed-exponent")]
    [InlineData("invalid-rsa")]
    public async Task SigningKeyRejectionRemainsAuthenticationFailureAsync(string keyMode)
    {
        using CandidateGateFixture fixture = new() { KeyMode = keyMode };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-authentication-required");
    }

    /// <summary>Consumer proofs must have a current, ordered lifetime with no future issuance.</summary>
    /// <param name="lifetime">The isolated temporal violation.</param>
    /// <returns>The consumer proof authentication refusal.</returns>
    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("reversed")]
    [InlineData("empty")]
    public async Task ConsumerTemporalClaimsRefuseAsync(string lifetime)
    {
        using CandidateGateFixture fixture = new();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.ConsumerClaims = claims => MutateLifetime(claims, lifetime);
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-authentication-required");
    }

    /// <summary>Archival issuance still requires an ordered lifetime and cannot originate in the future.</summary>
    /// <param name="lifetime">The invalid issuance lifetime.</param>
    /// <returns>The issuance authentication refusal.</returns>
    [Theory]
    [InlineData("future")]
    [InlineData("reversed")]
    public async Task ArchivedIssuanceRejectsInvalidLifetimeAsync(string lifetime)
    {
        using CandidateGateFixture fixture = new() { IssuanceClaims = claims => MutateLifetime(claims, lifetime) };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("issuer-authentication-required");
    }

    /// <summary>Expired issuance authenticates its archived bytes while fresh authority and consumer proof govern reuse.</summary>
    /// <returns>The authenticated archived identity and fresh supersession refusal.</returns>
    [Fact]
    public async Task ExpiredArchivedIssuanceStillRechecksCurrentAuthorityAsync()
    {
        using CandidateGateFixture fixture = new() { IssuanceClaims = claims => MutateLifetime(claims, "expired") };
        string request = fixture.Sign();
        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken).ConfigureAwait(true);
        binding.Candidate.GetProperty("committedRevision").GetString().ShouldBe(CandidateGateFixture.Merge);
        binding.Decision.GetProperty("requestId").GetString().ShouldBe("github:Hexalith/Orders:101");
        await fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.CurrentAttempt = 2;
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.ResolveAsync(request, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("latest-issued-attempt-required");
    }

    /// <summary>Coherent caller-workflow refs cannot bypass the explicit permitted PR or release ref guard.</summary>
    /// <param name="release">Whether the request is a release dispatch.</param>
    /// <param name="foreignRef">The coherent but forbidden ref.</param>
    /// <returns>The consumer ref authentication refusal.</returns>
    [Theory]
    [InlineData(false, "refs/pull/7/head")]
    [InlineData(false, "refs/pull/8/merge")]
    [InlineData(true, "refs/heads/feature")]
    [InlineData(true, "refs/tags/v1")]
    public async Task ConsumerPermittedRefIsCheckedIndependentlyAsync(bool release, string foreignRef)
    {
        using CandidateGateFixture fixture = new();
        if (release)
        {
            fixture.Payload["kind"] = "release";
            _ = fixture.Payload.Remove("pr");
        }

        CandidatePolicyBinding binding = await fixture.Gate.ResolveAsync(fixture.Sign(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        fixture.ConsumerClaims = claims =>
        {
            claims["ref"] = foreignRef;
            claims["workflow_ref"] = "Hexalith/Orders/.github/workflows/ci.yml@" + foreignRef;
        };
        CandidatePolicyException refusal = await Should.ThrowAsync<CandidatePolicyException>(() => fixture.Gate.VerifyExecutionAsync(binding, fixture.Context, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Condition.ShouldBe("consumer-authentication-required");
    }

    private static void MutateLifetime(JsonObject claims, string lifetime)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        claims["iat"] = lifetime == "future" ? now + 120 : now - 20;
        claims["exp"] = lifetime switch
        {
            "expired" => now - 1,
            "reversed" => now - 21,
            "empty" => now - 20,
            _ => now + 300,
        };
    }
}