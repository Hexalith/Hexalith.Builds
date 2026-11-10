// <copyright file="AttachedSuiteExecutionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Runs declared suites as runner-owned attached runs of a live owner, with real Python children.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class AttachedSuiteExecutionTests
{
    private const int _unkillableProcessId = 2_000_000_123;

    private static readonly JsonElement _local = JsonSerializer.SerializeToElement(new { kind = "local" });

    private static readonly JsonElement _ci = JsonSerializer.SerializeToElement(new { kind = "ci", repository = "o/r", workflowRunId = "101", workflowAttempt = 2, jobId = "ci", runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true });

    /// <summary>A passing attached suite publishes a valid attached result with terminated work and access, locally and in CI.</summary>
    /// <param name="ci">Whether the owner runs in a CI job.</param>
    /// <returns>The pass assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassingAttachedSuiteEndsWorkAndAccessAsync(bool ci)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Dictionary<string, string?>? previous = ci ? EnterCiContext() : null;
        try
        {
            Release(workspace, "all");
            Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, ci: ci).ConfigureAwait(true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
            (int exit, JsonElement output) = await owner.AttachAsync([], TestContext.Current.CancellationToken).ConfigureAwait(true);
            exit.ShouldBe(0, output.GetRawText());
            string attachedRunId = output.GetProperty("runId").GetString().ShouldNotBeNull();
            JsonElement result = AssertAttachedResult(owner, attachedRunId, "passed", "tests-completed", "terminal-outcome");
            result.GetProperty("termination").GetProperty("ownedWork")[0].GetProperty("status").GetString().ShouldBe("terminated");
            result.GetProperty("termination").GetProperty("environmentAccess")[0].GetProperty("status").GetString().ShouldBe("terminated");
            result.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe("not-retained");
            output.GetProperty("testResultPath").GetString().ShouldBe(Path.Combine(owner.Options.StateDirectory, "diagnostics", attachedRunId, "test-result.json"));
            CompositionAttachment recorded = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
            AssertIsolation(owner, workspace, recorded);
            string terminationDetail = await File.ReadAllTextAsync(Path.Combine(owner.Options.StateDirectory, "diagnostics", attachedRunId, Reference(result, "termination")), TestContext.Current.CancellationToken).ConfigureAwait(true);
            terminationDetail.ShouldContain("bearer valid until ");
            terminationDetail.ShouldContain("; no holder");
            owner.Running.IsCompleted.ShouldBeFalse("The owner keeps running while its own suite is blocked.");
            Release(workspace, "owner");
            (await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, owner.Output.ToString());
            owner.Cleanups.ShouldBe(1);
        }
        finally
        {
            ExitCiContext(previous);
        }
    }

    /// <summary>An attached failure fails only that run; a local owner retains its environment while CI never retains.</summary>
    /// <param name="ci">Whether the owner runs in a CI job.</param>
    /// <returns>The failure and retention assertions.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedFailureRetainsOnlyLocallyAndLeavesOwnerUntouchedAsync(bool ci)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Dictionary<string, string?>? previous = ci ? EnterCiContext() : null;
        try
        {
            Release(workspace, "all");
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "fail-all"), "fail", TestContext.Current.CancellationToken).ConfigureAwait(true);
            Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, ci: ci).ConfigureAwait(true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
            (int exit, JsonElement output) = await owner.AttachAsync([], TestContext.Current.CancellationToken).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.ProductOrTest, output.GetRawText());
            string attachedRunId = output.GetProperty("runId").GetString().ShouldNotBeNull();
            JsonElement result = AssertAttachedResult(owner, attachedRunId, "failed", "test-failure", "terminal-outcome");
            result.GetProperty("retention").GetProperty("disposition").GetString().ShouldBe(ci ? "not-retained" : "failure-retained");
            owner.Running.IsCompleted.ShouldBeFalse();
            Release(workspace, "owner");
            (await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, owner.Output.ToString());
            if (ci)
            {
                owner.Cleanups.ShouldBe(1);
                owner.Output.ToString().ShouldNotContain("HXI012");
            }
            else
            {
                owner.Cleanups.ShouldBe(0);
                owner.Output.ToString().ShouldContain("HXI012");
                owner.Output.ToString().ShouldContain(attachedRunId);
                Directory.Exists(owner.Session!.Plan.Workspace).ShouldBeTrue();
            }
        }
        finally
        {
            ExitCiContext(previous);
        }
    }

    /// <summary>Expiry stops a still-running child at the runner's expiry; a passing report written before then is ignored.</summary>
    /// <returns>The expiry assertions.</returns>
    [Fact]
    public async Task ExpiryStopsRunningChildAndIgnoresLateReportAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "report-first"), "report", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        (int exit, JsonElement output) = await owner.AttachAsync(["--hold-seconds", "2"], TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.Cancelled, output.GetRawText());
        output.GetProperty("status").GetString().ShouldBe("withdrawn");
        string attachedRunId = output.GetProperty("runId").GetString().ShouldNotBeNull();
        JsonElement result = AssertAttachedResult(owner, attachedRunId, "withdrawn", "hold-expired", "expired");
        CompositionAttachment recorded = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
        result.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset().ShouldBe(recorded.ExpiresAtUtc);
        recorded.EndedAtUtc.ShouldBe(recorded.ExpiresAtUtc);
        recorded.Termination.ShouldNotBeNull().OwnedWork.Single().Status.ShouldBe("terminated");
        File.Exists(Path.Combine(workspace.Root, "reported-" + attachedRunId)).ShouldBeTrue("The child wrote a passing report before expiry.");
        using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "observed-" + attachedRunId + ".json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        observed.RootElement.GetProperty("exp").GetInt64().ShouldBe(recorded.ExpiresAtUtc.ToUnixTimeSeconds(), "A hold shorter than the execution deadline bounds the bearer.");
        AssertNoTaggedProcess(attachedRunId);
        owner.Running.IsCompleted.ShouldBeFalse();
    }

    /// <summary>Two concurrent attachments: one expiring leaves the other attachment and the owner running.</summary>
    /// <returns>The isolation assertions.</returns>
    [Fact]
    public async Task ExpiringAttachmentLeavesPeerAndOwnerRunningAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> expiring = owner.AttachAsync(["--hold-seconds", "2"], TestContext.Current.CancellationToken);
        string expiringId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> peer = owner.AttachAsync(["--hold-seconds", "600"], TestContext.Current.CancellationToken);
        string peerId = await owner.WaitForStartedAsync(2, expiringId).ConfigureAwait(true);
        (int expiredExit, JsonElement expiredOutput) = await expiring.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        expiredExit.ShouldBe((int)ToolExitCode.Cancelled, expiredOutput.GetRawText());
        _ = AssertAttachedResult(owner, expiringId, "withdrawn", "hold-expired", "expired");
        peer.IsCompleted.ShouldBeFalse();
        (await owner.RecordAsync(peerId).ConfigureAwait(true)).EndedAtUtc.ShouldBeNull();
        owner.Running.IsCompleted.ShouldBeFalse();
        AssertTaggedProcess(peerId);
        Release(workspace, peerId);
        (int peerExit, JsonElement peerOutput) = await peer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        peerExit.ShouldBe(0, peerOutput.GetRawText());
        _ = AssertAttachedResult(owner, peerId, "passed", "tests-completed", "terminal-outcome");
    }

    /// <summary>The attached command's own cancellation ends only that run, as cancelled.</summary>
    /// <returns>The own-cancellation assertions.</returns>
    [Fact]
    public async Task OwnCancellationEndsOnlyThatRunAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        using CancellationTokenSource attacher = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Task<(int Exit, JsonElement Output)> cancelled = owner.AttachAsync([], attacher.Token);
        string cancelledId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> peer = owner.AttachAsync([], TestContext.Current.CancellationToken);
        string peerId = await owner.WaitForStartedAsync(2, cancelledId).ConfigureAwait(true);
        await attacher.CancelAsync().ConfigureAwait(true);
        (int exit, JsonElement output) = await cancelled.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.Cancelled, output.GetRawText());
        output.GetProperty("status").GetString().ShouldBe("cancelled");
        _ = AssertAttachedResult(owner, cancelledId, "cancelled", "run-cancelled", "terminal-outcome");
        AssertNoTaggedProcess(cancelledId);
        peer.IsCompleted.ShouldBeFalse();
        owner.Running.IsCompleted.ShouldBeFalse();
        (await owner.RecordAsync(peerId).ConfigureAwait(true)).EndedAtUtc.ShouldBeNull();
        Release(workspace, peerId);
        (await peer.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).Exit.ShouldBe(0);
    }

    /// <summary>Owner cancellation withdraws an active attachment and ends its work and access before any owner resource stops.</summary>
    /// <returns>The owner-cancellation ordering assertions.</returns>
    [Fact]
    public async Task OwnerCancellationEndsAttachedWorkBeforeOwnerTeardownAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        using CancellationTokenSource ownerToken = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Owner owner = await Owner.StartAsync(workspace, ownerToken.Token).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> attached = owner.AttachAsync([], TestContext.Current.CancellationToken);
        string attachedRunId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        await ownerToken.CancelAsync().ConfigureAwait(true);
        _ = await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (int exit, JsonElement output) = await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.Cancelled, output.GetRawText());
        _ = AssertAttachedResult(owner, attachedRunId, "withdrawn", "owner-cancelled", "owner-cancelled");
        owner.Cleanups.ShouldBe(1);
        CompositionAttachment atCleanup = owner.AttachmentsAtCleanup.Single(item => item.AttachedRunId == attachedRunId);
        atCleanup.Termination.ShouldNotBeNull().Failures.ShouldBeEmpty();
        owner.OwnerStatusAtCleanup.ShouldBe(CompositionRunStatus.Cancelled);
        AssertNoTaggedProcess(attachedRunId);
    }

    /// <summary>CI job end withdraws an active attachment and ends its work and access before owner cleanup.</summary>
    /// <returns>The CI job-end assertions.</returns>
    [Fact]
    public async Task CiJobEndWithdrawsActiveAttachmentBeforeCleanupAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Dictionary<string, string?> previous = EnterCiContext();
        try
        {
            Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, ci: true).ConfigureAwait(true);
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
            Task<(int Exit, JsonElement Output)> attached = owner.AttachAsync([], TestContext.Current.CancellationToken);
            string attachedRunId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
            Release(workspace, "owner");
            (await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, owner.Output.ToString());
            (int exit, JsonElement output) = await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.Cancelled, output.GetRawText());
            _ = AssertAttachedResult(owner, attachedRunId, "withdrawn", "ci-job-completed", "ci-job-completed");
            owner.Cleanups.ShouldBe(1);
            owner.AttachmentsAtCleanup.Single(item => item.AttachedRunId == attachedRunId).Termination.ShouldNotBeNull().Failures.ShouldBeEmpty();
            owner.Output.ToString().ShouldContain("HXI013");
            AssertNoTaggedProcess(attachedRunId);
        }
        finally
        {
            ExitCiContext(previous);
        }
    }

    /// <summary>A down from another terminal ends attached work and access before the first owner resource stops.</summary>
    /// <returns>The external-down ordering assertions.</returns>
    [Fact]
    public async Task DownFromAnotherTerminalEndsAttachedWorkBeforeOwnerResourcesStopAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, liveAppHost: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> attached = owner.AttachAsync([], TestContext.Current.CancellationToken);
        string attachedRunId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        CompositionRunState state = (await new CompositionRunStateStore(owner.Options.StateDirectory).TryReadAsync(owner.Session!.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
        CompositionDownResult down = await new CompositionEngine(owner.Options with { SelectedTest = state.SelectedTest, PlatformDownOverride = null }).DownAsync(state.RunId, TestContext.Current.CancellationToken).ConfigureAwait(true);
        down.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI011" && item.Field == "developer-stop");
        down.Result.Diagnostics.ShouldContain(item => item.RuleId == "HXI013" && item.Message.Contains(attachedRunId, StringComparison.Ordinal));
        (int exit, JsonElement output) = await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.Cancelled, output.GetRawText());
        _ = AssertAttachedResult(owner, attachedRunId, "withdrawn", "developer-stop", "developer-stop");
        CompositionAttachment recorded = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
        DateTimeOffset appHostStopped = DateTimeOffset.Parse(await File.ReadAllTextAsync(owner.AppHostStoppedPath, TestContext.Current.CancellationToken).ConfigureAwait(true), CultureInfo.InvariantCulture);
        recorded.Termination.ShouldNotBeNull().ObservedAtUtc.ShouldBeLessThanOrEqualTo(appHostStopped);
        AssertNoTaggedProcess(attachedRunId);
        _ = await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
    }

    /// <summary>A child that survives its stop is recorded as failed termination with leftovers; the outcome and expiry are unchanged.</summary>
    /// <returns>The stop-failure assertions.</returns>
    [Fact]
    public async Task SurvivingChildIsReportedWithoutChangingOutcomeOrExpiryAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Release(workspace, "all");
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, survivor: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        (int exit, JsonElement output) = await owner.AttachAsync([], TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe(0, output.GetRawText());
        output.GetProperty("diagnostics").EnumerateArray().ShouldContain(item => item.GetProperty("ruleId").GetString() == "HXR038");
        string attachedRunId = output.GetProperty("runId").GetString().ShouldNotBeNull();
        JsonElement result = AssertAttachedResult(owner, attachedRunId, "passed", "tests-completed", "terminal-outcome");
        JsonElement termination = result.GetProperty("termination");
        termination.GetProperty("ownedWork")[0].GetProperty("status").GetString().ShouldBe("failed");
        termination.GetProperty("environmentAccess")[0].GetProperty("status").GetString().ShouldBe("failed");
        termination.GetProperty("leftoverWorkIds").EnumerateArray().Select(item => item.GetString()).ShouldContain("process:" + _unkillableProcessId.ToString(CultureInfo.InvariantCulture));
        termination.GetProperty("failures").GetArrayLength().ShouldBe(2);
        CompositionAttachment recorded = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
        (recorded.ExpiresAtUtc - recorded.AcceptedAtUtc).ShouldBe(TimeSpan.FromSeconds(CompositionAttachment.DefaultHoldSeconds));
    }

    /// <summary>A retry presenting the lineage reconnects without a second child and keeps the first acceptance and expiry; ended or expired lineages refuse.</summary>
    /// <returns>The retry assertions.</returns>
    [Fact]
    public async Task RetryReconnectsLineageAndRefusesEndedOrExpiredAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> first = owner.AttachAsync(["--hold-seconds", "600"], TestContext.Current.CancellationToken);
        string attachedRunId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        CompositionAttachment lineage = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> reconnect = owner.AttachAsync(["--attachment-request-id", lineage.OriginalAttachmentRequestId], TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Release(workspace, attachedRunId);
        (int firstExit, JsonElement firstOutput) = await first.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (int againExit, JsonElement againOutput) = await reconnect.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        firstExit.ShouldBe(0, firstOutput.GetRawText());
        againExit.ShouldBe(0, againOutput.GetRawText());
        againOutput.GetProperty("runId").GetString().ShouldBe(attachedRunId);
        Directory.GetFiles(workspace.Root, "started-*").Length.ShouldBe(1);
        CompositionAttachment ended = await owner.RecordAsync(attachedRunId).ConfigureAwait(true);
        ended.AcceptedAtUtc.ShouldBe(lineage.AcceptedAtUtc);
        ended.ExpiresAtUtc.ShouldBe(lineage.ExpiresAtUtc);
        (int endedExit, JsonElement endedOutput) = await owner.AttachAsync(["--attachment-request-id", attachedRunId], TestContext.Current.CancellationToken).ConfigureAwait(true);
        endedExit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle);
        AssertRefusal(endedOutput, "HXR036", "attachment-ended");
        CompositionAttachment hold = (await owner.HoldOnlyAsync("1").ConfigureAwait(true)).ShouldNotBeNull();
        await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (int expiredExit, JsonElement expiredOutput) = await owner.AttachAsync(["--attachment-request-id", hold.OriginalAttachmentRequestId], TestContext.Current.CancellationToken).ConfigureAwait(true);
        expiredExit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle);
        AssertRefusal(expiredOutput, "HXR036", "attachment-expired");
        Directory.GetFiles(workspace.Root, "started-*").Length.ShouldBe(1);
    }

    /// <summary>Refused requests admit nothing and start no child.</summary>
    /// <param name="scenario">The refused request.</param>
    /// <param name="rule">The stable rule.</param>
    /// <param name="reason">The stable reason, or null for usage refusals.</param>
    /// <returns>The no-admission assertions.</returns>
    [Theory]
    [InlineData("filter", "HXC002", null)]
    [InlineData("evidence", "HXC002", null)]
    [InlineData("candidate", "HXC002", null)]
    [InlineData("receipt", "HXC002", null)]
    [InlineData("with-owner", "HXC002", null)]
    [InlineData("gate", "HXC002", null)]
    [InlineData("composition", "HXR034", "suite-incompatible")]
    [InlineData("owner-passed", "HXR035", "owner-passed")]
    public async Task RefusedRequestAdmitsNothingAndStartsNoChildAsync(string scenario, string rule, string? reason)
    {
        ArgumentNullException.ThrowIfNull(scenario);
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, gateSuite: scenario == "gate").ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        CompositionAttachment? hold = null;
        if (scenario == "owner-passed")
        {
            hold = await owner.HoldOnlyAsync("600").ConfigureAwait(true);
            Release(workspace, "owner");
            await owner.WaitForOwnerOutcomeAsync().ConfigureAwait(true);
        }

        string manifest = owner.Manifest;
        if (scenario == "composition")
        {
            manifest = Path.Combine(workspace.Root, "other.json");
            File.Copy(owner.Manifest, manifest);
            await File.AppendAllTextAsync(manifest, " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        string[] extra = scenario switch
        {
            "filter" => ["--filter", "anything"],
            "evidence" => ["--evidence", "evidence.json"],
            "candidate" => ["--candidate-request", Path.Combine(workspace.Root, "request.json")],
            "receipt" => ["--ci-owner-receipt", Path.Combine(workspace.Root, "receipt.json")],
            "with-owner" => ["--with-owner", "tenants"],
            _ => [],
        };
        int before = (await owner.AttachmentsAsync().ConfigureAwait(true)).Count;
        (int exit, JsonElement output) = await owner.AttachAsync(extra, TestContext.Current.CancellationToken, manifest, scenario == "gate" ? "gated" : "attached").ConfigureAwait(true);
        exit.ShouldBe((int)(rule == "HXC002" ? ToolExitCode.UsageOrManifest : ToolExitCode.TopologyOrLifecycle), output.GetRawText());
        AssertRefusal(output, rule, reason);
        (await owner.AttachmentsAsync().ConfigureAwait(true)).Count.ShouldBe(before);
        Directory.GetFiles(workspace.Root, "started-*").ShouldBeEmpty();
        if (hold is not null)
        {
            _ = await CompositionAttachAdmission.EndAsync(new CompositionRunStateStore(owner.Options.StateDirectory), owner.Session!.RunId, hold.AttachedRunId, "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }
    }

    /// <summary>Without a live owner command, attached-suite execution is refused before admission while hold-only attach still works.</summary>
    /// <returns>The no-live-owner assertions.</returns>
    [Fact]
    public async Task FailedLocalOwnerThatExitedRefusesAttachedSuitesButKeepsHoldOnlyAttachAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Release(workspace, "owner");
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "owner-fail"), "fail", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        (await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.ProductOrTest, owner.Output.ToString());
        File.Exists(AttachedSuiteSupervisorLease.PathFor(owner.DiagnosticsDirectory)).ShouldBeFalse();
        (int exit, JsonElement output) = await owner.AttachAsync([], TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, output.GetRawText());
        AssertRefusal(output, "HXR033", "owner-not-live");
        (await owner.AttachmentsAsync().ConfigureAwait(true)).ShouldBeEmpty();
        Directory.GetFiles(workspace.Root, "started-*").ShouldBeEmpty();
        CompositionAttachment hold = (await owner.HoldOnlyAsync("60").ConfigureAwait(true)).ShouldNotBeNull();
        hold.Execution.ShouldBeNull();
    }

    /// <summary>A failed local owner command stays open until its runner-owned attached runs end.</summary>
    /// <returns>The open-until-ended assertions.</returns>
    [Fact]
    public async Task FailedLocalOwnerStaysOpenUntilAttachedRunsEndAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "owner-fail"), "fail", TestContext.Current.CancellationToken).ConfigureAwait(true);
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        Task<(int Exit, JsonElement Output)> attached = owner.AttachAsync([], TestContext.Current.CancellationToken);
        string attachedRunId = await owner.WaitForStartedAsync(1).ConfigureAwait(true);
        Release(workspace, "owner");
        await owner.WaitForOwnerOutcomeAsync().ConfigureAwait(true);
        await Task.Delay(500, TestContext.Current.CancellationToken).ConfigureAwait(true);
        owner.Running.IsCompleted.ShouldBeFalse("The failed owner hosts a live attached run.");
        Release(workspace, attachedRunId);
        (await attached.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).Exit.ShouldBe(0);
        (await owner.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe((int)ToolExitCode.ProductOrTest, owner.Output.ToString());
        _ = AssertAttachedResult(owner, attachedRunId, "passed", "tests-completed", "terminal-outcome");
        owner.Cleanups.ShouldBe(0);
    }

    /// <summary>An attached run receives its own private credential channel bound to its run, deleted when its access ends.</summary>
    /// <returns>The credential-channel isolation assertions.</returns>
    [Fact]
    public async Task AttachedRunGetsItsOwnCredentialChannelAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Release(workspace, "all");
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken, credentials: true).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        (int exit, JsonElement output) = await owner.AttachAsync([], TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldBe(0, output.GetRawText());
        string attachedRunId = output.GetProperty("runId").GetString().ShouldNotBeNull();
        _ = AssertAttachedResult(owner, attachedRunId, "passed", "tests-completed", "terminal-outcome");
        using JsonDocument observed = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, "observed-" + attachedRunId + ".json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
        string credentials = observed.RootElement.GetProperty("credentials").GetString().ShouldNotBeNull();
        credentials.ShouldBe(Path.Combine(owner.Session!.Plan.Workspace, "attachments", attachedRunId, NativeTestHandoff.EndpointCredentialsFileName));
        observed.RootElement.GetProperty("credentialsRunId").GetString().ShouldBe(attachedRunId);
        File.Exists(credentials).ShouldBeFalse();
        File.Exists(Path.Combine(owner.Session.Plan.Workspace, NativeTestHandoff.EndpointCredentialsFileName)).ShouldBeTrue();
    }

    /// <summary>The hold-only attach interface is preserved; its ended holds record not-applicable termination items.</summary>
    /// <returns>The hold-only termination assertions.</returns>
    [Fact]
    public async Task HoldOnlyAttachRecordsNotApplicableTerminationAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        Owner owner = await Owner.StartAsync(workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable ownerLifetime = owner.ConfigureAwait(true);
        CompositionAttachment hold = (await owner.HoldOnlyAsync("600").ConfigureAwait(true)).ShouldNotBeNull();
        ToolCommandResult ended = await CompositionAttachAdmission.EndAsync(new CompositionRunStateStore(owner.Options.StateDirectory), owner.Session!.RunId, hold.AttachedRunId, "passed", DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Status.ShouldBe("ended");
        CompositionAttachmentTermination termination = ended.Attachment.ShouldNotBeNull().Termination.ShouldNotBeNull();
        termination.Recorder.ShouldBe("hold-only");
        termination.OwnedWork.Concat(termination.EnvironmentAccess).ShouldAllBe(item => item.Status == "not-applicable");
        termination.LeftoverWorkIds.ShouldBeEmpty();
        (await owner.RecordAsync(hold.AttachedRunId).ConfigureAwait(true)).Termination.ShouldBe(termination);
        Directory.GetFiles(workspace.Root, "started-*").ShouldBeEmpty();
    }

    /// <summary>A request the live owner never picks up is recorded as failed with not-applicable termination and a published result.</summary>
    /// <returns>The pickup-timeout assertions.</returns>
    [Fact]
    public async Task RequestNeverPickedUpIsRecordedAsFailedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, CompositionRunState state) = LocalRunLifecycleFixture.Create(workspace);
        string runId = CompositionRunPlanFactory.NewRunId();
        state = state with { RunId = runId, Workspace = Path.Combine(workspace.Root, "runs", runId), Status = CompositionRunStatus.Ready, ReadinessResults = [new CompositionResourceReadiness("owned", "process", "Healthy")], RemainingResources = null };
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with { HoldPollInterval = TimeSpan.FromMilliseconds(25), AttachedPickupTimeout = TimeSpan.FromSeconds(1) };
        await new CompositionRunStateStore(options.StateDirectory).WriteAsync(state, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string directory = Path.Combine(options.StateDirectory, "diagnostics", runId);
        CompositionWorkspace.CreatePrivateDirectory(directory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await CompositionDocumentStore.WriteAsync(AttachedSuiteSupervisorLease.PathFor(directory), new AttachedSuiteSupervisorLease(AttachedSuiteSupervisorLease.SupportedSchema, runId, Environment.ProcessId, null, now, now.AddMinutes(1), "open"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        StringWriter writer = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable writerLifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync(["test", "--manifest", workspace.Save(), "--profile", "verify", "--attach-to", runId, "--output", "json"], writer, TextWriter.Null, TestContext.Current.CancellationToken, "child.dll", options, _local).ConfigureAwait(true);
        exit.ShouldBe((int)ToolExitCode.TopologyOrLifecycle, writer.ToString());
        using JsonDocument output = JsonDocument.Parse(writer.ToString());
        output.RootElement.GetProperty("outcome").GetProperty("ruleId").GetString().ShouldBe("HXR033");
        string attachedRunId = output.RootElement.GetProperty("runId").GetString().ShouldNotBeNull();
        JsonElement result = AssertAttachedResult(options.StateDirectory, runId, attachedRunId, "failed", "startup-failure", "terminal-outcome");
        result.GetProperty("termination").GetProperty("ownedWork")[0].GetProperty("status").GetString().ShouldBe("not-applicable");
        result.GetProperty("termination").GetProperty("environmentAccess")[0].GetProperty("status").GetString().ShouldBe("not-applicable");
        CompositionAttachment recorded = (await CompositionAttachmentStore.ReadAsync(directory, runId, TestContext.Current.CancellationToken).ConfigureAwait(true)).Single();
        recorded.Termination.ShouldNotBeNull().Recorder.ShouldBe("attacher");
        recorded.Execution.ShouldNotBeNull().LaunchedAtUtc.ShouldBeNull();
    }

    private static JsonElement AssertAttachedResult(Owner owner, string attachedRunId, string outcome, string cause, string endReason) =>
        AssertAttachedResult(owner.Options.StateDirectory, owner.Session!.RunId, attachedRunId, outcome, cause, endReason);

    private static JsonElement AssertAttachedResult(string stateDirectory, string ownerRunId, string attachedRunId, string outcome, string cause, string endReason)
    {
        string path = Path.Combine(stateDirectory, "diagnostics", attachedRunId, "test-result.json");
        TestEnvironmentContractReadResult read = TestEnvironmentContractReader.ReadResult(path, TestContext.Current.CancellationToken);
        read.IsValid.ShouldBeTrue(string.Join(";", read.Diagnostics.Select(item => item.RuleId + ":" + item.Message)));
        JsonElement document = read.Document!.Value;
        JsonElement run = document.GetProperty("bindings").GetProperty("run");
        run.GetProperty("role").GetString().ShouldBe("attached");
        run.GetProperty("runId").GetString().ShouldBe(attachedRunId);
        run.GetProperty("attachedRunId").GetString().ShouldBe(attachedRunId);
        run.GetProperty("ownerRunId").GetString().ShouldBe(ownerRunId);
        document.GetProperty("bindings").GetProperty("candidate").GetProperty("classification").GetString().ShouldBe("baseline-only");
        document.GetProperty("bindings").GetProperty("tool").GetProperty("state").GetString().ShouldBe("not-yet-populated");
        document.GetProperty("bindings").GetProperty("deadlines").GetProperty("startup").TryGetProperty("actualDurationSeconds", out _).ShouldBeFalse();
        document.GetProperty("terminal").GetProperty("outcome").GetString().ShouldBe(outcome);
        document.GetProperty("terminal").GetProperty("cause").GetString().ShouldBe(cause);
        JsonElement hold = document.GetProperty("bindings").GetProperty("attachments").GetProperty("holds").EnumerateArray().Single(item => item.GetProperty("attachedRunId").GetString() == attachedRunId);
        hold.GetProperty("end").GetProperty("state").GetString().ShouldBe("ended");
        hold.GetProperty("end").GetProperty("reason").GetString().ShouldBe(endReason);
        JsonElement termination = document.GetProperty("termination");
        termination.GetProperty("state").GetString().ShouldBe("populated");
        termination.GetProperty("ownedWork").GetArrayLength().ShouldBe(1);
        termination.GetProperty("environmentAccess").GetArrayLength().ShouldBe(1);
        return document;
    }

    private static void AssertIsolation(Owner owner, PlatformManifestTestWorkspace workspace, CompositionAttachment recorded)
    {
        string attachedRunId = recorded.AttachedRunId;
        using JsonDocument observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(workspace.Root, "observed-" + attachedRunId + ".json")));
        JsonElement root = observed.RootElement;
        string attachmentWorkspace = Path.Combine(owner.Session!.Plan.Workspace, "attachments", attachedRunId);
        root.GetProperty("descriptor").GetString().ShouldBe(Path.Combine(attachmentWorkspace, "test-environment.json"));
        root.GetProperty("report").GetString().ShouldBe(Path.Combine(attachmentWorkspace, "native-tests", "native.trx"));
        root.GetProperty("sub").GetString().ShouldBe("g4-attached-tests:" + attachedRunId);
        root.GetProperty("exp").GetInt64().ShouldBeLessThanOrEqualTo(recorded.ExpiresAtUtc.ToUnixTimeSeconds());
        DateTimeOffset.Parse(root.GetProperty("credentialExpiry").GetString()!, CultureInfo.InvariantCulture).ToUnixTimeSeconds().ShouldBe(root.GetProperty("exp").GetInt64());
        File.Exists(root.GetProperty("descriptor").GetString()).ShouldBeFalse("The attachment descriptor is deleted when access ends.");
        TestEnvironmentContractReadResult descriptor = TestEnvironmentContractReader.ReadDescriptor(Path.Combine(workspace.Root, "descriptor-" + attachedRunId + ".json"), TestContext.Current.CancellationToken);
        descriptor.IsValid.ShouldBeTrue();
        JsonElement run = descriptor.Document!.Value.GetProperty("bindings").GetProperty("run");
        run.GetProperty("role").GetString().ShouldBe("attached");
        run.GetProperty("runId").GetString().ShouldBe(attachedRunId);
        run.GetProperty("attemptId").GetString().ShouldBe(recorded.Execution!.AttemptId);
        TestEnvironmentContractReadResult ownerDescriptor = TestEnvironmentContractReader.ReadDescriptor(Path.Combine(owner.Session.Plan.Workspace, "test-environment.json"), TestContext.Current.CancellationToken);
        if (ownerDescriptor.IsValid)
        {
            ownerDescriptor.Document!.Value.GetProperty("bindings").GetProperty("run").GetProperty("role").GetString().ShouldBe("owner");
        }

        AssertNoTaggedProcess(attachedRunId);
    }

    private static void AssertRefusal(JsonElement output, string rule, string? reason)
    {
        JsonElement diagnostic = output.GetProperty("diagnostics").EnumerateArray().First(item => item.GetProperty("ruleId").GetString() == rule);
        if (reason is not null)
        {
            diagnostic.GetProperty("field").GetString().ShouldBe(reason);
        }
    }

    private static string Reference(JsonElement result, string id) =>
        result.GetProperty("diagnostics").EnumerateArray().Single(item => item.GetProperty("id").GetString() == id).GetProperty("reference").GetString().ShouldNotBeNull();

    private static void AssertNoTaggedProcess(string attachedRunId)
    {
        (IReadOnlyList<int> processes, bool verified) = new CompositionRunResourceScanner("docker").FindTaggedProcesses(attachedRunId, TestContext.Current.CancellationToken);
        if (OperatingSystem.IsLinux())
        {
            verified.ShouldBeTrue();
            processes.ShouldBeEmpty();
        }
    }

    private static void AssertTaggedProcess(string attachedRunId)
    {
        if (OperatingSystem.IsLinux())
        {
            new CompositionRunResourceScanner("docker").FindTaggedProcesses(attachedRunId, TestContext.Current.CancellationToken).ProcessIds.ShouldNotBeEmpty();
        }
    }

    private static void Release(PlatformManifestTestWorkspace workspace, string target) =>
        File.WriteAllText(Path.Combine(workspace.Root, "release-" + target), "release");

    private static Dictionary<string, string?> EnterCiContext()
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal)
        {
            ["CI"] = "true",
            ["GITHUB_ACTIONS"] = "true",
            ["GITHUB_REPOSITORY"] = "o/r",
            ["GITHUB_RUN_ID"] = "101",
            ["GITHUB_RUN_ATTEMPT"] = "2",
            ["GITHUB_JOB"] = "ci",
            ["RUNNER_ENVIRONMENT"] = "github-hosted",
            ["RUNNER_OS"] = "Linux",
        };
        Dictionary<string, string?> previous = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        foreach ((string name, string value) in values)
        {
            Environment.SetEnvironmentVariable(name, value);
        }

        return previous;
    }

    private static void ExitCiContext(Dictionary<string, string?>? previous)
    {
        foreach ((string name, string? value) in previous ?? [])
        {
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class Owner : IAsyncDisposable
    {
        private const string _attachedScript = """
            import base64, json, os, pathlib, shutil, sys, time
            root = pathlib.Path(__file__).parent
            run = os.environ['HEXALITH_G4_RUN_ID']
            token = os.environ['HEXALITH_G4_ACCESS_TOKEN'].split('.')[1]
            claims = json.loads(base64.urlsafe_b64decode(token + '=' * (-len(token) % 4)))
            credentials = os.environ.get('HEXALITH_TEST_ENDPOINT_CREDENTIALS_PATH')
            bound = json.loads(pathlib.Path(credentials).read_text())['runId'] if credentials else None
            shutil.copy(os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'], root / ('descriptor-' + run + '.json'))
            report = os.environ['HEXALITH_G4_TEST_REPORT_PATH']
            (root / ('observed-' + run + '.json')).write_text(json.dumps({'exp': claims['exp'], 'sub': claims['sub'], 'descriptor': os.environ['HEXALITH_TEST_ENVIRONMENT_PATH'], 'report': report, 'credentials': credentials, 'credentialsRunId': bound, 'credentialExpiry': os.environ['HEXALITH_G4_CREDENTIAL_EXPIRY_UTC']}))
            (root / ('started-' + run)).write_text(str(os.getpid()))
            passing = '<TestRun><ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>'
            if (root / 'report-first').exists():
                pathlib.Path(report).write_text(passing)
                (root / ('reported-' + run)).write_text('reported')
            while not (root / ('release-' + run)).exists() and not (root / 'release-all').exists():
                time.sleep(0.02)
            pathlib.Path(report).write_text(passing)
            raise SystemExit(1 if (root / 'fail-all').exists() else 0)
            """;

        private const string _ownerScript = """
            import os, pathlib, time
            root = pathlib.Path(__file__).parent
            (root / 'owner-started').write_text('started')
            while not (root / 'release-owner').exists():
                time.sleep(0.02)
            pathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome="Completed"><Counters total="1" passed="1" failed="0" /></ResultSummary></TestRun>')
            raise SystemExit(1 if (root / 'owner-fail').exists() else 0)
            """;

        private const string _appHostScript = """
            import datetime, pathlib, signal, sys, time
            def stop(*_):
                pathlib.Path(sys.argv[1]).write_text(datetime.datetime.now(datetime.timezone.utc).isoformat())
                sys.exit(0)
            signal.signal(signal.SIGTERM, stop)
            print('ready', flush=True)
            while True:
                time.sleep(0.05)
            """;

        private readonly Process _appHost = new();
        private JsonElement _context = _local;

        public Task<int> Running { get; private set; } = Task.FromResult(0);

        public StringWriter Output { get; } = new();

        public CompositionEngineOptions Options { get; private set; } = null!;

        public CompositionRunSession? Session { get; private set; }

        public string Manifest { get; private set; } = string.Empty;

        public string AppHostStoppedPath { get; private set; } = string.Empty;

        public int Cleanups { get; private set; }

        public IReadOnlyList<CompositionAttachment> AttachmentsAtCleanup { get; private set; } = [];

        public CompositionRunStatus? OwnerStatusAtCleanup { get; private set; }

        public string DiagnosticsDirectory => Path.Combine(Options.StateDirectory, "diagnostics", Session!.RunId);

        public static async Task<Owner> StartAsync(PlatformManifestTestWorkspace workspace, CancellationToken ownerToken, bool ci = false, bool liveAppHost = false, bool survivor = false, bool credentials = false, bool gateSuite = false)
        {
            Owner owner = new() { _context = ci ? _ci : _local, AppHostStoppedPath = Path.Combine(workspace.Root, "apphost-stopped") };
            _ = LocalRunLifecycleFixture.Create(workspace);
            const string gated = """,{"name":"gated","executor":"python3","entry":"attached.py","reportFormat":"trx","executionTimeoutSeconds":120,"stopTimeoutSeconds":4,"mode":"source","daprRuntimeVersion":"1.18.1"}""";
            string suites = """
                [{"name":"verify","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":120,"stopTimeoutSeconds":4,"mode":"source"},
                 {"name":"attached","executor":"python3","entry":"attached.py","reportFormat":"trx","executionTimeoutSeconds":120,"stopTimeoutSeconds":4,"mode":"source"}
                """ + (gateSuite ? gated : string.Empty) + "]";
            workspace.Change("modules.0.tests", suites);
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), _ownerScript, TestContext.Current.CancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(workspace.Root, "attached.py"), _attachedScript, TestContext.Current.CancellationToken).ConfigureAwait(false);
            string manifest = workspace.Save();
            owner.Manifest = manifest;
            ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
            owner.Options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
            {
                SelectedTest = null,
                WorkspaceRoot = Path.Combine(workspace.Root, "workspace"),
                SourceMapping = new SourceMapping(WorkspaceMode.Source, workspace.Root, "sample", [new("sample", "source", workspace.Root)], false),
                ContinuousIntegrationOverride = ci,
                HoldPollInterval = TimeSpan.FromMilliseconds(25),
                AttachedPickupTimeout = TimeSpan.FromSeconds(15),
                AttachedProcessDiscoveryOverride = survivor ? Survivor : null,
                SelectedPlatformStartOverride = async (_, selection, token) =>
                {
                    CompositionRunPlan plan = await TestEnvironmentProvisioningFixture.PrepareAsync(CompositionTestFiles.CreatePlan(workspace.Root, CompositionRunPlanFactory.NewRunId()) with { SelectedTest = selection }, manifest, workspace.Root).ConfigureAwait(false);
                    CompositionReadiness ready = TestEnvironmentProvisioningFixture.Observe(new CompositionReadiness(CompositionReadiness.SupportedSchema, plan.RunId, new Uri("http://127.0.0.1:20012/"), null, [new("owned", "process", "Healthy")]), "sample");
                    if (credentials)
                    {
                        ready = await WithCredentialsAsync(plan, ready, token).ConfigureAwait(false);
                    }

                    int? appHostId = null;
                    DateTimeOffset? appHostStarted = null;
                    if (liveAppHost)
                    {
                        owner._appHost.StartInfo = CompositionProcess.CreateStartInfo("python3", ["-u", "-c", _appHostScript, owner.AppHostStoppedPath], workspace.Root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = plan.RunId });
                        owner._appHost.Start().ShouldBeTrue();
                        (await owner._appHost.StandardOutput.ReadLineAsync(token).ConfigureAwait(false)).ShouldBe("ready");
                        appHostId = owner._appHost.Id;
                        appHostStarted = owner._appHost.StartTime.ToUniversalTime();
                    }

                    owner.Session = new CompositionRunSession(plan, ready, "private-attached-signing-key", owner._appHost);
                    await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
                    CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(plan.RunId, plan.Workspace, selection.ManifestHash, selection.RequestedAtUtc) with
                    {
                        AttemptId = plan.AttemptId,
                        EnvironmentId = plan.EnvironmentId,
                        SelectedTest = selection,
                        Status = CompositionRunStatus.Ready,
                        ReadinessResults = [.. ready.Resources],
                        RetainOnFailure = !ci,
                        ExecutionContext = owner._context,
                        AppHostProcessId = appHostId,
                        AppHostStartedAt = appHostStarted,
                    };
                    await new CompositionRunStateStore(owner.Options.StateDirectory).WriteAsync(issued, token).ConfigureAwait(false);
                    return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, owner.Session);
                },
                PlatformDownOverride = async (_, _) =>
                {
                    owner.Cleanups++;
                    owner.AttachmentsAtCleanup = await CompositionAttachmentStore.ReadAsync(owner.DiagnosticsDirectory, owner.Session!.RunId, CancellationToken.None).ConfigureAwait(false);
                    owner.OwnerStatusAtCleanup = (await new CompositionRunStateStore(owner.Options.StateDirectory).TryReadAsync(owner.Session.RunId, CancellationToken.None).ConfigureAwait(false))?.Status;
                    Directory.Delete(owner.Session.Plan.Workspace, true);
                    return new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], []));
                },
            };
            owner.Running = ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, owner.Output, ownerToken, "child.dll", compositionOptions: owner.Options, mode: WorkspaceMode.Source);
            await owner.WaitForLeaseAsync().ConfigureAwait(false);
            return owner;
        }

        public async Task<(int Exit, JsonElement Output)> AttachAsync(string[] extra, CancellationToken cancellationToken, string? manifest = null, string profile = "attached")
        {
            StringWriter output = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = output.ConfigureAwait(false);
            string[] arguments = ["test", "--manifest", manifest ?? Manifest, "--profile", profile, "--attach-to", Session!.RunId, "--output", "json", .. extra];
            int exit = await ModuleCommandApplication.InvokeAsync(arguments, output, TextWriter.Null, cancellationToken, "child.dll", Options, _context).ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(output.ToString());
            return (exit, document.RootElement.Clone());
        }

        public async Task<CompositionAttachment?> HoldOnlyAsync(string seconds)
        {
            CompositionRunStateStore store = new(Options.StateDirectory);
            CompositionRunState state = (await store.TryReadAsync(Session!.RunId, TestContext.Current.CancellationToken).ConfigureAwait(false)).ShouldNotBeNull();
            ToolCommandResult admitted = await CompositionAttachAdmission.AdmitAsync(store, new CompositionAttachRequest(state.RunId, "source", state.SelectedTest!.ManifestHash) { HoldSeconds = seconds, AttacherContext = _context }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(false);
            return admitted.Attachment;
        }

        public Task<IReadOnlyList<CompositionAttachment>> AttachmentsAsync() =>
            CompositionAttachmentStore.ReadAsync(DiagnosticsDirectory, Session!.RunId, TestContext.Current.CancellationToken);

        public async Task<CompositionAttachment> RecordAsync(string attachedRunId) =>
            (await AttachmentsAsync().ConfigureAwait(false)).Single(item => item.AttachedRunId == attachedRunId);

        public async Task<string> WaitForStartedAsync(int count, params string[] known)
        {
            for (int attempt = 0; attempt < 1500; attempt++)
            {
                string[] started = [.. Directory.GetFiles(Path.GetDirectoryName(Manifest)!, "started-*").Select(path => Path.GetFileName(path)["started-".Length..])];
                if (started.Length >= count && started.Except(known, StringComparer.Ordinal).FirstOrDefault() is { } fresh)
                {
                    return fresh;
                }

                await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The attached child did not start: " + Output);
        }

        public async Task WaitForOwnerOutcomeAsync()
        {
            for (int attempt = 0; attempt < 1500; attempt++)
            {
                if (File.Exists(Path.Combine(DiagnosticsDirectory, "selected-test.json")))
                {
                    await Task.Delay(200, TestContext.Current.CancellationToken).ConfigureAwait(false);
                    return;
                }

                await Task.Delay(20, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The owner outcome was not recorded: " + Output);
        }

        public async ValueTask DisposeAsync()
        {
            if (Session is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Manifest)!, "release-owner"), "release", CancellationToken.None).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Manifest)!, "release-all"), "release", CancellationToken.None).ConfigureAwait(false);
            }

            try
            {
                _ = await Running.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException or IOException)
            {
                // Teardown of the test double is best effort.
            }

            try
            {
                if (Session is not null && await new CompositionRunStateStore(Options.StateDirectory).TryReadAsync(Session.RunId, CancellationToken.None).ConfigureAwait(false) is { } state)
                {
                    _ = await new CompositionEngine(Options with { SelectedTest = state.SelectedTest, PlatformDownOverride = null }).DownAsync(state.RunId, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                // Remaining test resources are removed with the workspace.
            }

            try
            {
                if (_appHost.StartInfo.FileName.Length > 0 && !_appHost.HasExited)
                {
                    CompositionProcess.Kill(_appHost);
                }
            }
            catch (InvalidOperationException)
            {
                // The double was never started.
            }

            _appHost.Dispose();
            await Output.DisposeAsync().ConfigureAwait(false);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S1172:Unused method parameters should be removed", Justification = "The discovery seam signature is fixed; this double reports one unkillable tagged survivor for every attached run.")]
        private static (IReadOnlyList<int> ProcessIds, bool Verified) Survivor(string runId, CancellationToken cancellationToken) => ([_unkillableProcessId], true);

        private static async Task<CompositionReadiness> WithCredentialsAsync(CompositionRunPlan plan, CompositionReadiness ready, CancellationToken token)
        {
            Uri invocation = new("http://127.0.0.1:20013/v1.0/invoke/sample/method/api/v1/");
            CompositionNativeHttpEndpoint endpoint = new("owned", "sample", "dapr-invocation", invocation, "owned-dapr-cli");
            CompositionResourceReadiness sidecar = new("owned-dapr-cli", "executable", "Healthy") { ModuleId = "sample", ObservedAt = DateTimeOffset.UtcNow, Endpoints = [new("http", "http", new Uri("http://127.0.0.1:20013/"))] };
            string path = Path.Combine(Path.GetFullPath(plan.Workspace), NativeTestHandoff.EndpointCredentialsFileName);
            JsonObject channel = new()
            {
                ["schema"] = NativeTestHandoff.EndpointCredentialsSchema,
                ["runId"] = plan.RunId,
                ["syntheticTenantId"] = plan.TenantNamespace,
                ["dataIsolationId"] = plan.ResourceNamespace,
                ["endpoints"] = new JsonArray(new JsonObject
                {
                    ["resourceId"] = "owned",
                    ["moduleId"] = "sample",
                    ["endpointName"] = "dapr-invocation",
                    ["uri"] = invocation.AbsoluteUri,
                    ["headerName"] = "dapr-api-token",
                    ["value"] = "fixture-attached-sidecar-credential",
                }),
            };
            CompositionWorkspace.CreatePrivateDirectory(plan.Workspace);
            await File.WriteAllTextAsync(path, channel.ToJsonString(), token).ConfigureAwait(false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            return ready with
            {
                Resources = CompositionNativeHttpEndpoints.Bind([.. ready.Resources, sidecar], [endpoint]),
                NativeHttpEndpoints = [endpoint],
                NativeEndpointCredentialsPath = path,
            };
        }

        private async Task WaitForLeaseAsync()
        {
            for (int attempt = 0; attempt < 1500; attempt++)
            {
                if (Session is not null && AttachedSuiteSupervisorLease.IsLive(DiagnosticsDirectory, Session.RunId, DateTimeOffset.UtcNow))
                {
                    return;
                }

                if (Running.IsCompleted)
                {
                    return;
                }

                await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(false);
            }

            throw new TimeoutException("The owner did not publish its attached-suite lease: " + Output);
        }
    }
}
