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
}