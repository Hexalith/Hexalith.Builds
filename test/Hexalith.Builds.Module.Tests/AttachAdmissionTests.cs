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

    /// <summary>The human output of attach and list shows the issued identities, expiry and hold deadline.</summary>
    /// <returns>The human-format assertions.</returns>
    [Fact]
    public async Task HumanOutputShowsAttachedRunAndHoldDeadlineAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (_, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace).ConfigureAwait(true);
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, owner.SelectedTest!);
        (int admittedExit, string admitted) = await RunAsync(["attach", "--owner-run-id", owner.RunId, "--mode", "source", "--composition-identity", composition, "--hold-seconds", "600", "--output", "human"], options).ConfigureAwait(true);
        admittedExit.ShouldBe(0, admitted);
        admitted.ShouldContain("attached-run=");
        admitted.ShouldContain("hold-seconds=600");
        admitted.ShouldContain("expires=");
        (int listedExit, string listed) = await RunAsync(["list", "--output", "human"], options).ConfigureAwait(true);
        listedExit.ShouldBe(0, listed);
        listed.ShouldContain("  attached=");
        listed.ShouldContain("state=recorded");
        listed.ShouldNotContain("hold-deadline=unavailable");
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
        CompositionAttachment expiring = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { HoldSeconds = "2" }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
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

    /// <summary>An attached failure before or during the success hold wait retains the environment and permits fresh attachment.</summary>
    /// <param name="beforeOwner">Whether the attached failure precedes the owner's native completion.</param>
    /// <returns>The owner, retention, listing and fresh-admission assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedFailureRetainsPassedOwnerWithoutWaitingForPeerAsync(bool beforeOwner)
    {
        using PlatformManifestTestWorkspace workspace = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, CancellationToken.None, delayNative: beforeOwner).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        if (beforeOwner)
        {
            _ = await run.WaitForOwnerAsync().ConfigureAwait(true);
        }
        else
        {
            _ = await run.WaitForResultAsync().ConfigureAwait(true);
        }

        CompositionRunSession session = run.Session.ShouldNotBeNull();
        CompositionRunStateStore store = new(run.Options.StateDirectory);
        CompositionAttachment peer = await SeedHoldAsync(store, (await store.TryReadAsync(session.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull()).ConfigureAwait(true);
        _ = await CompositionAttachAdmission.EndAsync(store, session.RunId, run.Seeded.AttachedRunId, "failed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, run.Output.ToString());
        run.Cleanups.ShouldBe(0);
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
    }

    /// <summary>Owner cancellation preserves an earlier attached failure, withdraws peers, and leaves the ready environment attachable.</summary>
    /// <returns>The retained-cancellation assertions.</returns>
    [Fact]
    public async Task AttachedFailureThenOwnerCancellationKeepsRetentionAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token, delayNative: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        _ = await run.WaitForOwnerAsync().ConfigureAwait(true);

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

    /// <summary>An unrecordable owner-cancellation withdrawal leaves the environment intact for explicit retry.</summary>
    /// <returns>The pending cleanup diagnostic assertions.</returns>
    [Fact]
    public async Task UnrecordableCancellationWithdrawalDefersCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource cancel = new();
        OwnerRun run = await OwnerRun.StartAsync(workspace, cancel.Token).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable runLifetime = run.ConfigureAwait(true);
        _ = await run.WaitForResultAsync().ConfigureAwait(true);

        // A directory at the lock path makes acquisition fail without allowing an attachment mutation.
        string lockPath = Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId, "attachments.lock");
        File.Delete(lockPath);
        _ = Directory.CreateDirectory(lockPath);
        await cancel.CancelAsync().ConfigureAwait(true);
        _ = await run.Running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        run.Cleanups.ShouldBe(0);
        run.Output.ToString().ShouldContain("HXT008");
        Directory.Exists(run.Session.Plan.Workspace).ShouldBeTrue();
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

        public Task<int> Running { get; private set; } = Task.FromResult(0);

        public StringWriter Output { get; } = new();

        public CompositionEngineOptions Options { get; private set; } = null!;

        public CompositionRunSession? Session { get; private set; }

        public CompositionAttachment Seeded { get; private set; } = null!;

        public int Cleanups { get; private set; }

        public bool CleanupTokenWasCancelled { get; private set; }

        public IReadOnlyList<CompositionAttachment> WithdrawalsAtCleanup { get; private set; } = [];

        public CompositionRunStatus? OwnerStatusAtCleanup { get; private set; }

        public static async Task<OwnerRun> StartAsync(PlatformManifestTestWorkspace workspace, CancellationToken runToken, bool failing = false, bool delayNative = false)
        {
            OwnerRun run = new();
            (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
            await File.WriteAllTextAsync(
                Path.Combine(workspace.Root, "runner.py"),
                "import os, pathlib, shutil, time\n" + (delayNative ? "time.sleep(1)\n" : string.Empty) + "shutil.copy(os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'], pathlib.Path(__file__).parent / 'descriptor-copy.json')\npathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n" + (failing ? "raise SystemExit(1)\n" : string.Empty),
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
                HoldPollInterval = TimeSpan.FromMilliseconds(25),
                SelectedPlatformStartOverride = async (_, selection, token) =>
                {
                    CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                    CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                    run.Session = new CompositionRunSession(plan, ready, "private-lifecycle-signing-key", run._appHost);
                    await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
                    DateTimeOffset accepted = DateTimeOffset.UtcNow;
                    CompositionAttachment hold = new(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), 600, accepted, 600, accepted.AddSeconds(600));
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
                    run.WithdrawalsAtCleanup = await CompositionAttachmentStore.ReadAsync(Path.Combine(run.Options.StateDirectory, "diagnostics", run.Session!.RunId), run.Session.RunId, token).ConfigureAwait(false);
                    run.OwnerStatusAtCleanup = (await new CompositionRunStateStore(run.Options.StateDirectory).TryReadAsync(run.Session.RunId, token).ConfigureAwait(false))?.Status;
                    run.WithdrawalsAtCleanup.ShouldAllBe(item => !item.IsLive(DateTimeOffset.UtcNow));
                    run.Cleanups++;
                    run.CleanupTokenWasCancelled = token.IsCancellationRequested;
                    Directory.Delete(run.Session.Plan.Workspace, true);
                    return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], []));
                },
            };
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

            _appHost.Dispose();
            await Output.DisposeAsync().ConfigureAwait(false);
        }
    }
}
