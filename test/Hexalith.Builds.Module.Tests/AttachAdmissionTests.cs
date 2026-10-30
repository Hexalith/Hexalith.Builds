// <copyright file="AttachAdmissionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;

using Shouldly;

using Xunit;

/// <summary>Verifies explicit attach admission, recorded holds, retries, and attachment-scoped endings.</summary>
public sealed class AttachAdmissionTests
{
    private static readonly JsonElement _local = JsonSerializer.SerializeToElement(new { kind = "local" });

    /// <summary>An omitted hold is accepted for 1800 seconds, recorded with its absolute expiry, and listed.</summary>
    /// <returns>The recorded hold and listing assertions.</returns>
    [Fact]
    public async Task OmittedHoldIsAcceptedForDefaultSecondsAndListedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), now, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, string.Join(';', result.Diagnostics.Select(item => item.Field + item.Message)));
        CompositionAttachment attachment = result.Attachment.ShouldNotBeNull();
        attachment.RequestedHoldSeconds.ShouldBeNull();
        attachment.EffectiveHoldSeconds.ShouldBe(1800);
        attachment.AcceptedAtUtc.ShouldBe(now);
        attachment.ExpiresAtUtc.ShouldBe(now.AddSeconds(1800));
        attachment.OwnerRunId.ShouldBe(owner.RunId);
        attachment.EnvironmentId.ShouldBe(owner.EnvironmentId);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, now.AddSeconds(5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunListingEntry entry = listed.Environments.ShouldNotBeNull().Single();
        entry.AttachedRunIds.ShouldBe([attachment.AttachedRunId]);
        entry.HoldDeadlineUtc.ShouldBe(attachment.ExpiresAtUtc);
        entry.AttachmentInformation.ShouldBe("attached");
        entry.Attachments.Single().OriginalAttachmentRequestId.ShouldBe(attachment.OriginalAttachmentRequestId);
    }

    /// <summary>Explicit whole seconds across the accepted range are recorded exactly.</summary>
    /// <param name="seconds">The explicit request.</param>
    /// <returns>The exact effective hold assertions.</returns>
    [Theory]
    [InlineData("1")]
    [InlineData("300")]
    [InlineData("14399")]
    [InlineData("14400")]
    public async Task ExplicitHoldIsAcceptedExactlyAsync(string seconds)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = seconds }, now, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionAttachment attachment = result.Attachment.ShouldNotBeNull();
        attachment.RequestedHoldSeconds.ShouldBe(int.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture));
        attachment.EffectiveHoldSeconds.ShouldBe(attachment.RequestedHoldSeconds!.Value);
        attachment.ExpiresAtUtc.ShouldBe(now.AddSeconds(attachment.EffectiveHoldSeconds));
    }

    /// <summary>Invalid holds are refused as usage errors and nothing is recorded, clamped or defaulted.</summary>
    /// <param name="seconds">The invalid request.</param>
    /// <returns>The refusal assertions.</returns>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("14401")]
    [InlineData("1.5")]
    [InlineData("")]
    [InlineData(" 5")]
    [InlineData("1e3")]
    [InlineData("99999999999999999999")]
    [InlineData("abc")]
    public async Task InvalidHoldIsRefusedWithoutRecordingAsync(string seconds)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = seconds }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.UsageOrManifest);
        result.Diagnostics.Single().RuleId.ShouldBe("HXR032");
        result.Attachment.ShouldBeNull();
        File.Exists(CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId))).ShouldBeFalse();
    }

    /// <summary>Ineligible targets and incompatible environments are refused with stable reasons.</summary>
    /// <param name="scenario">The mismatch scenario.</param>
    /// <param name="rule">The expected rule.</param>
    /// <param name="reason">The expected stable reason.</param>
    /// <returns>The refusal assertions.</returns>
    [Theory]
    [InlineData("hosted", "HXR033", "hosted-target")]
    [InlineData("developer-started", "HXR033", "developer-started-target")]
    [InlineData("unknown-owner", "HXR033", "owner-unknown")]
    [InlineData("cross-context", "HXR033", "cross-context")]
    [InlineData("cross-job", "HXR033", "cross-job")]
    [InlineData("mode", "HXR034", "mode-mismatch")]
    [InlineData("composition", "HXR034", "composition-mismatch")]
    [InlineData("artifact", "HXR034", "artifact-identity-unverifiable")]
    [InlineData("environment", "HXR034", "environment-mismatch")]
    [InlineData("not-ready", "HXR034", "not-ready")]
    [InlineData("starting", "HXR035", "owner-starting")]
    public async Task IncompatibleOrIneligibleTargetIsRefusedAsync(string scenario, string rule, string reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(
            workspace,
            state => scenario switch
            {
                "not-ready" => state with { ReadinessResults = [new CompositionResourceReadiness("owned", "process", "Unhealthy")] },
                "starting" => state with { Status = CompositionRunStatus.Starting },
                "cross-job" => state with { ExecutionContext = Ci("job-a") },
                _ => state,
            }).ConfigureAwait(true);
        CompositionAttachRequest request = Request(owner, composition);
        request = scenario switch
        {
            "hosted" or "developer-started" => request with { Target = scenario },
            "unknown-owner" => request with { OwnerRunId = CompositionRunPlanFactory.NewRunId() },
            "cross-context" => request with { AttacherContext = Ci("job-a") },
            "cross-job" => request with { AttacherContext = Ci("job-b") },
            "mode" => request with { Mode = "package" },
            "composition" => request with { CompositionIdentity = new string('a', 64) },
            "artifact" => request with { ArtifactIdentities = ["sha256:" + new string('b', 64)] },
            "environment" => request with { EnvironmentId = CompositionRunPlanFactory.NewRunId() },
            _ => request,
        };
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, request, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        result.Diagnostics.Single().RuleId.ShouldBe(rule);
        result.Diagnostics.Single().Field.ShouldBe(reason);
        File.Exists(CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId))).ShouldBeFalse();
    }

    /// <summary>A CI owner is refused even for a same-job attacher, because CI owners do not wait for holds.</summary>
    /// <returns>The refusal assertion.</returns>
    [Fact]
    public async Task SameJobCiAttachmentIsRefusedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => state with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { AttacherContext = Ci("job-a") }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Attachment.ShouldBeNull();
        result.Diagnostics.Single().RuleId.ShouldBe("HXR035");
        result.Diagnostics.Single().Field.ShouldBe("owner-ci-holds-unsupported");
        File.Exists(CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId))).ShouldBeFalse();
    }

    /// <summary>A finished owner refuses new attaches and names its state, unless it is a ready failure-retained local environment.</summary>
    /// <param name="owner">The owner state scenario.</param>
    /// <param name="reason">The expected refusal reason, or null when accepted.</param>
    /// <returns>The owner-state assertions.</returns>
    [Theory]
    [InlineData("passed", "owner-passed")]
    [InlineData("failed-not-retained", "owner-failed")]
    [InlineData("cancelled", "owner-cancelled")]
    [InlineData("cleaned", "owner-cleaned")]
    [InlineData("cleaning", "owner-cleaning")]
    [InlineData("failed-retained", null)]
    [InlineData("failed-then-cancelled-retained", null)]
    [InlineData("retained-stopped", "owner-cleaning")]
    public async Task OwnerStateDecidesAdmissionAsync(string owner, string? reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState state, string composition) = await ReadyOwnerAsync(workspace, current => Finished(current, owner)).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(state, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (reason is null)
        {
            _ = result.Attachment.ShouldNotBeNull();
        }
        else
        {
            result.Diagnostics.Single().RuleId.ShouldBe("HXR035");
            result.Diagnostics.Single().Field.ShouldBe(reason);
        }
    }

    /// <summary>Attached failure retention permits admission only before cleanup has been requested.</summary>
    /// <param name="cleanup">The owner's current cleanup disposition.</param>
    /// <param name="reason">The expected refusal reason, or null for retained admission.</param>
    /// <returns>The admission precedence and unchanged attachment assertions.</returns>
    [Theory]
    [InlineData("not-requested", null)]
    [InlineData("pending", "owner-cleaning")]
    [InlineData("in-progress", "owner-cleaning")]
    [InlineData("incomplete", "owner-cleaning")]
    [InlineData("complete", "owner-cleaned")]
    public async Task AttachedFailureCannotOverrideCleanupRefusalAsync(string cleanup, string? reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionAttachment failed = (await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionRunState finished = Finished(owner, "passed");
        await store.WriteAsync(finished with { CleanupDisposition = cleanup, TestRun = finished.TestRun! with { CleanupDisposition = cleanup } }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (reason is null)
        {
            _ = result.Attachment.ShouldNotBeNull();
        }
        else
        {
            result.Attachment.ShouldBeNull();
            result.Diagnostics.Single().RuleId.ShouldBe("HXR035");
            result.Diagnostics.Single().Field.ShouldBe(reason);
        }

        IReadOnlyList<CompositionAttachment> recorded = await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        recorded.Single(item => item.AttachedRunId == failed.AttachedRunId).ShouldBe(failed);
        recorded.Count.ShouldBe(reason is null ? 2 : 1);
    }

    /// <summary>A retry with an issued identity keeps the first acceptance and expiry; unknown and expired lineages are refused.</summary>
    /// <returns>The immutable lineage assertions.</returns>
    [Fact]
    public async Task RetryKeepsFirstAcceptanceAndExpiryAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment first = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "60" }, now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        foreach (string presented in new[] { first.OriginalAttachmentRequestId, first.AttachedRunId })
        {
            ToolCommandResult retry = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { PresentedRequestId = presented, HoldSeconds = "14400" }, now.AddSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            retry.Attachment.ShouldBe(first);
            retry.Status.ShouldBe("attach-retried");
        }

        ToolCommandResult unknown = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { PresentedRequestId = CompositionRunPlanFactory.NewRunId() }, now, TestContext.Current.CancellationToken).ConfigureAwait(true);
        unknown.Diagnostics.Single().RuleId.ShouldBe("HXR036");
        ToolCommandResult expired = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { PresentedRequestId = first.OriginalAttachmentRequestId }, first.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(true);
        expired.Diagnostics.Single().Field.ShouldBe("attachment-expired");
        _ = await CompositionAttachmentStore.ObserveAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, first.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(true);
        foreach (string presented in new[] { first.OriginalAttachmentRequestId, first.AttachedRunId })
        {
            foreach (DateTimeOffset observedAt in new[] { first.ExpiresAtUtc, first.ExpiresAtUtc.AddSeconds(-1) })
            {
                ToolCommandResult recordedExpiry = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { PresentedRequestId = presented }, observedAt, TestContext.Current.CancellationToken).ConfigureAwait(true);
                recordedExpiry.Diagnostics.Single().Field.ShouldBe("attachment-expired");
            }
        }

        // A request without the lineage is a new original request with its own hold.
        ToolCommandResult fresh = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), first.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(true);
        fresh.Attachment.ShouldNotBeNull().OriginalAttachmentRequestId.ShouldNotBe(first.OriginalAttachmentRequestId);
    }

    /// <summary>Ending one attachment leaves the owner and the other attachment untouched.</summary>
    /// <returns>The scoped end assertions.</returns>
    [Fact]
    public async Task EndingOneAttachmentLeavesOwnerAndPeersRunningAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment one = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionAttachment two = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        byte[] before = await File.ReadAllBytesAsync(store.PathFor(owner.RunId), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult ended = await CompositionAttachAdmission.EndAsync(store, owner.RunId, one.AttachedRunId, "passed", now.AddSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Attachment.ShouldNotBeNull().EndReason.ShouldBe("terminal-outcome");
        (await File.ReadAllBytesAsync(store.PathFor(owner.RunId), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(before);
        ToolCommandResult again = await CompositionAttachAdmission.EndAsync(store, owner.RunId, one.AttachedRunId, "cancelled", now.AddSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        again.Attachment.ShouldNotBeNull().EndedAtUtc.ShouldBe(now.AddSeconds(10));
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, now.AddSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunListingEntry entry = listed.Environments.ShouldNotBeNull().Single();
        entry.AttachedRunIds.ShouldBe([two.AttachedRunId]);
        entry.Cleanup.ShouldBe("not-requested");
        (await CompositionAttachAdmission.EndAsync(store, owner.RunId, CompositionRunPlanFactory.NewRunId(), "passed", now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Diagnostics.Single().RuleId.ShouldBe("HXR036");

        // The owner keeps accepting attachments after one of them ended.
        _ = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), now.AddSeconds(31), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
    }

    /// <summary>
    /// The human output of attach and list shows the issued identities, expiry and hold deadline, and human ends show the
    /// recorded first outcome, including withdrawal of a late report.
    /// </summary>
    /// <returns>The human-format assertions.</returns>
    [Fact]
    public async Task HumanOutputShowsAttachedRunAndHoldDeadlineAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!);
        (int admittedExit, string admitted) = await RunAsync(["attach", "--owner-run-id", owner.RunId, "--mode", "source", "--composition-identity", composition, "--hold-seconds", "600", "--output", "human"], options).ConfigureAwait(true);
        admittedExit.ShouldBe(0, admitted);
        admitted.ShouldContain("attached-run=");
        admitted.ShouldContain("hold-seconds=600");
        admitted.ShouldContain("expires=");
        admitted.ShouldContain("outcome=active");
        (int listedExit, string listed) = await RunAsync(["list", "--output", "human"], options).ConfigureAwait(true);
        listedExit.ShouldBe(0, listed);
        listed.ShouldContain("  attached=");
        listed.ShouldContain("state=recorded");
        listed.ShouldNotContain("hold-deadline=unavailable");

        string attached = admitted.Split("attached-run=")[1][..32];
        (int endedExit, string ended) = await RunAsync(["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", attached, "--outcome", "failed", "--output", "human"], options).ConfigureAwait(true);
        endedExit.ShouldBe(0, ended);
        ended.ShouldContain("attached-run=" + attached);
        ended.ShouldContain("outcome=failed");
        ended.ShouldContain("/terminal-outcome");

        DateTimeOffset lateAccepted = DateTimeOffset.UtcNow.AddSeconds(-120);
        CompositionAttachment late = new(CompositionRunPlanFactory.NewRunId(), owner.EnvironmentId!, owner.RunId, CompositionRunPlanFactory.NewRunId(), 60, lateAccepted, 60, lateAccepted.AddSeconds(60));
        _ = await CompositionAttachmentStore.UpdateAsync<int>(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, existing => (0, [.. existing, late]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (int lateExit, string lateText) = await RunAsync(["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", late.AttachedRunId, "--outcome", "passed", "--output", "human"], options).ConfigureAwait(true);
        lateExit.ShouldBe(0, lateText);
        lateText.ShouldContain("attached-run=" + late.AttachedRunId);
        lateText.ShouldContain("outcome=withdrawn");
        lateText.ShouldNotContain("outcome=passed");
        lateText.ShouldContain("ended=" + late.ExpiresAtUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + "/expired");
    }

    /// <summary>The CLI admits, retries and ends attachments with the public options and exit codes.</summary>
    /// <returns>The command-line assertions.</returns>
    [Fact]
    public async Task AttachCommandAdmitsRetriesAndEndsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!);
        string[] common = ["attach", "--owner-run-id", owner.RunId, "--mode", "source", "--composition-identity", composition, "--output", "json"];
        (int admittedExit, string admittedText) = await RunAsync([.. common, "--hold-seconds", "600"], options).ConfigureAwait(true);
        admittedExit.ShouldBe(0, admittedText);
        using JsonDocument document = JsonDocument.Parse(admittedText);
        JsonElement attachment = document.RootElement.GetProperty("attachment");
        attachment.GetProperty("effectiveHoldSeconds").GetInt32().ShouldBe(600);
        string request = attachment.GetProperty("originalAttachmentRequestId").GetString()!;
        string attached = attachment.GetProperty("attachedRunId").GetString()!;
        (int retriedExit, string retriedText) = await RunAsync([.. common, "--attachment-request-id", request], options).ConfigureAwait(true);
        retriedExit.ShouldBe(0, retriedText);
        retriedText.ShouldContain(attachment.GetProperty("expiresAtUtc").GetString()!.Split('.')[0]);
        foreach (string bad in new[] { "0", "14401", "1.5", string.Empty })
        {
            (int refusedExit, string refusedText) = await RunAsync([.. common, "--hold-seconds", bad], options).ConfigureAwait(true);
            refusedExit.ShouldBe((int)ToolExitCode.UsageOrManifest, refusedText);
            refusedText.ShouldContain("HXR032");
        }

        (int hostedExit, string hostedText) = await RunAsync([.. common, "--target", "hosted"], options).ConfigureAwait(true);
        hostedExit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle);
        hostedText.ShouldContain("HXR033");
        (int endedExit, string endedText) = await RunAsync(["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", attached, "--outcome", "passed", "--output", "json"], options).ConfigureAwait(true);
        endedExit.ShouldBe(0, endedText);
        endedText.ShouldContain("terminal-outcome");
        (int afterEndExit, string afterEndText) = await RunAsync([.. common, "--attachment-request-id", request], options).ConfigureAwait(true);
        afterEndExit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle);
        afterEndText.ShouldContain("HXR036");
        _ = (await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().EndedAtUtc.ShouldNotBeNull();
    }

    /// <summary>Owner success-cleanup waits for a live hold to end, and never runs before it.</summary>
    /// <returns>The deferred cleanup assertions.</returns>
    [Fact]
    public async Task WaitReturnsOnlyAfterEveryHoldEndsOrExpiresAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionAttachment ended = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionAttachment expiring = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "8" }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionAttachment earlier = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "6" }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        Task waiting = CompositionAttachmentStore.WaitForHoldsOrFailureAsync(directory, owner.RunId, TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        waiting.IsCompleted.ShouldBeFalse();
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, ended.AttachedRunId, "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        waiting.IsCompleted.ShouldBeFalse();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        DateTimeOffset.UtcNow.ShouldBeGreaterThanOrEqualTo(expiring.ExpiresAtUtc);
        CompositionAttachment recorded = (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single(item => item.AttachedRunId == expiring.AttachedRunId);
        recorded.Outcome.ShouldBe("withdrawn");
        recorded.EndedAtUtc.ShouldBe(expiring.ExpiresAtUtc);
        IReadOnlyList<CompositionAttachment> all = await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        JsonElement report = AssertWithdrawalReport(directory, all);
        report.GetProperty("attachments").GetArrayLength().ShouldBe(2);
        report.GetProperty("attachments").EnumerateArray().ShouldContain(item => item.GetProperty("attachedRunId").GetString() == earlier.AttachedRunId);
    }

    /// <summary>A passed local owner defers cleanup while a hold is live, lists it pending, and cleans with a fresh budget after the last end.</summary>
    /// <returns>The owner lifecycle assertions, the descriptor holds and the published holds.</returns>
    [Fact]
    public async Task PassedOwnerDefersCleanupUntilHoldEndsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        string resultPath = await run.WaitForResultAsync().ConfigureAwait(true);

        // The stop allowance of the original stopping budget (4 seconds) has lapsed before the hold ends.
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Running.IsCompleted.ShouldBeFalse();
        run.Cleanups.ShouldBe(0);
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionRunListingEntry pending = (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        pending.Cleanup.ShouldBe("pending");
        pending.HoldDeadlineUtc.ShouldBe(run.Seeded.ExpiresAtUtc);
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        _ = await CompositionAttachAdmission.EndAsync(store, run.Session!.RunId, run.Seeded.AttachedRunId, "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Cleanups.ShouldBe(1);
        run.CleanupTokenWasCancelled.ShouldBeFalse();
        TestEnvironmentContractReadResult published = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        published.IsValid.ShouldBeTrue(string.Join(';', published.Diagnostics.Select(item => item.Message)));
        JsonElement hold = published.Document!.Value.GetProperty("bindings").GetProperty("attachments").GetProperty("holds").EnumerateArray().Single();
        hold.GetProperty("attachedRunId").GetString().ShouldBe(run.Seeded.AttachedRunId);
        hold.GetProperty("end").GetProperty("reason").GetString().ShouldBe("terminal-outcome");

        // The descriptor written for the child carried the seeded hold as active.
        using JsonDocument descriptor = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "descriptor-copy.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement descriptorHold = descriptor.RootElement.GetProperty("bindings").GetProperty("attachments").GetProperty("holds").EnumerateArray().Single();
        descriptorHold.GetProperty("attachedRunId").GetString().ShouldBe(run.Seeded.AttachedRunId);
        descriptorHold.GetProperty("end").GetProperty("state").GetString().ShouldBe("active");
    }

    /// <summary>A failed local owner finishes without waiting for a live hold.</summary>
    /// <returns>The no-wait assertions.</returns>
    [Fact]
    public async Task FailedOwnerDoesNotWaitForLiveHoldAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None, failing: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        int exit = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldNotBe((int)ToolExitCode.Success, run.Output.ToString());
        (await CompositionAttachmentStore.ReadAsync(Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId), run.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().IsLive(DateTimeOffset.UtcNow).ShouldBeTrue();
    }

    /// <summary>Cancelling a success hold wait withdraws attachments before cleanup, preserving the first passed outcome.</summary>
    /// <returns>The withdrawal, ordering and first-outcome assertions.</returns>
    [Fact]
    public async Task CancelledWaitWithdrawsBeforeCleanupAndPreservesFirstOutcomeAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        string resultPath = await run.WaitForResultAsync().ConfigureAwait(true);
        JsonElement before = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value.GetProperty("terminal").Clone();

        // Even after the original stop budget has elapsed, cancellation gets its finite cleanup allowance.
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(1);
        run.CleanupTokenWasCancelled.ShouldBeFalse();
        run.Output.ToString().ShouldContain("HXI011");
        run.Output.ToString().ShouldContain(run.Seeded.AttachedRunId);
        run.Output.ToString().ShouldContain("owner-cancelled");
        run.WithdrawalsAtCleanup.Single().Outcome.ShouldBe("withdrawn");
        run.WithdrawalsAtCleanup.Single().EndReason.ShouldBe("owner-cancelled");
        run.OwnerStatusAtCleanup.ShouldBe(CompositionRunStatus.Cancelled);
        Directory.Exists(run.Session!.Plan.Workspace).ShouldBeFalse();
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value.GetProperty("terminal").GetProperty("recordedAt").GetString().ShouldBe(before.GetProperty("recordedAt").GetString());
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).Document!.Value.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe("passed");
    }

    /// <summary>Corrupt attachment metadata during the wait reports HXT008 and never cleans up.</summary>
    /// <returns>The deferred-cleanup and diagnostic assertions.</returns>
    [Fact]
    public async Task CorruptAttachmentMetadataDefersCleanupWithEvidenceDiagnosticAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(CompositionAttachmentStore.PathFor(Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId)), "{not-json", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        run.Output.ToString().ShouldContain("HXT008");
        ModuleTestRunRecord committed = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(run.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.CleanupDisposition.ShouldBe("pending");
        committed.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    /// <summary>Retries still run the context and owner-state checks, so a cleaned owner or another CI job is refused.</summary>
    /// <param name="scenario">The retry scenario.</param>
    /// <param name="reason">The expected stable reason.</param>
    /// <returns>The refusal assertions.</returns>
    [Theory]
    [InlineData("cleaned", "owner-cleaned")]
    [InlineData("passed", "owner-passed")]
    [InlineData("cross-job", "cross-job")]
    public async Task RetryIsRefusedWhenOwnerFinishedOrJobDiffersAsync(string scenario, string reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => scenario == "cross-job" ? state with { ExecutionContext = Ci("job-a") } : state).ConfigureAwait(true);
        JsonElement context = _local;
        CompositionAttachment first = scenario == "cross-job"
            ? await SeedHoldAsync(store, owner).ConfigureAwait(true)
            : (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { AttacherContext = context }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        if (scenario != "cross-job")
        {
            await store.WriteAsync(Finished(owner, scenario), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CompositionAttachRequest retry = Request(owner, composition) with { PresentedRequestId = first.OriginalAttachmentRequestId, AttacherContext = scenario == "cross-job" ? Ci("job-b") : _local };
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, retry, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Attachment.ShouldBeNull();
        result.Diagnostics.Single().Field.ShouldBe(reason);
    }

    /// <summary>A CI owner is never admitted through the local failure-retained exception.</summary>
    /// <returns>The refusal assertion.</returns>
    [Fact]
    public async Task CiFailureRetainedOwnerIsRefusedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => Finished(state, "failed-retained") with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { AttacherContext = Ci("job-a") }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Diagnostics.Single().Field.ShouldBe("owner-ci-holds-unsupported");
    }

    /// <summary>Any differing CI identity element refuses the attachment as cross-job.</summary>
    /// <param name="element">The differing element.</param>
    /// <returns>The refusal assertion.</returns>
    [Theory]
    [InlineData("attempt")]
    [InlineData("run")]
    [InlineData("repository")]
    public async Task DifferingCiIdentityIsCrossJobAsync(string element)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => state with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        JsonElement attacher = element switch
        {
            "attempt" => Ci("job-a", attempt: 2),
            "run" => Ci("job-a", run: "2"),
            _ => Ci("job-a", repository: "o/other"),
        };
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { AttacherContext = attacher }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Diagnostics.Single().Field.ShouldBe("cross-job");
    }

    /// <summary>A passively expired hold ends as expired at its expiry and lists as ended or expired.</summary>
    /// <returns>The end-time assertions.</returns>
    [Fact]
    public async Task ExpiredHoldEndsAtItsExpiryAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment hold = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "60" }, now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionAttachment ended = (await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "passed", now.AddSeconds(120), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        ended.EndReason.ShouldBe("expired");
        ended.Outcome.ShouldBe("withdrawn");
        ended.EndedAtUtc.ShouldBe(hold.ExpiresAtUtc);
        CompositionRunListingEntry entry = (await CompositionRunListing.ReadAsync(store, now.AddSeconds(130), TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        entry.AttachmentInformation.ShouldBe("ended-or-expired");
        entry.AttachedRunIds.ShouldBeEmpty();
    }

    /// <summary>A CI owner never lists hold-deferred cleanup, because CI owners do not wait for holds.</summary>
    /// <returns>The listing assertion.</returns>
    [Fact]
    public async Task CiOwnerListingDoesNotClaimHoldDeferredCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace, state => state with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _ = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        await store.WriteAsync(Finished(owner, "passed") with { CleanupDisposition = "not-requested" }, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunListingEntry entry = (await CompositionRunListing.ReadAsync(store, now.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        entry.Cleanup.ShouldBe("not-requested");
    }

    /// <summary>Parallel admissions each receive a distinct consistent record.</summary>
    /// <returns>The persisted records assertion.</returns>
    [Fact]
    public async Task ParallelAdmissionsRecordDistinctConsistentHoldsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        ToolCommandResult[] results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken))).ConfigureAwait(true);
        results.ShouldAllBe(result => result.Attachment != null);
        IReadOnlyList<CompositionAttachment> recorded = await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        recorded.Count.ShouldBe(8);
        recorded.Select(item => item.AttachedRunId).Distinct().Count().ShouldBe(8);
        recorded.Select(item => item.OriginalAttachmentRequestId).Distinct().Count().ShouldBe(8);
    }

    /// <summary>Corrupt attachment metadata is unverifiable for admission and end, and the listing is incomplete.</summary>
    /// <returns>The refusal assertions.</returns>
    [Fact]
    public async Task CorruptAttachmentMetadataIsUnverifiableAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        await File.WriteAllTextAsync(CompositionAttachmentStore.PathFor(directory), "{not-json", TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult admitted = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        admitted.Diagnostics.Single().Field.ShouldBe("owner-unverifiable");
        ToolCommandResult ended = await CompositionAttachAdmission.EndAsync(store, owner.RunId, CompositionRunPlanFactory.NewRunId(), "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Attachment.ShouldBeNull();
        ended.Outcome.ExitCode.ShouldNotBe(ToolExitCode.Success);
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        listed.Diagnostics.Single().RuleId.ShouldBe("HXR024");
    }

    /// <summary>Option combinations that mix admission and end, or name an attached run without ending, are usage errors.</summary>
    /// <param name="kind">Whether the minimal command ends an attachment or admits one.</param>
    /// <param name="option">The conflicting option.</param>
    /// <param name="value">The conflicting option value.</param>
    /// <returns>The usage exit assertions.</returns>
    [Theory]
    [InlineData("end", "--mode", "source")]
    [InlineData("end", "--composition-identity", "abc")]
    [InlineData("end", "--target", "hosted")]
    [InlineData("end", "--environment-id", "00000000000000000000000000000000")]
    [InlineData("end", "--artifact-identity", "sha256:abc")]
    [InlineData("end", "--hold-seconds", "5")]
    [InlineData("admit", "--outcome", "passed")]
    [InlineData("admit", "--attached-run-id", "00000000000000000000000000000000")]
    public async Task CliRejectsMixedOptionCombinationsAsync(string kind, string option, string value)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!);
        string[] arguments = kind == "end"
            ? ["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", owner.RunId, "--outcome", "passed", option, value]
            : ["attach", "--owner-run-id", owner.RunId, "--mode", "source", "--composition-identity", composition, option, value];
        (int exit, string text) = await RunAsync(arguments, options).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest, text);
        text.ShouldContain("HXC001");
    }

    /// <summary>Every reported terminal outcome is durable and the first record survives later outcomes and expiry.</summary>
    /// <param name="outcome">The first reported outcome.</param>
    /// <returns>The immutable persisted outcome assertions.</returns>
    [Theory]
    [InlineData("passed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task AttachedFirstOutcomeIsImmutableAsync(string outcome)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionAttachment first = (await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, outcome, hold.AcceptedAtUtc.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        first.Outcome.ShouldBe(outcome);
        first.EndReason.ShouldBe("terminal-outcome");
        CompositionAttachment later = (await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "passed", hold.ExpiresAtUtc.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        later.ShouldBe(first);
        IReadOnlyList<CompositionAttachment> withdrawn = await CompositionAttachmentStore.WithdrawAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, "developer-stop", hold.ExpiresAtUtc.AddSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        withdrawn.ShouldBeEmpty();
        (await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().ShouldBe(first);
    }

    /// <summary>Missing and unsupported attached outcomes are HXR032 usage refusals.</summary>
    /// <param name="outcome">The missing or invalid outcome.</param>
    /// <returns>The CLI diagnostic and no-write assertions.</returns>
    [Theory]
    [InlineData(null)]
    [InlineData("withdrawn")]
    [InlineData("unknown")]
    [InlineData("")]
    public async Task EndRequiresSupportedOutcomeAsync(string? outcome)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        string[] args = ["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", hold.AttachedRunId, "--output", "json"];
        (int exit, string text) = await RunAsync(outcome is null ? args : [.. args, "--outcome", outcome], LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!)).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.UsageOrManifest);
        text.ShouldContain("HXR032");
        (await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().Outcome.ShouldBeNull();
    }

    /// <summary>Withdrawal records earlier expiries and live withdrawals together, leaving terminal outcomes untouched.</summary>
    /// <param name="reason">The environment withdrawal cause.</param>
    /// <returns>The recorded end times, reasons and immutable expiries.</returns>
    [Theory]
    [InlineData("owner-cancelled")]
    [InlineData("developer-stop")]
    public async Task WithdrawalRecordsExpiryAndLiveHoldsInOnePassAsync(string reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionAttachment expired = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "1" }, now.AddSeconds(-2), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        CompositionAttachment live = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        IReadOnlyList<CompositionAttachment> withdrawn = await CompositionAttachmentStore.WithdrawAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, reason, now.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        withdrawn.Count.ShouldBe(2);
        withdrawn.ShouldAllBe(item => item.Outcome == "withdrawn" && item.IsConsistent());
        withdrawn.Single(item => item.AttachedRunId == expired.AttachedRunId).EndReason.ShouldBe("expired");
        withdrawn.Single(item => item.AttachedRunId == expired.AttachedRunId).EndedAtUtc.ShouldBe(expired.ExpiresAtUtc);
        CompositionAttachment actual = withdrawn.Single(item => item.AttachedRunId == live.AttachedRunId);
        actual.EndReason.ShouldBe(reason);
        actual.EndedAtUtc.ShouldBe(now.AddSeconds(1));
        actual.ExpiresAtUtc.ShouldBe(live.ExpiresAtUtc);
    }

    /// <summary>A withdrawal clock read before racing acceptance never records an end before acceptance.</summary>
    /// <param name="reason">The withdrawal reason.</param>
    /// <returns>The clamped end time and successfully verified durable record.</returns>
    [Theory]
    [InlineData("owner-cancelled")]
    [InlineData("developer-stop")]
    public async Task WithdrawalBeforeAcceptanceClampsEndTimeAsync(string reason)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionAttachment withdrawn = (await CompositionAttachmentStore.WithdrawAsync(directory, owner.RunId, reason, hold.AcceptedAtUtc.AddSeconds(-1), TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        withdrawn.EndedAtUtc.ShouldBe(hold.AcceptedAtUtc);
        withdrawn.ExpiresAtUtc.ShouldBe(hold.ExpiresAtUtc);
        withdrawn.Outcome.ShouldBe("withdrawn");
        withdrawn.EndReason.ShouldBe(reason);
        withdrawn.IsConsistent().ShouldBeTrue();
        (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().ShouldBe(withdrawn);
    }

    /// <summary>An absent owner cannot end an active or unknown attachment, and existing metadata remains untouched.</summary>
    /// <param name="known">Whether the target is an active issued attachment.</param>
    /// <returns>The original refusal and read-only assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOwnerRefusesUnendedOrUnknownAttachmentWithoutWritingAsync(bool known)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        string path = CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId));
        byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(store.PathFor(owner.RunId));
        ToolCommandResult end = await CompositionAttachAdmission.EndAsync(store, owner.RunId, known ? hold.AttachedRunId : CompositionRunPlanFactory.NewRunId(), "passed", hold.ExpiresAtUtc.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        end.Diagnostics.Single().RuleId.ShouldBe("HXR033");
        end.Diagnostics.Single().Field.ShouldBe("owner-unknown");
        end.Attachment.ShouldBeNull();
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(before);
        File.Exists(store.PathFor(owner.RunId)).ShouldBeFalse();
    }

    /// <summary>An archived end cannot bypass an unverifiable owner-state directory.</summary>
    /// <returns>The stable refusal and unchanged archived attachment assertions.</returns>
    [Fact]
    public async Task ArchivedEndRefusesDirectoryAtOwnerStatePathAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "passed", hold.AcceptedAtUtc.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId));
        byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        File.Delete(store.PathFor(owner.RunId));
        _ = Directory.CreateDirectory(store.PathFor(owner.RunId));
        ToolCommandResult end = await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "failed", hold.AcceptedAtUtc.AddSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        end.Diagnostics.Single().RuleId.ShouldBe("HXR033");
        end.Diagnostics.Single().Field.ShouldBe("attachment-unrecordable");
        end.Attachment.ShouldBeNull();
        (await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(before);
        Directory.Exists(store.PathFor(owner.RunId)).ShouldBeTrue();
    }

    /// <summary>An attached failure before or during the success hold wait retains the environment and permits fresh attachment.</summary>
    /// <param name="beforeOwner">Whether the attached failure precedes the owner's native completion.</param>
    /// <returns>The owner, retention, listing and fresh-admission assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedFailureRetainsPassedOwnerWithoutWaitingForPeerAsync(bool beforeOwner)
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None, blockNative: beforeOwner).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        if (beforeOwner)
        {
            _ = await run.WaitForNativeAsync(workspace.Root).ConfigureAwait(true);
        }
        else
        {
            _ = await run.WaitForResultAsync().ConfigureAwait(true);
        }

        CompositionRunSession session = run.Session.ShouldNotBeNull();
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionAttachment peer = await SeedHoldAsync(store, (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull()).ConfigureAwait(true);
        _ = await CompositionAttachAdmission.EndAsync(store, session.RunId, run.Seeded.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        if (beforeOwner)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "native-release"), "release", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Cleanups.ShouldBe(0);
        using JsonDocument output = JsonDocument.Parse(run.Output.ToString());
        JsonElement retentionDiagnostic = output.RootElement.GetProperty("diagnostics").EnumerateArray().Single(item => item.GetProperty("ruleId").GetString() == "HXI012");
        retentionDiagnostic.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain(run.Seeded.AttachedRunId);
        retentionDiagnostic.GetProperty("message").GetString().ShouldNotBeNull().ShouldContain("run down to release");
        retentionDiagnostic.GetProperty("phase").GetString().ShouldBe(nameof(ToolPhase.Cleanup));
        retentionDiagnostic.GetProperty("category").GetString().ShouldBe(nameof(ToolFailureCategory.None));
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
        CompositionRunState retained = (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        retained.TestRun.ShouldNotBeNull().ResourceDisposition.ShouldBe("retained");
        retained.TestRun.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        CompositionRunListingEntry listing = (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        listing.Retention.ShouldBe("failure-retained");
        listing.FailureRunIds.ShouldBe([run.Seeded.AttachedRunId]);
        listing.AttachedRunIds.ShouldContain(peer.AttachedRunId);
        listing.Cleanup.ShouldBe("not-requested");
        _ = (await CompositionAttachAdmission.AdmitAsync(store, Request(retained, retained.SelectedTest!.ManifestHash), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        string directory = Path.Combine(store.Directory, "diagnostics", retained.RunId);
        CompositionAttachment firstFailure = (await CompositionAttachmentStore.ReadAsync(directory, retained.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single(item => item.AttachedRunId == run.Seeded.AttachedRunId);
        CompositionDownResult down = await new CompositionEngine(run.Options with { SelectedTest = retained.SelectedTest }).DownAsync(retained.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        IReadOnlyList<CompositionAttachment> ended = await CompositionAttachmentStore.ReadAsync(directory, retained.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Single(item => item.AttachedRunId == firstFailure.AttachedRunId).ShouldBe(firstFailure);
        CompositionAttachment withdrawnPeer = ended.Single(item => item.AttachedRunId == peer.AttachedRunId);
        withdrawnPeer.EndReason.ShouldBe("developer-stop");
        byte[] archivedAttachments = await File.ReadAllBytesAsync(CompositionAttachmentStore.PathFor(directory), TestContext.Current.CancellationToken).ConfigureAwait(true);
        foreach (string presented in new[] { peer.AttachedRunId, peer.OriginalAttachmentRequestId })
        {
            ToolCommandResult repeatedEnd = await CompositionAttachAdmission.EndAsync(store, retained.RunId, presented, "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
            repeatedEnd.Status.ShouldBe("ended");
            repeatedEnd.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            repeatedEnd.Attachment.ShouldBe(withdrawnPeer);
        }

        (await File.ReadAllBytesAsync(CompositionAttachmentStore.PathFor(directory), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(archivedAttachments);
        ModuleTestRunRecord released = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(retained.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionRunListing.Retention(retained with { TestRun = released, CleanupDisposition = released.CleanupDisposition }, ended).ShouldBe("released");
        TestEnvironmentContractReader.ReadResult(Path.Combine(directory, "test-result.json"), TestContext.Current.CancellationToken).Document!.Value.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("released");
        File.Exists(store.PathFor(retained.RunId)).ShouldBeFalse();
        (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().ShouldNotContain(item => item.OwnerRunId == retained.RunId);
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
    }

    /// <summary>A concurrent complete down must not produce a diagnostic saying the environment remains running.</summary>
    /// <returns>The completed disposition and absent retention diagnostic assertions.</returns>
    [Fact]
    public async Task ConcurrentCompleteDownSuppressesAttachedRetentionDiagnosticAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);
        CompositionRunSession session = run.Session.ShouldNotBeNull();
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionRunState owner = (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        string entered = Path.Combine(workspace.Root, "retention-scan-entered");
        string release = Path.Combine(workspace.Root, "retention-scan-release");
        string downDocker = Path.Combine(workspace.Root, "down-docker");
        CompositionTestFiles.WriteScript(downDocker, "exit 0");
        CompositionTestFiles.WriteScript(run.Options.DockerCommand, "touch '" + entered + "'\nwhile [ ! -f '" + release + "' ]; do sleep 0.01; done\nexit 0");
        try
        {
            _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, run.Seeded.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
            for (int attempt = 0; attempt < 400 && !File.Exists(entered); attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            File.Exists(entered).ShouldBeTrue();
            CompositionDownResult down = await new CompositionEngine(run.Options with { SelectedTest = owner.SelectedTest, DockerCommand = downDocker }).DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
            down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Output.ToString().ShouldNotContain("HXI012");
        run.Cleanups.ShouldBe(0);
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
        ModuleTestRunRecord completed = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        completed.CleanupDisposition.ShouldBe("complete");
        completed.ResourceDisposition.ShouldBe("removed");
    }

    /// <summary>A failure recorded after the last hold poll still retains resources when cancellation interrupts the wait.</summary>
    /// <returns>The post-withdrawal failure recheck and durable retention assertions.</returns>
    [Fact]
    public async Task AttachedFailureAfterLastHoldPollThenCancellationKeepsRetentionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, holdPollInterval: TimeSpan.FromMinutes(1)).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);
        CompositionRunSession session = run.Session.ShouldNotBeNull();
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        ToolCommandResult ended = await CompositionAttachAdmission.EndAsync(store, session.RunId, run.Seeded.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Attachment.ShouldNotBeNull().Outcome.ShouldBe("failed");
        run.Running.IsCompleted.ShouldBeFalse();
        await cancel.CancelAsync().ConfigureAwait(true);
        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Cleanups.ShouldBe(0);
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
        CompositionRunState retained = (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        retained.TestRun.ShouldNotBeNull().ResourceDisposition.ShouldBe("retained");
        retained.TestRun.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        retained.CleanupDisposition.ShouldBe("not-requested");
        CompositionRunListingEntry listed = (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        listed.Retention.ShouldBe("failure-retained");
        listed.FailureRunIds.ShouldBe([run.Seeded.AttachedRunId]);
    }

    /// <summary>Owner cancellation preserves an earlier attached failure, withdraws peers, and leaves the ready environment attachable.</summary>
    /// <returns>The retained-cancellation assertions.</returns>
    [Fact]
    public async Task AttachedFailureThenOwnerCancellationKeepsRetentionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, blockNative: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        _ = await run.WaitForNativeAsync(workspace.Root).ConfigureAwait(true);

        CompositionRunSession session = run.Session.ShouldNotBeNull();
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionRunState owner = (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionAttachment peer = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, run.Seeded.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        run.Output.ToString().ShouldContain("HXI011");
        IReadOnlyList<CompositionAttachment> attachments = await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        attachments.Single(item => item.AttachedRunId == run.Seeded.AttachedRunId).Outcome.ShouldBe("failed");
        attachments.Single(item => item.AttachedRunId == peer.AttachedRunId).EndReason.ShouldBe("owner-cancelled");
        CompositionRunState retained = (await store.TryReadAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        retained.TestRun.ShouldNotBeNull().ResourceDisposition.ShouldBe("retained");
        _ = (await CompositionAttachAdmission.AdmitAsync(store, Request(retained, retained.SelectedTest!.ManifestHash), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
    }

    /// <summary>Explicit down refuses corrupt attachment metadata before deleting any workspace, then succeeds on retry.</summary>
    /// <returns>The no-teardown, withdrawal and retry assertions.</returns>
    [Fact]
    public async Task DownRequiresDurableWithdrawalAndReportsBeforeTeardownAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string path = CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId));
        byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(path, "{invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!));
        CompositionDownResult refused = await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        refused.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR037");
        refused.Result.Outcome.RuleId.ShouldBe("HXR037");
        refused.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        Directory.Exists(owner.Workspace).ShouldBeTrue();
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionDownResult retried = await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, string.Join(';', retried.Result.Diagnostics.Select(item => item.Message)));
        retried.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Message.Contains(hold.AttachedRunId, StringComparison.Ordinal) && item.Field == "developer-stop");
        Directory.Exists(owner.Workspace).ShouldBeFalse();
        CompositionAttachment ended = (await CompositionAttachmentStore.ReadAsync(Path.GetDirectoryName(path)!, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        ended.Outcome.ShouldBe("withdrawn");
        ended.EndReason.ShouldBe("developer-stop");
    }

    /// <summary>Owner cancellation after a completed explicit down does not recreate the removed owner tracking.</summary>
    /// <returns>The absent state and listing assertions.</returns>
    [Fact]
    public async Task OwnerCancellationAfterCompletedDownKeepsTrackingRemovedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!));
        CompositionDownResult down = await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, string.Join(';', down.Result.Diagnostics.Select(item => item.Message)));
        File.Exists(store.PathFor(owner.RunId)).ShouldBeFalse();

        await engine.RecordOwnerCancellationAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(store.PathFor(owner.RunId)).ShouldBeFalse();
        (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().ShouldBeEmpty();
    }

    /// <summary>An unrecordable owner-cancellation withdrawal leaves the environment intact for explicit retry.</summary>
    /// <returns>The pending cleanup diagnostic assertions.</returns>
    [Fact]
    public async Task UnrecordableCancellationWithdrawalDefersCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, liveAppHost: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);

        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(run.Session!.AppHost.Id);
        CompositionEngine engine = new(run.Options);
        ModuleTestRunRecord first = (await engine.ReadCommittedOutcomeAsync(run.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session.RunId);
        FileStream cancellationGate = new(Path.Combine(directory, "outcome.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cancellationGateLifetime = cancellationGate.ConfigureAwait(true);

        // Cancel the waiter before obstructing withdrawal; the outcome gate holds the cancellation pass.
        await cancel.CancelAsync().ConfigureAwait(true);
        string lockPath = Path.Combine(directory, "attachments.lock");
        File.Delete(lockPath);
        _ = Directory.CreateDirectory(lockPath);
        await cancellationGate.DisposeAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        observer.HasExited.ShouldBeFalse();
        run.Output.ToString().ShouldContain("Attachment withdrawal or its report could not be durably recorded");
        ModuleTestRunRecord committed = (await engine.ReadCommittedOutcomeAsync(run.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.CleanupDisposition.ShouldBe("pending");
        committed.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        committed.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        committed.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        Directory.Exists(run.Session.Plan.Workspace).ShouldBeTrue();
    }

    /// <summary>Supported 2.6 expired and terminal records upgrade without blocking exact cleanup.</summary>
    /// <returns>The normalized outcomes, unchanged end records and successful cleanup assertions.</returns>
    [Fact]
    public async Task LegacyExpiredAndTerminalAttachmentsUpgradeBeforeCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment expired = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionAttachment terminal = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        expired = expired with { EndReason = "expired", EndedAtUtc = expired.ExpiresAtUtc };
        terminal = terminal with { EndReason = "terminal-outcome", EndedAtUtc = terminal.AcceptedAtUtc };
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        await CompositionDocumentStore.WriteAsync(CompositionAttachmentStore.PathFor(directory), new CompositionAttachmentDocument(CompositionAttachmentDocument.SupportedSchema, owner.RunId, [expired, terminal]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        IReadOnlyList<CompositionAttachment> read = await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        read.Single(item => item.AttachedRunId == expired.AttachedRunId).Outcome.ShouldBe("withdrawn");
        read.Single(item => item.AttachedRunId == expired.AttachedRunId).EndedAtUtc.ShouldBe(expired.ExpiresAtUtc);
        read.Single(item => item.AttachedRunId == terminal.AttachedRunId).Outcome.ShouldBe("unknown");
        read.ShouldAllBe(item => item.IsConsistent());
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        CompositionDownResult down = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!)).DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        down.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Field == "expired" && item.Message.Contains(expired.AttachedRunId, StringComparison.Ordinal));
        Directory.Exists(owner.Workspace).ShouldBeFalse();
    }

    /// <summary>Report persistence cannot cause a new admission to refuse after its hold is committed.</summary>
    /// <returns>The accepted admission despite a blocked observation-report path.</returns>
    [Fact]
    public async Task BlockedWithdrawalReportDoesNotChangeAdmissionSemanticsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "passed", hold.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        _ = Directory.CreateDirectory(Path.Combine(directory, "attachment-withdrawals.json"));
        ToolCommandResult admitted = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionAttachment accepted = admitted.Attachment.ShouldNotBeNull();
        (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldContain(item => item.AttachedRunId == accepted.AttachedRunId);
    }

    /// <summary>An expiry observed during the success wait is reported durably before automatic cleanup starts.</summary>
    /// <returns>The cumulative report, expiry and output ordering assertions.</returns>
    [Fact]
    public async Task OwnerWaitExpiryIsDurablyReportedBeforeCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None, holdSeconds: 1).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Cleanups.ShouldBe(1);
        run.Output.ToString().ShouldContain("HXI011");
        run.Output.ToString().ShouldContain("expired");
        run.ReportAtCleanup.ShouldNotBeNull().GetProperty("attachments")[0].GetProperty("endedAtUtc").GetDateTimeOffset().ShouldBe(run.Seeded.ExpiresAtUtc);
    }

    /// <summary>Initial attachment-read failures durably defer cleanup while preserving the first passed outcome.</summary>
    /// <returns>The committed pending disposition and no-cleanup assertions.</returns>
    [Fact]
    public async Task InitialAttachmentReadFailureRecordsPendingCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        _ = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!) with
        {
            ContinuousIntegrationOverride = false,
            SourceMapping = new Hexalith.Builds.Tooling.Workspace.SourceMapping(Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
            SelectedPlatformStartOverride = (_, _, _) => Task.FromResult(new CompositionStartResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), owner.RunId, null)),
        };
        CompositionEngine engine = new(options);
        ModuleTestRunRecord first = (await engine.RecordTestOutcomeAsync(owner.RunId, [.. owner.ReadinessResults], new ToolCommandResult("completed", ToolOutcome.Passed(), []), "unknown", new CompositionRunResources([], []), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        await File.WriteAllTextAsync(CompositionAttachmentStore.PathFor(Path.Combine(store.Directory, "diagnostics", owner.RunId)), "{invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        _ = await ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, workspace.Save(), "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options).ConfigureAwait(true);
        ModuleTestRunRecord committed = (await engine.ReadCommittedOutcomeAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.CleanupDisposition.ShouldBe("pending");
        committed.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        committed.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        Directory.Exists(owner.Workspace).ShouldBeTrue();
        output.ToString().ShouldContain("HXT008");
    }

    /// <summary>Reports derive from the terminal records committed under the observation lock, rather than an older expiry projection.</summary>
    /// <returns>The first failure and absent false expiry-report assertions.</returns>
    [Fact]
    public async Task ExpiryObservationReportsOnlyCommittedWithdrawalsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionAttachment old = (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        old.ObservedAt(hold.ExpiresAtUtc).Outcome.ShouldBe("withdrawn");
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, hold.AttachedRunId, "failed", hold.AcceptedAtUtc.AddSeconds(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
        IReadOnlyList<CompositionAttachment> observed = await CompositionAttachmentStore.ObserveAsync(directory, owner.RunId, hold.ExpiresAtUtc, TestContext.Current.CancellationToken).ConfigureAwait(true);
        observed.Single().Outcome.ShouldBe("failed");
        CompositionAttachmentStore.WithdrawalDiagnostics(observed).ShouldBeEmpty();
        File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeFalse();
    }

    /// <summary>An unrecordable expiry report prevents automatic cleanup and records pending cleanup for an explicit retry.</summary>
    /// <returns>The pending disposition, retained withdrawal, durable retry report and first-outcome assertions.</returns>
    [Fact]
    public async Task UnrecordableWaitExpiryReportDefersCleanupUntilDownAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None, holdSeconds: 2, blockNative: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        _ = await run.WaitForNativeAsync(workspace.Root).ConfigureAwait(true);
        CompositionRunSession session = run.Session.ShouldNotBeNull();
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", session.RunId);
        string report = Path.Combine(directory, "attachment-withdrawals.json");
        _ = Directory.CreateDirectory(report);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "native-release"), "release", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string resultPath = await run.WaitForResultAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionEngine engine = new(run.Options);
        ModuleTestRunRecord first = (await engine.ReadCommittedOutcomeAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        first.CleanupDisposition.ShouldBe("pending");
        first.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        run.Cleanups.ShouldBe(0);
        run.Output.ToString().ShouldContain("HXT008");
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
        Directory.Delete(report);
        CompositionDownResult retried = await engine.DownAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        retried.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Field == "expired" && item.Message.Contains(run.Seeded.AttachedRunId, StringComparison.Ordinal));
        ModuleTestRunRecord final = (await engine.ReadCommittedOutcomeAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        final.TerminalOutcome.ShouldBe(first.TerminalOutcome);
        final.TerminalAtUtc.ShouldBe(first.TerminalAtUtc);
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        Directory.Exists(session.Plan.Workspace).ShouldBeFalse();
    }

    /// <summary>Both attachment-lock orderings either withdraw a racing admitted run or refuse it after cancellation/down state changes.</summary>
    /// <param name="down">Whether explicit down rather than owner cancellation withdraws the environment.</param>
    /// <param name="admissionFirst">Whether admission wins the attachment lock before the stop state is recorded.</param>
    /// <returns>The raced admission, withdrawal, report and immutable binding assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AdmissionAndWithdrawalRespectBothLockOrderingsAsync(bool down, bool admissionFirst)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment original = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!));
        string stoppingLock = down ? "cleanup.lock" : "outcome.lock";
        string lockName = admissionFirst ? stoppingLock : "attachments.lock";
        FileStream gate = new(Path.Combine(directory, lockName), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable gateLifetime = gate.ConfigureAwait(true);
        Task<ToolCommandResult> admission;
        Task stopping;
        if (admissionFirst)
        {
            stopping = StopAsync();
            stopping.IsCompleted.ShouldBeFalse();
            admission = CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            _ = (await admission.ConfigureAwait(true)).Attachment.ShouldNotBeNull();
        }
        else
        {
            admission = CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken);
            admission.IsCompleted.ShouldBeFalse();
            stopping = StopAsync();
            for (int attempt = 0; attempt < 200; attempt++)
            {
                CompositionRunState? current = await store.TryReadAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                if (current?.Status == CompositionRunStatus.Cancelled || current?.CleanupDisposition == "in-progress")
                {
                    break;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            CompositionRunState marked = (await store.TryReadAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            (marked.Status == CompositionRunStatus.Cancelled || marked.CleanupDisposition == "in-progress").ShouldBeTrue();
        }

        await gate.DisposeAsync().ConfigureAwait(true);
        await stopping.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult result = await admission.ConfigureAwait(true);
        IReadOnlyList<CompositionAttachment> recorded = await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        recorded.ShouldAllBe(item => item.Outcome == "withdrawn" && item.EndReason == (down ? "developer-stop" : "owner-cancelled"));
        recorded.Single(item => item.AttachedRunId == original.AttachedRunId).ExpiresAtUtc.ShouldBe(original.ExpiresAtUtc);
        if (admissionFirst)
        {
            recorded.ShouldContain(item => item.AttachedRunId == result.Attachment!.AttachedRunId);
        }
        else
        {
            result.Attachment.ShouldBeNull();
            result.Diagnostics.Single().RuleId.ShouldBeOneOf("HXR035", "HXR033");
            recorded.Count.ShouldBe(1);
        }

        _ = AssertWithdrawalReport(directory, recorded);

        async Task StopAsync()
        {
            if (down)
            {
                (await engine.DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
            }
            else
            {
                await engine.RecordOwnerCancellationAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = await CompositionAttachmentStore.WithdrawAsync(directory, owner.RunId, "owner-cancelled", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
        }
    }

    /// <summary>Session cleanup preserves a running AppHost until both withdrawal and its report can be durably retained.</summary>
    /// <param name="withinBudget">Whether cleanup uses the command's existing session stop budget.</param>
    /// <param name="blockReport">Whether the report rather than attachment metadata is unrecordable.</param>
    /// <returns>The live-process refusal, durable stop ordering and successful retry assertions.</returns>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SessionCleanupKeepsLiveAppHostUntilWithdrawalAndReportSucceedAsync(bool withinBudget, bool blockReport)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        string report = Path.Combine(directory, "attachment-withdrawals.json");
        string observation = Path.Combine(workspace.Root, "apphost-stop-report.json");
        const string script = "import sys,pathlib\nprint('ready',flush=True)\nsys.stdin.readline()\npathlib.Path(sys.argv[2]).write_text(pathlib.Path(sys.argv[1]).read_text())\n";
        using System.Diagnostics.Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", script, report, observation], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner.RunId }) };
        appHost.Start().ShouldBeTrue();
        (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(appHost.Id);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = owner.RunId, EnvironmentId = owner.EnvironmentId!, AttemptId = owner.AttemptId!, Workspace = owner.Workspace, SelectedTest = owner.SelectedTest };
        CompositionReadiness readiness = new(CompositionReadiness.SupportedSchema, owner.RunId, new Uri("http://127.0.0.1:20012/"), null, [.. owner.ReadinessResults]);
        CompositionRunSession session = new(plan, readiness, "private-key", appHost);
        CompositionEngine engine = new(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!) with { StopTimeout = TimeSpan.FromSeconds(2) });
        string attachmentsPath = CompositionAttachmentStore.PathFor(directory);
        byte[] original = await File.ReadAllBytesAsync(attachmentsPath, TestContext.Current.CancellationToken).ConfigureAwait(true);
        try
        {
            if (blockReport)
            {
                _ = Directory.CreateDirectory(report);
            }
            else
            {
                await File.WriteAllTextAsync(attachmentsPath, "{invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            CompositionDownResult refused = await StopAsync().ConfigureAwait(true);
            refused.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR037");
            observer.HasExited.ShouldBeFalse();
            Directory.Exists(owner.Workspace).ShouldBeTrue();
            File.Exists(observation).ShouldBeFalse();
            if (blockReport)
            {
                Directory.Delete(report);
            }
            else
            {
                await File.WriteAllBytesAsync(attachmentsPath, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            CompositionDownResult retried = await StopAsync().ConfigureAwait(true);
            retried.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success, string.Join(';', retried.Result.Diagnostics.Select(item => item.Message)));
            observer.Refresh();
            observer.HasExited.ShouldBeTrue();
            using JsonDocument seen = JsonDocument.Parse(await File.ReadAllTextAsync(observation, TestContext.Current.CancellationToken).ConfigureAwait(true));
            seen.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(item => item.GetProperty("ruleId").GetString() == "HXI011" && item.GetProperty("message").GetString()!.Contains(hold.AttachedRunId, StringComparison.Ordinal));
            seen.RootElement.GetProperty("attachments")[0].GetProperty("outcome").GetString().ShouldBe("withdrawn");
            Directory.Exists(owner.Workspace).ShouldBeFalse();
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }

        Task<CompositionDownResult> StopAsync() => withinBudget
            ? engine.DownWithinBudgetAsync(session, TestContext.Current.CancellationToken)
            : engine.DownAsync(session, TestContext.Current.CancellationToken);
    }

    /// <summary>Invalid outcome/end combinations cannot influence admission or failure retention.</summary>
    /// <param name="reason">The recorded end reason, or null while active.</param>
    /// <param name="outcome">The inconsistent recorded outcome.</param>
    /// <returns>The rejected durable read and owner-unverifiable admission assertions.</returns>
    [Theory]
    [InlineData(null, "failed")]
    [InlineData("terminal-outcome", "withdrawn")]
    [InlineData("expired", "passed")]
    [InlineData("developer-stop", null)]
    public async Task InconsistentAttachmentOutcomesRefuseReadAndAdmissionAsync(string? reason, string? outcome)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionAttachment invalid = hold with { EndReason = reason, Outcome = outcome, EndedAtUtc = reason is null ? null : hold.ExpiresAtUtc };
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        await CompositionDocumentStore.WriteAsync(CompositionAttachmentStore.PathFor(directory), new CompositionAttachmentDocument(CompositionAttachmentDocument.SupportedSchema, owner.RunId, [invalid]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await Should.ThrowAsync<IOException>(() => CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        ToolCommandResult refused = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        refused.Attachment.ShouldBeNull();
        refused.Diagnostics.ShouldContain(item => item.RuleId == "HXR033" && item.Field == "owner-unverifiable");
    }

    /// <summary>A spent resource stopping allowance still permits durable owner cancellation and withdrawal.</summary>
    /// <returns>The immutable cancellation, withdrawn hold and exhausted cleanup-token assertions.</returns>
    [Fact]
    public async Task ExhaustedStopBudgetStillRecordsCancellationAndWithdrawsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, liveAppHost: true, blockNative: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        CompositionRunState owner = await run.WaitForNativeAsync(workspace.Root).ConfigureAwait(true);
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(run.Session!.AppHost.Id);
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", owner.RunId);
        FileStream outcomeGate = new(Path.Combine(directory, "outcome.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outcomeGateLifetime = outcomeGate.ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await outcomeGate.DisposeAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(1);
        run.CleanupTokenWasCancelled.ShouldBeTrue(run.Output.ToString());
        run.WithdrawalsAtCleanup.Single().Outcome.ShouldBe("withdrawn");
        run.WithdrawalsAtCleanup.Single().EndReason.ShouldBe("owner-cancelled");
        run.OwnerStatusAtCleanup.ShouldBe(CompositionRunStatus.Cancelled);
        ModuleTestRunRecord committed = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Cancelled);
        run.Output.ToString().ShouldContain("HXI011");
        await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        observer.HasExited.ShouldBeTrue();
        File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeTrue();
    }

    /// <summary>Unverifiable terminal publication prevents stopping a withdrawn session even after its stop budget expires.</summary>
    /// <returns>The durable withdrawal, pending first pass and live AppHost assertions.</returns>
    [Fact]
    public async Task CancellationPublicationRefusalPreservesLiveAppHostAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, liveAppHost: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        string resultPath = await run.WaitForResultAsync().ConfigureAwait(true);
        CompositionRunSession session = run.Session.ShouldNotBeNull();
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(session.AppHost.Id);
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", session.RunId);
        FileStream outcomeGate = new(Path.Combine(directory, "outcome.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outcomeGateLifetime = outcomeGate.ConfigureAwait(true);
        await File.WriteAllTextAsync(resultPath, "{invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        await Task.Delay(TimeSpan.FromSeconds(4.5), TestContext.Current.CancellationToken).ConfigureAwait(true);
        observer.HasExited.ShouldBeFalse();
        await outcomeGate.DisposeAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        observer.HasExited.ShouldBeFalse();
        run.Output.ToString().ShouldContain("Automatic cleanup is pending until a valid terminal result is durably retained");
        (await CompositionAttachmentStore.ReadAsync(directory, session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().Outcome.ShouldBe("withdrawn");
        ModuleTestRunRecord committed = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
        committed.CleanupDisposition.ShouldBe("pending");
        Directory.Exists(session.Plan.Workspace).ShouldBeTrue();
    }

    /// <summary>Cancellation stops the withdrawn session before a held cleanup lock exhausts the remaining budget.</summary>
    /// <returns>The bounded AppHost exit and retryable cleanup-lock failure assertions.</returns>
    [Fact]
    public async Task CancellationStopsLiveAppHostBeforeCleanupLockExpiresAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, liveAppHost: true, blockNative: true, useEngineCleanup: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        CompositionRunState owner = await run.WaitForNativeAsync(workspace.Root).ConfigureAwait(true);
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(run.Session!.AppHost.Id);
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", owner.RunId);
        FileStream cleanupGate = new(Path.Combine(directory, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable cleanupGateLifetime = cleanupGate.ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Output.ToString().ShouldContain("HXR023");
        await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        observer.HasExited.ShouldBeTrue();
        (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().EndReason.ShouldBe("owner-cancelled");
        File.Exists(Path.Combine(directory, "attachment-withdrawals.json")).ShouldBeTrue();
        Directory.Exists(run.Session.Plan.Workspace).ShouldBeTrue();
    }

    /// <summary>A retained resource disposition without any recorded failure cannot reopen a passed owner for admission.</summary>
    /// <returns>The owner-passed refusal despite the retained disposition.</returns>
    [Fact]
    public async Task PassedOwnerWithRetainedDispositionRefusesAttachmentAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => Finished(state, "passed")).ConfigureAwait(true);
        owner = owner with { TestRun = owner.TestRun! with { ResourceDisposition = "retained", FailureRetained = false } };
        await store.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ToolCommandResult refused = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        refused.Attachment.ShouldBeNull();
        refused.Diagnostics.ShouldContain(item => item.RuleId == "HXR035" && item.Field == "owner-passed");
    }

    /// <summary>A failed attempt to persist pending cleanup remains visible in diagnostics.</summary>
    /// <returns>The unchanged durable disposition and explicit unrecorded-state diagnostic.</returns>
    [Fact]
    public async Task UnrecordablePendingCleanupStateReportsEvidenceFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);
        string directory = Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId);
        FileStream gate = new(Path.Combine(directory, "outcome.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable gateLifetime = gate.ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        run.Output.ToString().ShouldContain("Pending cleanup state could not be durably recorded");
        await gate.DisposeAsync().ConfigureAwait(true);
        ModuleTestRunRecord committed = (await new CompositionEngine(run.Options).ReadCommittedOutcomeAsync(run.Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        committed.CleanupDisposition.ShouldBe("not-requested");
        committed.TerminalOutcome.ExitCode.ShouldBe(ToolExitCode.Success);
    }

    /// <summary>Cumulative observation, cancellation and teardown reports name each withdrawn run once.</summary>
    /// <returns>The unique command-level HXI011 assertions through incomplete cleanup.</returns>
    [Fact]
    public async Task CumulativeWithdrawalDiagnosticsDoNotRepeatAttachedRunsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, delayNative: true, failCleanup: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = run.ConfigureAwait(true);
        CompositionRunState owner = await run.WaitForOwnerAsync().ConfigureAwait(true);
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionAttachment expired = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        _ = await CompositionAttachmentStore.UpdateAsync<int>(directory, owner.RunId, current => (0, [.. current.Select(item => item.AttachedRunId == expired.AttachedRunId ? item with { AcceptedAtUtc = item.AcceptedAtUtc.AddHours(-1), ExpiresAtUtc = item.ExpiresAtUtc.AddHours(-1) } : item)]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(1);
        using JsonDocument output = JsonDocument.Parse(run.Output.ToString());
        JsonElement[] reports = [.. output.RootElement.GetProperty("diagnostics").EnumerateArray().Where(item => item.GetProperty("ruleId").GetString() == "HXI011")];
        reports.Count(item => item.GetProperty("message").GetString()!.Contains(expired.AttachedRunId, StringComparison.Ordinal)).ShouldBe(1);
        reports.Count(item => item.GetProperty("message").GetString()!.Contains(run.Seeded.AttachedRunId, StringComparison.Ordinal)).ShouldBe(1);
    }

    /// <summary>Only local selected-test owners require durable withdrawal before resource stopping.</summary>
    /// <param name="context">The recorded owner locality or environment-only category.</param>
    /// <param name="blockOutcome">Whether the selected-test state write rather than cleanup marker is blocked.</param>
    /// <returns>The strict local refusal or best-effort nonattachable cleanup assertions.</returns>
    [Theory]
    [InlineData("local", false)]
    [InlineData("ci", false)]
    [InlineData("missing", false)]
    [InlineData("environment", false)]
    [InlineData("local", true)]
    [InlineData("ci", true)]
    [InlineData("missing", true)]
    public async Task DownWithdrawalRequirementFollowsAdmissionLocalityAsync(string context, bool blockOutcome)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState original, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        string kind = context == "ci" ? "ci" : "local";
        CompositionRunState owner = original with { ExecutionContext = context == "missing" ? null : JsonSerializer.SerializeToElement(new { kind }), SelectedTest = context == "environment" ? null : original.SelectedTest };
        await store.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        if (!blockOutcome)
        {
            _ = Directory.CreateDirectory(Path.Combine(directory, "cleanup-request.json"));
        }

        Stream outcomeGate = blockOutcome ? new FileStream(Path.Combine(directory, "outcome.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None) : Stream.Null;
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outcomeGateLifetime = outcomeGate.ConfigureAwait(true);
        using System.Diagnostics.Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready',flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner.RunId }) };
        appHost.Start().ShouldBeTrue();
        (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(appHost.Id);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = owner.RunId, EnvironmentId = owner.EnvironmentId!, AttemptId = owner.AttemptId!, Workspace = owner.Workspace, SelectedTest = owner.SelectedTest };
        CompositionRunSession session = new(plan, new(CompositionReadiness.SupportedSchema, owner.RunId, new Uri("http://127.0.0.1:20012/"), null, [.. owner.ReadinessResults]), "private-key", appHost);
        try
        {
            CompositionDownResult result = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, original.SelectedTest!)).DownWithinBudgetAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXT008");
            result.Result.Diagnostics.ShouldNotContain(item => item.Message.Contains("cleanup continued", StringComparison.Ordinal));
            if (blockOutcome)
            {
                File.Exists(Path.Combine(directory, "cleanup-request.json")).ShouldBeTrue();
            }

            if (context == "local")
            {
                result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR037" && item.Message.Contains("cleanup state", StringComparison.Ordinal));
                result.Result.Outcome.RuleId.ShouldBe("HXR037");
                result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
                observer.HasExited.ShouldBeFalse();
                Directory.Exists(owner.Workspace).ShouldBeTrue();
            }
            else
            {
                result.Result.Diagnostics.ShouldNotContain(item => item.RuleId == "HXR037");
                await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
                Directory.Exists(owner.Workspace).ShouldBeFalse();
            }
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>
    /// Provisioning lock stalls after withdrawal still stop and dispose the live session AppHost. Informational withdrawal
    /// reports never classify the incomplete result, and a recorded bookkeeping failure is reported once.
    /// </summary>
    /// <param name="cancelBound">Whether the stop bound expires rather than the provisioning lock timing out.</param>
    /// <param name="environmentOnly">Whether this owner cannot accept attachments.</param>
    /// <param name="blockMarker">Whether the cleanup-request marker cannot be written.</param>
    /// <returns>The durable withdrawal or cleanup-state transition, classification, stopped process and retained workspace assertions.</returns>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task PostWithdrawalProvisioningFailureStopsSessionAppHostAsync(bool cancelBound, bool environmentOnly, bool blockMarker)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        ModuleTestSelection selection = owner.SelectedTest!;
        if (environmentOnly)
        {
            owner = owner with { SelectedTest = null, CleanupDisposition = "not-requested" };
            await store.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        CompositionAttachment? hold = environmentOnly ? null : await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        if (blockMarker)
        {
            _ = Directory.CreateDirectory(Path.Combine(directory, "cleanup-request.json"));
        }

        using System.Diagnostics.Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready',flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner.RunId }) };
        appHost.Start().ShouldBeTrue();
        (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(appHost.Id);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = owner.RunId, EnvironmentId = owner.EnvironmentId!, AttemptId = owner.AttemptId!, Workspace = owner.Workspace, SelectedTest = owner.SelectedTest };
        CompositionRunSession session = new(plan, new(CompositionReadiness.SupportedSchema, owner.RunId, new Uri("http://127.0.0.1:20012/"), null, [.. owner.ReadinessResults]), "private-key", appHost);
        FileStream gate = new(Path.Combine(directory, "provisioning.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable gateLifetime = gate.ConfigureAwait(true);
        using CancellationTokenSource stopping = new(cancelBound ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(10));
        try
        {
            CompositionDownResult result = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, selection)).DownWithinBudgetAsync(session, stopping.Token).ConfigureAwait(true);
            result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR023");
            result.Result.Diagnostics.Count(item => item.RuleId == "HXT008").ShouldBe(blockMarker ? 1 : 0, string.Join(';', result.Result.Diagnostics.Select(item => item.RuleId)));
            result.Result.Outcome.ExitCode.ShouldBe(blockMarker ? ToolExitCode.EvidenceSchemaOrPolicy : ToolExitCode.TopologyOrLifecycle);
            if (hold is not null)
            {
                result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Message.Contains(hold.AttachedRunId, StringComparison.Ordinal));
                result.Result.Outcome.RuleId.ShouldBe("HXR023");
                (await CompositionAttachmentStore.ReadAsync(directory, owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().Outcome.ShouldBe("withdrawn");
            }
            else
            {
                (await store.TryReadAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull().CleanupDisposition.ShouldBe("in-progress");
                File.Exists(CompositionAttachmentStore.PathFor(directory)).ShouldBeFalse();
                result.Result.Diagnostics.ShouldNotContain(item => item.RuleId == "HXI011");
            }

            await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observer.HasExited.ShouldBeTrue();
            _ = Should.Throw<InvalidOperationException>(() => appHost.HasExited);
            Directory.Exists(owner.Workspace).ShouldBeTrue();
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>Several failed cleanup bookkeeping writes for one owner report HXT008 once beside the withdrawal refusal.</summary>
    /// <returns>The single bookkeeping diagnostic, refusal and retained workspace assertions.</returns>
    [Fact]
    public async Task RepeatedBookkeepingFailuresReportOneRecordingFailureAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        _ = Directory.CreateDirectory(Path.Combine(directory, "cleanup.lock"));
        _ = Directory.CreateDirectory(Path.Combine(directory, "cleanup-request.json"));
        CompositionDownResult result = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!)).DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Diagnostics.Count(item => item.RuleId == "HXT008").ShouldBe(1, string.Join(';', result.Result.Diagnostics.Select(item => item.RuleId)));
        result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR037");
        Directory.Exists(owner.Workspace).ShouldBeTrue();
    }

    /// <summary>
    /// A CI owner can never hold attachments, so its live session AppHost stops before cleanup locks or state reads and
    /// early cleanup exits, including an exhausted stopping budget, never leave it running.
    /// </summary>
    /// <param name="exit">The early cleanup exit: an exhausted budget or an unverifiable owner state.</param>
    /// <returns>The incomplete cleanup, stopped and disposed AppHost and retained workspace assertions.</returns>
    [Theory]
    [InlineData("exhausted-budget")]
    [InlineData("unverifiable-state")]
    public async Task CiSessionStopsAppHostOnEarlyCleanupExitsAsync(string exit)
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace, state => state with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        if (exit == "unverifiable-state")
        {
            await File.WriteAllTextAsync(store.PathFor(owner.RunId), "{invalid", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        using System.Diagnostics.Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready',flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner.RunId }) };
        appHost.Start().ShouldBeTrue();
        (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(appHost.Id);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = owner.RunId, EnvironmentId = owner.EnvironmentId!, AttemptId = owner.AttemptId!, Workspace = owner.Workspace, SelectedTest = owner.SelectedTest };
        CompositionRunSession session = new(plan, new(CompositionReadiness.SupportedSchema, owner.RunId, new Uri("http://127.0.0.1:20012/"), null, [.. owner.ReadinessResults]), "private-key", appHost) { LocalExecution = false };
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(10));
        if (exit == "exhausted-budget")
        {
            await stopping.CancelAsync().ConfigureAwait(true);
        }

        try
        {
            CompositionDownResult result = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!)).DownWithinBudgetAsync(session, stopping.Token).ConfigureAwait(true);
            result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR023");
            result.Result.Diagnostics.ShouldNotContain(item => item.RuleId == "HXR037");
            await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observer.HasExited.ShouldBeTrue();
            _ = Should.Throw<InvalidOperationException>(() => appHost.HasExited);
            Directory.Exists(owner.Workspace).ShouldBeTrue();
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>A Run or Debug session stops its AppHost before contending for the cleanup lock, so a held lock cannot leave it running.</summary>
    /// <returns>The incomplete cleanup, stopped and disposed AppHost and retained workspace assertions.</returns>
    [Fact]
    public async Task SessionDownStopsUnattachableAppHostWhileCleanupLockIsHeldAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState original, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionRunState owner = original with { SelectedTest = null, ExecutionContext = null };
        await store.WriteAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        string directory = Path.Combine(store.Directory, "diagnostics", owner.RunId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        FileStream gate = new(Path.Combine(directory, "cleanup.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable gateLifetime = gate.ConfigureAwait(true);
        using System.Diagnostics.Process appHost = new() { StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready',flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner.RunId }) };
        appHost.Start().ShouldBeTrue();
        (await appHost.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe("ready");
        using System.Diagnostics.Process observer = System.Diagnostics.Process.GetProcessById(appHost.Id);
        CompositionRunPlan plan = CompositionTestFiles.CreatePlan(workspace.Root) with { RunId = owner.RunId, EnvironmentId = owner.EnvironmentId!, AttemptId = owner.AttemptId!, Workspace = owner.Workspace, SelectedTest = null };
        CompositionRunSession session = new(plan, new(CompositionReadiness.SupportedSchema, owner.RunId, new Uri("http://127.0.0.1:20012/"), null, [.. owner.ReadinessResults]), "private-key", appHost);
        try
        {
            CompositionDownResult result = await new CompositionEngine(LocalRunLifecycleFixture.Options(workspace.Root, original.SelectedTest!) with { SelectedTest = null }).DownAsync(session, TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXR023");
            result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
            await observer.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken).ConfigureAwait(true);
            observer.HasExited.ShouldBeTrue();
            _ = Should.Throw<InvalidOperationException>(() => appHost.HasExited);
            Directory.Exists(owner.Workspace).ShouldBeTrue();
        }
        finally
        {
            CompositionProcess.Kill(observer);
        }
    }

    /// <summary>Incomplete teardown after durable withdrawal is classified by its cleanup failure, never by the informational withdrawal report.</summary>
    /// <returns>The withdrawal report, cleanup classification and absent bookkeeping-failure assertions.</returns>
    [Fact]
    public async Task IncompleteTeardownAfterWithdrawalKeepsCleanupClassificationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, _) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionAttachment hold = await SeedHoldAsync(store, owner).ConfigureAwait(true);
        CompositionWorkspace.CreatePrivateDirectory(owner.Workspace);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!);
        CompositionTestFiles.WriteScript(options.DockerCommand, "exit 1");
        CompositionDownResult result = await new CompositionEngine(options).DownAsync(owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Message.Contains(hold.AttachedRunId, StringComparison.Ordinal) && item.Field == "developer-stop");
        result.Result.Diagnostics.ShouldNotContain(item => item.RuleId == "HXT008");
        result.Result.Outcome.RuleId.ShouldBe("HXR023");
        result.Result.Outcome.Category.ShouldBe(ToolFailureCategory.TopologyOrLifecycle);
        result.Result.Outcome.ExitCode.ShouldBe(ToolExitCode.TopologyOrLifecycle);
        (await CompositionAttachmentStore.ReadAsync(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single().Outcome.ShouldBe("withdrawn");
    }

    private static JsonElement AssertWithdrawalReport(string directory, IReadOnlyList<CompositionAttachment> attachments)
    {
        using JsonDocument report = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "attachment-withdrawals.json")));
        foreach (CompositionAttachment attachment in attachments.Where(item => item.Outcome == "withdrawn"))
        {
            report.RootElement.GetProperty("attachments").EnumerateArray().ShouldContain(item => item.GetProperty("attachedRunId").GetString() == attachment.AttachedRunId && item.GetProperty("endReason").GetString() == attachment.EndReason);
            report.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(item => item.GetProperty("ruleId").GetString() == "HXI011" && item.GetProperty("field").GetString() == attachment.EndReason && item.GetProperty("message").GetString()!.Contains(attachment.AttachedRunId, StringComparison.Ordinal));
        }

        return report.RootElement.Clone();
    }

    private static async Task<CompositionAttachment> SeedHoldAsync(CompositionRunStateStore store, CompositionRunState owner)
    {
        DateTimeOffset accepted = DateTimeOffset.UtcNow;
        CompositionAttachment hold = new(CompositionRunPlanFactory.NewRunId(), owner.EnvironmentId!, owner.RunId, CompositionRunPlanFactory.NewRunId(), 600, accepted, 600, accepted.AddSeconds(600));
        _ = await CompositionAttachmentStore.UpdateAsync<int>(Path.Combine(store.Directory, "diagnostics", owner.RunId), owner.RunId, existing => (0, [.. existing, hold]), TestContext.Current.CancellationToken).ConfigureAwait(true);
        return hold;
    }

    private static JsonElement Ci(string job, string repository = "o/r", string run = "1", int attempt = 1) => JsonSerializer.SerializeToElement(new { kind = "ci", repository, workflowRunId = run, workflowAttempt = attempt, jobId = job, runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true });

    private static CompositionAttachRequest Request(CompositionRunState owner, string composition) => new(owner.RunId, "source", composition) { AttacherContext = _local };

    private static CompositionRunState Finished(CompositionRunState state, string scenario)
    {
        ModuleTestSelection selected = state.SelectedTest!;
        ToolOutcome failed = ToolOutcome.Passed().Fail(ToolPhase.Test, ToolFailureCategory.ProductOrTest, "HXT001", ToolExitCode.ProductOrTest);
        ToolOutcome cancelled = ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "HXC130", ToolExitCode.Cancelled);
        ModuleTestRunRecord Record(string status, ToolOutcome outcome, string disposition, bool retained) => new(state.RunId, state.RunId, selected, null, null, null, null, null, false, [], status, outcome, DateTimeOffset.UtcNow, disposition, new CompositionRunResources([], []))
        {
            FailureRetained = retained,
        };

        return scenario switch
        {
            "passed" => state with { TestRun = Record("completed", ToolOutcome.Passed(), "unknown", false) },
            "failed-not-retained" => state with { TestRun = Record("failed", failed, "unknown", false) },
            "cancelled" => state with { TestRun = Record("cancelled", cancelled, "unknown", false) },
            "cleaned" => state with { TestRun = Record("completed", ToolOutcome.Passed(), "removed", false) with { CleanupDisposition = "complete" }, CleanupDisposition = "complete", RemainingResources = new CompositionRunResources([], []) },
            "cleaning" => state with { TestRun = Record("completed", ToolOutcome.Passed(), "unknown", false) with { CleanupDisposition = "in-progress" }, CleanupDisposition = "in-progress" },
            "failed-retained" => state with { Status = CompositionRunStatus.Failed, TestRun = Record("failed", failed, "retained", true) },
            "failed-then-cancelled-retained" => state with { Status = CompositionRunStatus.Cancelled, TestRun = Record("failed", failed, "retained", true) },
            "retained-stopped" => state with { Status = CompositionRunStatus.Failed, TestRun = Record("failed", failed, "retained", true) with { CleanupDisposition = "pending" }, CleanupDisposition = "pending" },
            _ => throw new ArgumentOutOfRangeException(nameof(scenario), scenario, "Unknown owner scenario."),
        };
    }

    private static async Task<(CompositionRunStateStore Store, CompositionRunState Owner, string Composition)> ReadyOwnerAsync(PlatformManifestTestWorkspace workspace, Func<CompositionRunState, CompositionRunState>? change = null)
    {
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        state = state with
        {
            Status = CompositionRunStatus.Ready,
            ReadinessResults = [new CompositionResourceReadiness("owned", "process", "Healthy")],
            RemainingResources = null,
        };
        state = change?.Invoke(state) ?? state;
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected);
        CompositionRunStateStore store = new(options.StateDirectory);
        await store.WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        return (store, state, selected.ManifestHash);
    }

    private static async Task<(int ExitCode, string Text)> RunAsync(string[] arguments, CompositionEngineOptions options)
    {
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(arguments, output, TextWriter.Null, TestContext.Current.CancellationToken, compositionOptions: options).ConfigureAwait(true);
        return (exit, output.ToString());
    }

    private sealed class OwnerRun : IAsyncDisposable
    {
        private readonly System.Diagnostics.Process _appHost = new();
        private System.Diagnostics.Process? _appHostObserver;

        public Task<int> Running { get; private set; } = Task.FromResult(0);

        public StringWriter Output { get; } = new();

        public CompositionEngineOptions Options { get; private set; } = null!;

        public CompositionRunSession? Session { get; private set; }

        public CompositionAttachment Seeded { get; private set; } = null!;

        public int Cleanups { get; private set; }

        public bool CleanupTokenWasCancelled { get; private set; }

        public IReadOnlyList<CompositionAttachment> WithdrawalsAtCleanup { get; private set; } = [];

        public CompositionRunStatus? OwnerStatusAtCleanup { get; private set; }

        public JsonElement? ReportAtCleanup { get; private set; }

        public static async Task<OwnerRun> StartAsync(PlatformManifestTestWorkspace workspace, CancellationToken runToken, bool failing = false, bool delayNative = false, int holdSeconds = 600, bool failCleanup = false, bool liveAppHost = false, bool blockNative = false, bool useEngineCleanup = false, TimeSpan? holdPollInterval = null)
        {
            OwnerRun run = new();
            (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
            if (blockNative || delayNative)
            {
                // Delayed or blocked native runs must not reach the fixture's two-second execution limit under load.
                workspace.Change("modules.0.tests.0.executionTimeoutSeconds", "60");
            }

            string nativeBarrier = blockNative ? "while not pathlib.Path(__file__).with_name('native-release').exists(): time.sleep(0.01)\n" : string.Empty;
            await File.WriteAllTextAsync(
                Path.Combine(workspace.Root, "runner.py"),
                "import os, pathlib, shutil, time\npathlib.Path(__file__).with_name('native-started').write_text('started')\n" + nativeBarrier + (delayNative ? "time.sleep(1)\n" : string.Empty) + "shutil.copy(os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'], pathlib.Path(__file__).parent / 'descriptor-copy.json')\npathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n" + (failing ? "raise SystemExit(1)\n" : string.Empty),
                TestContext.Current.CancellationToken).ConfigureAwait(false);
            string manifest = workspace.Save();
            string nativeDotnet = Path.Combine(workspace.Root, "native-dotnet");
            CompositionTestFiles.WriteScript(nativeDotnet, "exec python3 '" + Path.Combine(workspace.Root, "runner.py") + "'");
            run.Options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
            {
                SelectedTest = null,
                WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
                NativeDotnetCommand = nativeDotnet,
                SourceMapping = new Hexalith.Builds.Tooling.Workspace.SourceMapping(Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
                ContinuousIntegrationOverride = false,
                HoldPollInterval = holdPollInterval ?? TimeSpan.FromMilliseconds(25),
                SelectedPlatformStartOverride = async (_, selection, token) =>
                {
                    CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                    CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                    if (liveAppHost)
                    {
                        run._appHost.StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", "import sys; print('ready',flush=True); sys.stdin.readline()"], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId });
                        run._appHost.Start().ShouldBeTrue();
                        (await run._appHost.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)).ShouldBe("ready");
                        run._appHostObserver = System.Diagnostics.Process.GetProcessById(run._appHost.Id);
                    }

                    run.Session = new CompositionRunSession(plan, ready, "private-lifecycle-signing-key", run._appHost);
                    await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
                    DateTimeOffset accepted = DateTimeOffset.UtcNow;
                    CompositionAttachment hold = new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), holdSeconds, accepted, holdSeconds, accepted.AddSeconds(holdSeconds));
                    run.Seeded = hold;
                    int written = await CompositionAttachmentStore.UpdateAsync<int>(Path.Combine(workspace.Root, "state", "diagnostics", plan.RunId), plan.RunId, existing => (0, [.. existing, hold]), token).ConfigureAwait(false);
                    written.ShouldBe(0);
                    CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selection.ManifestHash, selection.RequestedAtUtc) with
                    {
                        AttemptId = plan.AttemptId,
                        EnvironmentId = plan.EnvironmentId,
                        SelectedTest = selection,
                        Status = CompositionRunStatus.Ready,
                        ReadinessResults = [.. ready.Resources],
                        RetainOnFailure = true,
                        ExecutionContext = _local,
                    };
                    await new CompositionRunStateStore(run.Options.StateDirectory).WriteAsync(issued, token).ConfigureAwait(false);
                    return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, run.Session);
                },
                PlatformDownOverride = async (_, token) =>
                {
                    run.Cleanups++;
                    run.CleanupTokenWasCancelled = token.IsCancellationRequested;
                    run.WithdrawalsAtCleanup = await CompositionAttachmentStore.ReadAsync(Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId), run.Session.RunId, CancellationToken.None).ConfigureAwait(false);
                    run.OwnerStatusAtCleanup = (await new CompositionRunStateStore(run.Options.StateDirectory).TryReadAsync(run.Session.RunId, CancellationToken.None).ConfigureAwait(false))?.Status;
                    run.WithdrawalsAtCleanup.ShouldAllBe(item => !item.IsLive(DateTimeOffset.UtcNow));
                    if (run.WithdrawalsAtCleanup.Any(item => item.Outcome == "withdrawn"))
                    {
                        run.ReportAtCleanup = AssertWithdrawalReport(Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session.RunId), run.WithdrawalsAtCleanup);
                    }

                    Directory.Delete(run.Session.Plan.Workspace, true);
                    return new CompositionDownResult(
                        new ToolCommandResult(failCleanup ? "failed" : "completed", failCleanup ? ToolOutcome.Passed().Fail(ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "HXR023", ToolExitCode.TopologyOrLifecycle) : ToolOutcome.Passed(), CompositionAttachmentStore.WithdrawalDiagnostics(run.WithdrawalsAtCleanup)),
                        new CompositionRunResources([], []));
                },
            };
            if (useEngineCleanup)
            {
                run.Options = run.Options with { PlatformDownOverride = null };
            }

            run.Running = ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, run.Output, runToken, "child.dll", compositionOptions: run.Options, mode: Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Source);
            return run;
        }

        public async Task<CompositionRunState> WaitForOwnerAsync()
        {
            CompositionRunStateStore store = new(Options.StateDirectory);
            for (int attempt = 0; attempt < 400; attempt++)
            {
                if (Session is not null && Seeded is not null && await store.TryReadAsync(Session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(false) is { } state)
                {
                    return state;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The owner state was not issued: " + Output);
        }

        public async Task<CompositionRunState> WaitForNativeAsync(string root)
        {
            CompositionRunState owner = await WaitForOwnerAsync().ConfigureAwait(false);
            for (int attempt = 0; attempt < 400 && !File.Exists(Path.Combine(root, "native-started")); attempt++)
            {
                await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            File.Exists(Path.Combine(root, "native-started")).ShouldBeTrue();
            return owner;
        }

        public async Task<string> WaitForResultAsync()
        {
            for (int attempt = 0; attempt < 400; attempt++)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(false);
                string? candidate = Session is null ? null : Path.Combine(Options.StateDirectory, "diagnostics", Session.RunId, "test-result.json");
                if (candidate is not null && File.Exists(candidate))
                {
                    await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(false);
                    return candidate;
                }
            }

            throw new TimeoutException("The owner result was not published: " + Output);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Running.IsCompleted && Seeded is not null && Session is not null)
                {
                    _ = await CompositionAttachAdmission.EndAsync(new CompositionRunStateStore(Options.StateDirectory), Session.RunId, Seeded.AttachedRunId, "passed", DateTimeOffset.UtcNow, CancellationToken.None).ConfigureAwait(false);
                }

                _ = await Running.WaitAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
            {
                // Teardown of the test double is best effort.
            }

            if (_appHostObserver is not null)
            {
                CompositionProcess.Kill(_appHostObserver);
                _appHostObserver.Dispose();
            }

            _appHost.Dispose();
            await Output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
