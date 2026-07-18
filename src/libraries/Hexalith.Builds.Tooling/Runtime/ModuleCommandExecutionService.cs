// <copyright file="ModuleCommandExecutionService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text;

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
    /// <returns>The stable process exit code.</returns>
    public static async Task<int> ExecuteAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        string? evidencePath,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(writer);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;

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

            if (command == ModuleInvocationCommand.Down)
            {
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

            ModuleRuntimePlan runtimePlan = ModuleRuntimePlan.Create(manifest, profile);
            string? filterHash = string.IsNullOrWhiteSpace(filter)
                ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(filter)));
            _ = await ModuleInvocationStateStore.CreateAsync(
                command,
                manifestPath,
                profile,
                filterHash,
                runtimePlan,
                cancellationToken).ConfigureAwait(false);
            ToolDiagnostic prerequisiteDiagnostic = new(
                "HXR003",
                ToolPhase.Prerequisite,
                ToolFailureCategory.PrerequisiteUnavailable,
                "No accepted descriptor ABI is available to compose the requested runtime.",
                "runtime",
                "Provide an owner-approved Builds descriptor ABI before retrying.");
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
                null,
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
        CancellationToken cancellationToken)
    {
        ToolCommandResult result = new(status, outcome, diagnostics);
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
                command,
                manifestPath,
                manifest,
                profile,
                filter,
                result,
                startedUtc,
                DateTimeOffset.UtcNow,
                Guid.NewGuid().ToString("N"));
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