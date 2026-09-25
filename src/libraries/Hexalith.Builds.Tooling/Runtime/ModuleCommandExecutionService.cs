// <copyright file="ModuleCommandExecutionService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Executes validated module runner commands while preserving the first causal outcome.
/// </summary>
public static class ModuleCommandExecutionService
{
    /// <summary>
    /// Executes one module runner command.
    /// </summary>
    /// <param name="command">The requested command.</param>
    /// <param name="manifestPath">The manifest file path.</param>
    /// <param name="profile">The optional named profile.</param>
    /// <param name="filter">The optional test filter.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <param name="writer">The destination writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> ExecuteAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
        => await ExecuteAsync(
            command,
            manifestPath,
            profile,
            filter,
            null,
            format,
            writer,
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Executes one module runner command and optionally emits its canonical run evidence.
    /// </summary>
    /// <param name="command">The requested command.</param>
    /// <param name="manifestPath">The manifest file path.</param>
    /// <param name="profile">The optional named profile.</param>
    /// <param name="filter">The optional test filter.</param>
    /// <param name="evidencePath">The optional repository-relative evidence output path.</param>
    /// <param name="format">The diagnostic output format.</param>
    /// <param name="writer">The destination writer.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="descriptorChildEntryAssemblyPath">The optional private descriptor child entry assembly.</param>
    /// <param name="runId">The optional run identity to tear down.</param>
    /// <param name="compositionOptions">The optional runner-owned composition settings.</param>
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> ExecuteAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        string? evidencePath,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken,
        string? descriptorChildEntryAssemblyPath = null,
        string? runId = null,
        CompositionEngineOptions? compositionOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(writer);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        CompositionEngine? engine = null;
        CompositionRunSession? session = null;
        bool sessionHandedOff = false;
        ModuleManifest? loadedManifest = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ManifestLoadResult manifestResult = ModuleManifestLoader.Load(manifestPath);
            if (!manifestResult.IsValid)
            {
                return await WriteResultAsync(
                    "failed",
                    ToolOutcome.Passed().Fail(
                        ToolPhase.Manifest,
                        ToolFailureCategory.Manifest,
                        manifestResult.Diagnostics[0].RuleId,
                        ToolExitCode.UsageOrManifest),
                    manifestResult.Diagnostics,
                    format,
                    writer,
                    command,
                    manifestPath,
                    null,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            ModuleManifest manifest = manifestResult.Manifest!;
            loadedManifest = manifest;
            if (!ValidateProfile(command, profile, manifest, out ToolDiagnostic? profileDiagnostic))
            {
                return await WriteResultAsync(
                    "failed",
                    ToolOutcome.Passed().Fail(
                        ToolPhase.Usage,
                        ToolFailureCategory.Usage,
                        profileDiagnostic!.RuleId,
                        ToolExitCode.UsageOrManifest),
                    [profileDiagnostic],
                    format,
                    writer,
                    command,
                    manifestPath,
                    manifest,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            bool isExecutable = manifest.Modules.Count > 0
                && manifest.Modules.All(module => module.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                && (manifest.Ui?.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ?? true);

            if (command == ModuleInvocationCommand.Down && isExecutable && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath))
            {
                engine = new CompositionEngine(compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath));
                string manifestHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
                IReadOnlyList<CompositionRunState> matches = await engine.StateStore.FindByManifestHashAsync(manifestHash, cancellationToken).ConfigureAwait(false);
                if ((runId is not null && !CompositionRunPlanFactory.IsRunId(runId))
                    || (runId is null && matches.Count > 1))
                {
                    ToolDiagnostic ambiguous = new(
                        "HXR024",
                        ToolPhase.Usage,
                        ToolFailureCategory.Usage,
                        "A matching run identity is required for this manifest.",
                        "runId",
                        "Supply --run-id from the public run result.");
                    return await WriteResultAsync(
                        "failed",
                        ToolOutcome.Passed().Fail(ambiguous.Phase, ambiguous.Category, ambiguous.RuleId, ToolExitCode.UsageOrManifest),
                        [ambiguous],
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                CompositionRunState? target = runId is null
                    ? matches.SingleOrDefault()
                    : matches.FirstOrDefault(state => string.Equals(state.RunId, runId, StringComparison.Ordinal));
                CompositionDownResult down = target is null
                    ? new CompositionDownResult(
                        new ToolCommandResult(
                            "completed",
                            ToolOutcome.Passed(),
                            [new ToolDiagnostic("HXI001", ToolPhase.Cleanup, ToolFailureCategory.None, "Runner-owned invocation cleanup completed.", "down")]),
                        new CompositionRunResources([], []))
                    : await engine.DownAsync(target.RunId, cancellationToken).ConfigureAwait(false);
                return await WriteResultAsync(
                    down.Result.Status,
                    down.Result.Outcome,
                    down.Result.Diagnostics,
                    format,
                    writer,
                    command,
                    manifestPath,
                    manifest,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken,
                    target?.RunId ?? runId,
                    runId).ConfigureAwait(false);
            }

            if (command == ModuleInvocationCommand.Down)
            {
                if (runId is not null)
                {
                    ToolDiagnostic unsupportedRunId = new(
                        "HXR024",
                        ToolPhase.Usage,
                        ToolFailureCategory.Usage,
                        "A run identity requires an executable manifest.",
                        "runId");
                    return await WriteResultAsync(
                        "failed",
                        ToolOutcome.Passed().Fail(ToolPhase.Usage, ToolFailureCategory.Usage, unsupportedRunId.RuleId, ToolExitCode.UsageOrManifest),
                        [unsupportedRunId],
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                await ModuleInvocationStateStore.DownAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                ToolDiagnostic cleanupDiagnostic = new(
                    "HXI001",
                    ToolPhase.Cleanup,
                    ToolFailureCategory.None,
                    "Runner-owned invocation cleanup completed.",
                    "down");
                return await WriteResultAsync(
                    "completed",
                    ToolOutcome.Passed(),
                    [cleanupDiagnostic],
                    format,
                    writer,
                    command,
                    manifestPath,
                    manifest,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            RuntimePrerequisiteCheck prerequisite = RuntimePrerequisiteGate.Check(manifest);
            if (!prerequisite.IsAvailable)
            {
                return await WriteResultAsync(
                    "unavailable",
                    ToolOutcome.Passed().Fail(
                        ToolPhase.Prerequisite,
                        ToolFailureCategory.PrerequisiteUnavailable,
                        prerequisite.Diagnostic!.RuleId,
                        ToolExitCode.PrerequisiteUnavailable),
                    [prerequisite.Diagnostic],
                    format,
                    writer,
                    command,
                    manifestPath,
                    manifest,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            if (isExecutable && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath))
            {
                ExecutableDescriptorLoadResult descriptorResult = await ExecutableDescriptorLoader.LoadAsync(
                    manifest,
                    manifestPath,
                    descriptorChildEntryAssemblyPath,
                    cancellationToken).ConfigureAwait(false);
                if (!descriptorResult.IsValid)
                {
                    ToolDiagnostic descriptorDiagnostic = descriptorResult.Diagnostics[0];
                    bool unavailable = descriptorDiagnostic.Category == ToolFailureCategory.PrerequisiteUnavailable;
                    return await WriteResultAsync(
                        unavailable ? "unavailable" : "failed",
                        ToolOutcome.Passed().Fail(
                            descriptorDiagnostic.Phase,
                            descriptorDiagnostic.Category,
                            descriptorDiagnostic.RuleId,
                            unavailable ? ToolExitCode.PrerequisiteUnavailable : ToolExitCode.UsageOrManifest),
                        descriptorResult.Diagnostics,
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                PersistedProfileDefinition? persistedProfile = command == ModuleInvocationCommand.Test
                    ? PersistedProfileLoader.TryLoad(manifest, manifestPath, profile, filter)
                    : null;
                CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
                if (persistedProfile is not null)
                {
                    options = options with { EnableSecondEventStoreInstance = true };
                }

                engine = new CompositionEngine(command == ModuleInvocationCommand.Run
                    ? options with { PersistAfterParentExit = true }
                    : options);
                CompositionStartResult start = await engine.StartAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                session = start.Session;
                if (!start.IsReady)
                {
                    CancellationToken resultToken = start.Result.Outcome.ExitCode == ToolExitCode.Cancelled
                        ? CancellationToken.None
                        : cancellationToken;
                    return await WriteResultAsync(
                        start.Result.Status,
                        start.Result.Outcome,
                        start.Result.Diagnostics,
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        resultToken,
                        start.RunId).ConfigureAwait(false);
                }

                if (command == ModuleInvocationCommand.Run)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int exitCode = await WriteResultAsync(
                        start.Result.Status,
                        start.Result.Outcome,
                        start.Result.Diagnostics,
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        cancellationToken,
                        start.RunId).ConfigureAwait(false);
                    if (exitCode != (int)ToolExitCode.Success)
                    {
                        _ = await engine.DownAsync(session!, CancellationToken.None).ConfigureAwait(false);
                        session = null;
                    }
                    else
                    {
                        sessionHandedOff = true;
                    }

                    return exitCode;
                }

                ToolCommandResult? profileResult = persistedProfile is null
                    ? null
                    : await PersistedProfileExecutor.ExecuteAsync(session!, persistedProfile, cancellationToken).ConfigureAwait(false);
                NativeTestExecutionResult? nativeResult = profileResult?.Outcome.ExitCode == ToolExitCode.Success
                    && persistedProfile!.NativeTests is PersistedProfileNativeTests nativeTests
                        ? await NativeTestExecutor.ExecuteAsync(session!, nativeTests, manifestPath, cancellationToken).ConfigureAwait(false)
                        : null;
                CompositionDownResult cleanup = await engine.DownAsync(session!, CancellationToken.None).ConfigureAwait(false);
                session = null;
                if (profileResult is not null)
                {
                    ToolCommandResult finalResult = CombineTestResults(profileResult, nativeResult, cleanup.Result);
                    return await WriteResultAsync(
                        finalResult.Status,
                        finalResult.Outcome,
                        finalResult.Diagnostics,
                        format,
                        writer,
                        command,
                        manifestPath,
                        manifest,
                        profile,
                        filter,
                        evidencePath,
                        startedUtc,
                        cancellationToken,
                        start.RunId,
                        completedPersistedProfile: finalResult.Outcome.ExitCode == ToolExitCode.Success ? persistedProfile : null,
                        nativeTests: nativeResult,
                        nativePlatform: persistedProfile!.NativeTests?.Platform).ConfigureAwait(false);
                }

                ToolDiagnostic unsupported = new(
                    "HXR029",
                    ToolPhase.Prerequisite,
                    ToolFailureCategory.PrerequisiteUnavailable,
                    "The requested profile has no qualified public test executor.",
                    "profile",
                    "Use run and down for Stage 3 composition; qualify profile execution before claiming a test pass.");
                IReadOnlyList<ToolDiagnostic> testDiagnostics = cleanup.Result.Outcome.ExitCode == ToolExitCode.Success
                    ? [unsupported]
                    : [unsupported, .. cleanup.Result.Diagnostics];
                return await WriteResultAsync(
                    "unavailable",
                    ToolOutcome.Passed().Fail(unsupported.Phase, unsupported.Category, unsupported.RuleId, ToolExitCode.PrerequisiteUnavailable),
                    testDiagnostics,
                    format,
                    writer,
                    command,
                    manifestPath,
                    manifest,
                    profile,
                    filter,
                    evidencePath,
                    startedUtc,
                    cancellationToken,
                    start.RunId).ConfigureAwait(false);
            }

            ToolDiagnostic prerequisiteDiagnostic = new(
                "HXR003",
                ToolPhase.Prerequisite,
                ToolFailureCategory.PrerequisiteUnavailable,
                "The approved descriptor ABI cannot yet be composed into a supported runtime.",
                "runtime",
                "Qualify the runner-owned G-6 topology before retrying.");
            return await WriteResultAsync(
                "unavailable",
                ToolOutcome.Passed().Fail(
                    ToolPhase.Prerequisite,
                    ToolFailureCategory.PrerequisiteUnavailable,
                    prerequisiteDiagnostic.RuleId,
                    ToolExitCode.PrerequisiteUnavailable),
                [prerequisiteDiagnostic],
                format,
                writer,
                command,
                manifestPath,
                manifest,
                profile,
                filter,
                evidencePath,
                startedUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (session is not null && engine is not null)
            {
                _ = await engine.DownAsync(session, CancellationToken.None).ConfigureAwait(false);
                session = null;
            }

            ToolDiagnostic cancellationDiagnostic = new(
                "HXC130",
                ToolPhase.Cleanup,
                ToolFailureCategory.Cancelled,
                "The invocation was cancelled.",
                "cancellation");
            return await WriteResultAsync(
                "cancelled",
                ToolOutcome.Passed().Fail(
                    ToolPhase.Cleanup,
                    ToolFailureCategory.Cancelled,
                    cancellationDiagnostic.RuleId,
                    ToolExitCode.Cancelled),
                [cancellationDiagnostic],
                format,
                writer,
                command,
                manifestPath,
                loadedManifest,
                profile,
                filter,
                evidencePath,
                startedUtc,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return await WriteLifecycleFailureAsync(
                command,
                manifestPath,
                profile,
                filter,
                evidencePath,
                format,
                writer,
                startedUtc,
                cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            return await WriteLifecycleFailureAsync(
                command,
                manifestPath,
                profile,
                filter,
                evidencePath,
                format,
                writer,
                startedUtc,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (session is not null && !sessionHandedOff && engine is not null)
            {
                try
                {
                    _ = await engine.DownAsync(session, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // The engine retains run state when bounded cleanup cannot be verified, allowing an exact down retry.
                }
            }
        }
    }

    /// <summary>
    /// Combines a persisted profile result, its optional native test step, and the run teardown into one command result.
    /// </summary>
    /// <param name="profileResult">The persisted profile result.</param>
    /// <param name="nativeResult">The native test step result, when the profile passed and declares native tests.</param>
    /// <param name="cleanupResult">The teardown result.</param>
    /// <returns>The first causal failure with every diagnostic, or the passing step result.</returns>
    internal static ToolCommandResult CombineTestResults(
        ToolCommandResult profileResult,
        NativeTestExecutionResult? nativeResult,
        ToolCommandResult cleanupResult)
    {
        ArgumentNullException.ThrowIfNull(profileResult);
        ArgumentNullException.ThrowIfNull(cleanupResult);

        ToolCommandResult stepResult = nativeResult is null
            ? profileResult
            : new ToolCommandResult(
                nativeResult.Result.Status,
                nativeResult.Result.Outcome,
                [.. profileResult.Diagnostics, .. nativeResult.Result.Diagnostics]);
        ToolOutcome causalOutcome = stepResult.Outcome.ExitCode == ToolExitCode.Success ? cleanupResult.Outcome : stepResult.Outcome;
        return cleanupResult.Outcome.ExitCode == ToolExitCode.Success
            ? stepResult
            : new ToolCommandResult("failed", causalOutcome, [.. stepResult.Diagnostics, .. cleanupResult.Diagnostics]);
    }

    private static async Task<int> WriteResultAsync(
        string status,
        ToolOutcome outcome,
        IReadOnlyList<ToolDiagnostic> diagnostics,
        ToolOutputFormat format,
        TextWriter writer,
        ModuleInvocationCommand command,
        string manifestPath,
        ModuleManifest? manifest,
        string? profile,
        string? filter,
        string? evidencePath,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken,
        string? runId = null,
        string? requestedRunId = null,
        PersistedProfileDefinition? completedPersistedProfile = null,
        NativeTestExecutionResult? nativeTests = null,
        string? nativePlatform = null)
    {
        ToolCommandResult result = new(status, outcome, diagnostics) { RunId = runId };
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            ModuleRunTestCounts? testCounts = null;
            Dictionary<string, string>? artifactHashes = null;
            if (nativeTests is { Report: { } report, ReportBytes: { } reportBytes }
                && nativePlatform is not null
                && evidencePath.EndsWith(".json", StringComparison.Ordinal))
            {
                string reportPath = NativeTestExecutor.RetainedReportPath(evidencePath, nativePlatform);
                ModuleRunEvidenceWriteResult reportResult = await ModuleRunEvidenceWriter.WriteArtifactAsync(
                    reportPath,
                    manifestPath,
                    reportBytes,
                    cancellationToken).ConfigureAwait(false);
                if (reportResult.Succeeded)
                {
                    testCounts = new ModuleRunTestCounts(true, report.Total, report.Passed, report.Failed, report.Skipped);
                    artifactHashes = new Dictionary<string, string>(StringComparer.Ordinal) { [reportPath] = Convert.ToHexString(SHA256.HashData(reportBytes)) };
                }
                else
                {
                    result = MergeEvidenceFailure(result, reportResult.Diagnostic!);
                }
            }

            ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
                command,
                manifestPath,
                manifest,
                profile,
                filter,
                result,
                startedUtc,
                DateTimeOffset.UtcNow,
                runId ?? Guid.NewGuid().ToString("N"),
                requestedRunId,
                completedPersistedProfile is null ? null : PersistedProfileEvidence.Assertions(completedPersistedProfile.Modules.Select(module => module.ModuleId)),
                completedPersistedProfile is null ? null : PersistedProfileEvidence.Sequences(completedPersistedProfile.Modules.Select(module => module.ModuleId)),
                testCounts,
                artifactHashes);
            ModuleRunEvidenceWriteResult evidenceResult = await ModuleRunEvidenceWriter.WriteAsync(
                evidencePath,
                manifestPath,
                evidence,
                cancellationToken).ConfigureAwait(false);
            if (!evidenceResult.Succeeded)
            {
                result = MergeEvidenceFailure(result, evidenceResult.Diagnostic!);
            }
        }

        await ToolDiagnosticFormatter.WriteAsync(
            writer,
            result,
            format,
            cancellationToken).ConfigureAwait(false);
        return (int)result.Outcome.ExitCode;
    }

    private static ToolCommandResult MergeEvidenceFailure(ToolCommandResult result, ToolDiagnostic diagnostic)
    {
        if (result.Outcome.ExitCode == ToolExitCode.Success)
        {
            ToolOutcome evidenceOutcome = ToolOutcome.Passed().Fail(
                ToolPhase.Evidence,
                ToolFailureCategory.EvidencePolicy,
                diagnostic.RuleId,
                ToolExitCode.EvidenceSchemaOrPolicy);
            return new ToolCommandResult("failed", evidenceOutcome, [diagnostic]);
        }

        return new ToolCommandResult(result.Status, result.Outcome, [.. result.Diagnostics, diagnostic]);
    }

    private static async Task<int> WriteLifecycleFailureAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        string? evidencePath,
        ToolOutputFormat format,
        TextWriter writer,
        DateTimeOffset startedUtc,
        CancellationToken cancellationToken)
    {
        ToolDiagnostic lifecycleDiagnostic = new(
            "HXR004",
            ToolPhase.Topology,
            ToolFailureCategory.TopologyOrLifecycle,
            "The runner-owned lifecycle state could not be safely managed.",
            "runtime",
            "Resolve runner-owned state access before retrying.");
        return await WriteResultAsync(
            "failed",
            ToolOutcome.Passed().Fail(
                ToolPhase.Topology,
                ToolFailureCategory.TopologyOrLifecycle,
                lifecycleDiagnostic.RuleId,
                ToolExitCode.TopologyOrLifecycle),
            [lifecycleDiagnostic],
            format,
            writer,
            command,
            manifestPath,
            null,
            profile,
            filter,
            evidencePath,
            startedUtc,
            cancellationToken).ConfigureAwait(false);
    }

    private static bool ValidateProfile(
        ModuleInvocationCommand command,
        string? profile,
        ModuleManifest manifest,
        out ToolDiagnostic? diagnostic)
    {
        if (!string.IsNullOrWhiteSpace(profile)
            && (ManifestPathValidator.ContainsPlaceholder(profile) || ManifestSecretDetector.ContainsSecret(profile)))
        {
            diagnostic = new ToolDiagnostic(
                "HXC003",
                ToolPhase.Usage,
                ToolFailureCategory.Usage,
                "The requested qualification profile contains prohibited material.",
                "profile",
                "Use a declared metadata-only profile name.");
            return false;
        }

        if (command != ModuleInvocationCommand.Test && string.IsNullOrWhiteSpace(profile))
        {
            diagnostic = null;
            return true;
        }

        if (string.IsNullOrWhiteSpace(profile) || !manifest.Profiles.ContainsKey(profile))
        {
            diagnostic = new ToolDiagnostic(
                "HXC002",
                ToolPhase.Usage,
                ToolFailureCategory.Usage,
                "The requested qualification profile is not declared by the manifest.",
                "profile",
                "Use a profile declared in the validated manifest.");
            return false;
        }

        diagnostic = null;
        return true;
    }
}
