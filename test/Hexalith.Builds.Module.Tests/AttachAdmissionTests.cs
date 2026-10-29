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

    /// <summary>A CI attacher in the same workflow run, attempt and job is accepted.</summary>
    /// <returns>The accepted CI attachment.</returns>
    [Fact]
    public async Task SameJobCiAttachmentIsAcceptedAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (CompositionRunStateStore store, CompositionRunState owner, string composition) = await ReadyOwnerAsync(workspace, state => state with { ExecutionContext = Ci("job-a") }).ConfigureAwait(true);
        ToolCommandResult result = await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition) with { AttacherContext = Ci("job-a") }, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = result.Attachment.ShouldNotBeNull();
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
        ToolCommandResult ended = await CompositionAttachAdmission.EndAsync(store, owner.RunId, one.AttachedRunId, now.AddSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        ended.Attachment.ShouldNotBeNull().EndReason.ShouldBe("terminal-outcome");
        (await File.ReadAllBytesAsync(store.PathFor(owner.RunId), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(before);
        ToolCommandResult again = await CompositionAttachAdmission.EndAsync(store, owner.RunId, one.AttachedRunId, now.AddSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);
        again.Attachment.ShouldNotBeNull().EndedAtUtc.ShouldBe(now.AddSeconds(10));
        ToolCommandResult listed = await CompositionRunListing.ReadAsync(store, now.AddSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        CompositionRunListingEntry entry = listed.Environments.ShouldNotBeNull().Single();
        entry.AttachedRunIds.ShouldBe([two.AttachedRunId]);
        entry.Cleanup.ShouldBe("not-requested");
        (await CompositionAttachAdmission.EndAsync(store, owner.RunId, CompositionRunPlanFactory.NewRunId(), now, TestContext.Current.CancellationToken).ConfigureAwait(true)).Diagnostics.Single().RuleId.ShouldBe("HXR036");

        // The owner keeps accepting attachments after one of them ended.
        _ = (await CompositionAttachAdmission.AdmitAsync(store, Request(owner, composition), now.AddSeconds(31), TestContext.Current.CancellationToken).ConfigureAwait(true)).Attachment.ShouldNotBeNull();
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
        (int endedExit, string endedText) = await RunAsync(["attach", "--end", "--owner-run-id", owner.RunId, "--attached-run-id", attached, "--output", "json"], options).ConfigureAwait(true);
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
        Task waiting = CompositionAttachmentStore.WaitForNoLiveHoldsAsync(directory, owner.RunId, TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        waiting.IsCompleted.ShouldBeFalse();
        _ = await CompositionAttachAdmission.EndAsync(store, owner.RunId, ended.AttachedRunId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        waiting.IsCompleted.ShouldBeFalse();
        await waiting.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken).ConfigureAwait(true);
        DateTimeOffset.UtcNow.ShouldBeGreaterThanOrEqualTo(expiring.ExpiresAtUtc);
    }

    /// <summary>A passed local owner defers cleanup while a hold is live, lists it pending, and cleans after the last end.</summary>
    /// <returns>The owner lifecycle assertions and the published holds.</returns>
    [Fact]
    public async Task PassedOwnerDefersCleanupUntilHoldEndsAsync()
    {
        using PlatformManifestTestWorkspace workspace = new();
        (ModuleTestSelection selected, _) = LocalRunLifecycleFixture.Create(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root, "runner.py"), "import os, pathlib\npathlib.Path(os.environ['HEXALITH_G4_TEST_REPORT_PATH']).write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters total=\"1\" passed=\"1\" failed=\"0\" /></ResultSummary></TestRun>')\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string manifest = workspace.Save();
        using System.Diagnostics.Process appHost = new();
        CompositionRunSession? session = null;
        int cleanups = 0;
        CompositionAttachment? seeded = null;
        string nativeDotnet = Path.Combine(workspace.Root, "native-dotnet");
        CompositionTestFiles.WriteScript(nativeDotnet, "exec python3 '" + Path.Combine(workspace.Root, "runner.py") + "'");
        CompositionEngineOptions options = LocalRunLifecycleFixture.Options(workspace.Root, selected) with
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
                session = new CompositionRunSession(plan, ready, "private-lifecycle-signing-key", appHost);
                await CompositionDocumentStore.WriteAsync(plan.ReadinessPath, ready, token).ConfigureAwait(false);
                DateTimeOffset accepted = DateTimeOffset.UtcNow;
                seeded = new CompositionAttachment(CompositionRunPlanFactory.NewRunId(), plan.EnvironmentId, plan.RunId, CompositionRunPlanFactory.NewRunId(), 600, accepted, 600, accepted.AddSeconds(600));
                CompositionAttachment hold = seeded;
                int written = await CompositionAttachmentStore.UpdateAsync<int>(Path.Combine(workspace.Root, "state", "diagnostics", plan.RunId), plan.RunId, existing => (0, [.. existing, hold]), token).ConfigureAwait(false);
                written.ShouldBe(0);
                return new CompositionStartResult(new ToolCommandResult("ready", ToolOutcome.Passed(), []), plan.RunId, session);
            },
            PlatformDownOverride = (_, _) =>
            {
                cleanups++;
                Directory.Delete(session!.Plan.Workspace, true);
                return Task.FromResult(new CompositionDownResult(new ToolCommandResult("completed", ToolOutcome.Passed(), []), new CompositionRunResources([], [])));
            },
        };
        StringWriter output = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable outputLifetime = output.ConfigureAwait(true);
#pragma warning disable CA2025 // The running task is awaited before the writer is disposed.
        Task<int> running = ModuleCommandExecutionService.ExecuteAsync(ModuleInvocationCommand.Test, manifest, "verify", null, null, ToolOutputFormat.Json, output, TestContext.Current.CancellationToken, "child.dll", compositionOptions: options, mode: Hexalith.Builds.Tooling.Workspace.WorkspaceMode.Source);
#pragma warning restore CA2025
        CompositionRunStateStore store = new(options.StateDirectory);
        string? resultPath = null;
        for (int attempt = 0; attempt < 400 && resultPath is null; attempt++)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken).ConfigureAwait(true);
#pragma warning disable CA1508 // The start override assigns the session on another continuation.
            string candidate = session is null ? string.Empty : Path.Combine(options.StateDirectory, "diagnostics", session.RunId, "test-result.json");
            resultPath = candidate.Length > 0 && File.Exists(candidate) ? candidate : null;
#pragma warning restore CA1508
        }

        _ = resultPath.ShouldNotBeNull(output.ToString());
        await Task.Delay(300, TestContext.Current.CancellationToken).ConfigureAwait(true);
        running.IsCompleted.ShouldBeFalse();
        cleanups.ShouldBe(0);
        CompositionRunListingEntry pending = (await CompositionRunListing.ReadAsync(store, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true)).Environments.ShouldNotBeNull().Single();
        pending.Cleanup.ShouldBe("pending");
        pending.HoldDeadlineUtc.ShouldBe(seeded.ShouldNotBeNull().ExpiresAtUtc);
        TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        _ = await CompositionAttachAdmission.EndAsync(store, session!.RunId, seeded.AttachedRunId, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await running.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(0, output.ToString());
        cleanups.ShouldBe(1);
        TestEnvironmentContractReadResult published = TestEnvironmentContractReader.ReadResult(resultPath, TestContext.Current.CancellationToken);
        published.IsValid.ShouldBeTrue(string.Join(';', published.Diagnostics.Select(item => item.Message)));
        JsonElement hold = published.Document!.Value.GetProperty("bindings").GetProperty("attachments").GetProperty("holds").EnumerateArray().Single();
        hold.GetProperty("attachedRunId").GetString().ShouldBe(seeded.AttachedRunId);
        hold.GetProperty("end").GetProperty("reason").GetString().ShouldBe("terminal-outcome");
    }

    private static JsonElement Ci(string job) => JsonSerializer.SerializeToElement(new { kind = "ci", repository = "o/r", workflowRunId = "1", workflowAttempt = 1, jobId = job, runnerKind = "github-hosted", operatingSystem = "Linux", disposable = true });

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
}
