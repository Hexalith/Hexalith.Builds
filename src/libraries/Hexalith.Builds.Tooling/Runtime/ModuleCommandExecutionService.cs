// <copyright file="ModuleCommandExecutionService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Executes validated module runner commands while preserving the first causal outcome.
/// </summary>
public static class ModuleCommandExecutionService
{
    private static readonly AsyncLocal<IReadOnlyList<ToolDiagnostic>?> _identityWarnings = new();

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
    public static Task<int> ExecuteAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        ToolOutputFormat format,
        TextWriter writer,
        CancellationToken cancellationToken)
        => ExecuteAsync(
            command,
            manifestPath,
            profile,
            filter,
            null,
            format,
            writer,
            cancellationToken);

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
    /// <param name="mode">The tool-selected build mode.</param>
    /// <param name="profiles">Optional ordered batch profile names from the same manifest.</param>
    /// <param name="candidateRequestPath">Optional GitHub-authenticated candidate request envelope.</param>
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
        CompositionEngineOptions? compositionOptions = null,
        WorkspaceMode mode = WorkspaceMode.Source,
        IReadOnlyList<string>? profiles = null,
        string? candidateRequestPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(writer);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        using ModuleTestExecutionBudget? testBudget = command == ModuleInvocationCommand.Test ? new ModuleTestExecutionBudget(cancellationToken) : null;
        using CompositionStartupRequest? startupRequest = PersistsAfterExit(command) || command == ModuleInvocationCommand.Test
            ? new CompositionStartupRequest(startedUtc, compositionOptions?.ReadinessTimeout ?? TimeSpan.FromSeconds(SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds), cancellationToken)
            : null;
        if (startupRequest is not null && (compositionOptions is not null || descriptorChildEntryAssemblyPath is not null))
        {
            compositionOptions = (compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath!)) with { StartupRequestedAtUtc = startedUtc };
        }

        CancellationToken setupToken = startupRequest?.Token ?? testBudget?.Token ?? cancellationToken;
        if (command == ModuleInvocationCommand.Debug)
        {
            mode = WorkspaceMode.Source;
        }

        CompositionEngine? engine = null;
        CompositionRunSession? session = null;
        bool sessionHandedOff = false;
        ModuleManifest? loadedManifest = null;
        IReadOnlyList<ToolDiagnostic>? previousIdentityWarnings = _identityWarnings.Value;
        _identityWarnings.Value = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidateRequestPath is not null && (command != ModuleInvocationCommand.Test || !PlatformManifestCommand.IsV2(manifestPath)))
            {
                ToolCommandResult refusal = PlatformRefusal("HXP301", "Candidate policy refused: declared-candidate-suite-required", ToolExitCode.UsageOrManifest);
                return await WriteResultAsync(refusal.Status, refusal.Outcome, refusal.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None).ConfigureAwait(false);
            }

            if (command == ModuleInvocationCommand.Down && runId is not null)
            {
                return await ExecuteExactDownAsync(manifestPath, runId, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, compositionOptions, cancellationToken).ConfigureAwait(false);
            }

            // A direct reference may contain a stale or absent working-tree manifest.
            // Resolve its recorded gitlink before parsing any of its bytes.
            SourceMapping? preResolvedMapping = null;
            if (command != ModuleInvocationCommand.Down && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath))
            {
                bool rootManifest = await (compositionOptions?.RootManifestCheckOverride?.Invoke(manifestPath, setupToken) ?? WorkspaceRootResolver.IsRootManifestAsync(manifestPath, setupToken)).ConfigureAwait(false);
                if (rootManifest && startupRequest is not null)
                {
                    await startupRequest.ConfigureAsync(manifestPath, mode, compositionOptions?.SourceMapping).ConfigureAwait(false);
                }

                if (!rootManifest)
                {
                    preResolvedMapping = await (compositionOptions?.MappingResolverOverride?.Invoke(manifestPath, mode, setupToken) ?? WorkspaceRootResolver.ResolveAsync(manifestPath, mode, setupToken)).ConfigureAwait(false);
                    _identityWarnings.Value = PlatformIdentityResolver.Diagnostics(preResolvedMapping.IdentityFindings, warning: true);
                    if (startupRequest is not null)
                    {
                        await startupRequest.ConfigureAsync(manifestPath, mode, preResolvedMapping).ConfigureAwait(false);
                    }
                }
            }

            if (profiles is { Count: > 1 } && (command != ModuleInvocationCommand.Test || !PlatformManifestCommand.IsV2(manifestPath)))
            {
                ToolCommandResult incompatible = PlatformRefusal("HXC002", "Repeated profiles require compatible declared suites from one v2 manifest.", ToolExitCode.UsageOrManifest);
                return await WriteResultAsync(incompatible.Status, incompatible.Outcome, incompatible.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None).ConfigureAwait(false);
            }

            if (PlatformManifestCommand.IsV2(manifestPath))
            {
                return await ExecutePlatformAsync(command, manifestPath, profile, filter, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, runId, compositionOptions, mode, preResolvedMapping, testBudget, startupRequest?.Deadline, startupRequest?.Token, profiles, candidateRequestPath, cancellationToken).ConfigureAwait(false);
            }

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
            bool isExecutable = manifest.Modules.Count > 0
                && manifest.Modules.All(module => module.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                && (manifest.Ui?.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ?? true);
            bool nonExecutableAtRoot = !isExecutable && command != ModuleInvocationCommand.Down
                && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath)
                && await WorkspaceRootResolver.IsRootManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            bool requiresMapping = command != ModuleInvocationCommand.Down
                && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath)
                && !nonExecutableAtRoot;
            if ((!requiresMapping || preResolvedMapping is null) && !ValidateProfile(command, profile, manifest, out ToolDiagnostic? profileDiagnostic))
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

            RuntimePrerequisiteCheck? prerequisite = preResolvedMapping is null ? RuntimePrerequisiteGate.Check(manifest) : null;
            if (prerequisite?.IsAvailable == false)
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

            ToolDiagnostic? preResolutionAspire = null;
            if (requiresMapping && preResolvedMapping is null && descriptorChildEntryAssemblyPath is not null)
            {
                CompositionEngineOptions earlyOptions = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
                preResolutionAspire = await CompositionPrerequisiteProbe.ProbeAspireAsync(
                    earlyOptions.AspireCommand, earlyOptions.AspireProbeTimeout, setupToken).ConfigureAwait(false);
                if (preResolutionAspire is not null)
                {
                    return await WriteResultAsync(
                        "unavailable",
                        ToolOutcome.Passed().Fail(ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, preResolutionAspire.RuleId, ToolExitCode.PrerequisiteUnavailable),
                        [preResolutionAspire],
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
            }

            if (requiresMapping && descriptorChildEntryAssemblyPath is not null)
            {
                CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
                loadedManifest = null;
                SourceMapping mapping = preResolvedMapping
                    ?? options.SourceMapping
                    ?? await (options.MappingResolverOverride?.Invoke(manifestPath, mode, setupToken) ?? WorkspaceRootResolver.ResolveAsync(manifestPath, mode, setupToken)).ConfigureAwait(false);
                _identityWarnings.Value = PlatformIdentityResolver.Diagnostics(mapping.IdentityFindings, warning: true);
                options = options with { SourceMapping = mapping };

                // Initialization can supply a missing direct-reference manifest, and the root
                // manifest may change while initialization is in progress. Use current bytes.
                ManifestLoadResult currentManifest = ModuleManifestLoader.Load(manifestPath);
                if (!currentManifest.IsValid)
                {
                    return await WriteResultAsync(
                        "failed",
                        ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, currentManifest.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest),
                        currentManifest.Diagnostics,
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

                manifest = currentManifest.Manifest!;
                loadedManifest = manifest;
                isExecutable = manifest.Modules.Count > 0
                    && manifest.Modules.All(module => module.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    && (manifest.Ui?.DescriptorAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ?? true);
                if (!ValidateProfile(command, profile, manifest, out ToolDiagnostic? currentProfileDiagnostic))
                {
                    return await WriteResultAsync(
                        "failed",
                        ToolOutcome.Passed().Fail(ToolPhase.Usage, ToolFailureCategory.Usage, currentProfileDiagnostic!.RuleId, ToolExitCode.UsageOrManifest),
                        [currentProfileDiagnostic],
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

                prerequisite = RuntimePrerequisiteGate.Check(manifest);
                if (!prerequisite.IsAvailable)
                {
                    return await WriteResultAsync(
                        "unavailable",
                        ToolOutcome.Passed().Fail(ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, prerequisite.Diagnostic!.RuleId, ToolExitCode.PrerequisiteUnavailable),
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

                if (!isExecutable)
                {
                    ToolDiagnostic changedShape = NonexecutableTopologyDiagnostic();
                    return await WriteResultAsync(
                        "unavailable",
                        ToolOutcome.Passed().Fail(changedShape.Phase, changedShape.Category, changedShape.RuleId, ToolExitCode.PrerequisiteUnavailable),
                        [changedShape],
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

                ToolDiagnostic? aspire = preResolvedMapping is null ? preResolutionAspire : await CompositionPrerequisiteProbe.ProbeAspireAsync(options.AspireCommand, options.AspireProbeTimeout, setupToken).ConfigureAwait(false);
                if (aspire is not null)
                {
                    return await WriteResultAsync(
                        "unavailable",
                        ToolOutcome.Passed().Fail(ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, aspire.RuleId, ToolExitCode.PrerequisiteUnavailable),
                        [aspire],
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

                // A rejected pinned catalog must not run descriptor child code.
                IReadOnlyList<ToolDiagnostic>? catalogRefusal = await PreflightPinnedCatalogAsync(mapping, options.WorkspaceRoot, setupToken).ConfigureAwait(false);
                if (catalogRefusal is not null)
                {
                    return await WriteResultAsync(
                        "unavailable",
                        ToolOutcome.Passed().Fail(catalogRefusal[0].Phase, catalogRefusal[0].Category, catalogRefusal[0].RuleId, ToolExitCode.PrerequisiteUnavailable),
                        catalogRefusal,
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

                ExecutableDescriptorLoadResult descriptorResult = await ExecutableDescriptorLoader.LoadAsync(
                    manifest,
                    manifestPath,
                    descriptorChildEntryAssemblyPath,
                    setupToken,
                    mapping).ConfigureAwait(false);
                if (!descriptorResult.IsValid)
                {
                    ToolDiagnostic descriptorDiagnostic = descriptorResult.Diagnostics[0];
                    bool unavailable = descriptorDiagnostic.Category == ToolFailureCategory.PrerequisiteUnavailable;
                    ToolExitCode descriptorExit = ToolExitCode.UsageOrManifest;
                    if (unavailable)
                    {
                        descriptorExit = ToolExitCode.PrerequisiteUnavailable;
                    }
                    else if (descriptorDiagnostic.Category == ToolFailureCategory.TopologyOrLifecycle)
                    {
                        descriptorExit = ToolExitCode.TopologyOrLifecycle;
                    }

                    return await WriteResultAsync(
                        unavailable ? "unavailable" : "failed",
                        ToolOutcome.Passed().Fail(
                            descriptorDiagnostic.Phase,
                            descriptorDiagnostic.Category,
                            descriptorDiagnostic.RuleId,
                            descriptorExit),
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

                ToolDiagnostic? primaryNativePathDiagnostic = null;
                PersistedProfileDefinition? persistedProfile = command == ModuleInvocationCommand.Test
                    ? PersistedProfileLoader.TryLoad(manifest, manifestPath, profile, filter, out primaryNativePathDiagnostic)
                    : null;
                if (primaryNativePathDiagnostic is not null)
                {
                    return await WriteResultAsync(
                        "failed",
                        ToolOutcome.Passed().Fail(primaryNativePathDiagnostic.Phase, primaryNativePathDiagnostic.Category, primaryNativePathDiagnostic.RuleId, ToolExitCode.UsageOrManifest),
                        [primaryNativePathDiagnostic],
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

                if (persistedProfile?.NativeTests is { } primaryTests)
                {
                    string nativeRoot = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
                    List<ToolDiagnostic> nativePathDiagnostics = [];
                    string? nativeProject = ManifestPathValidator.ValidateExistingFile(primaryTests.Project, nativeRoot, "profile.nativeTests.project", nativePathDiagnostics);
                    if (nativeProject is null && nativePathDiagnostics.Count > 0)
                    {
                        ToolDiagnostic invalidNativeProject = nativePathDiagnostics[0];
                        return await WriteResultAsync(
                            "failed",
                            ToolOutcome.Passed().Fail(invalidNativeProject.Phase, invalidNativeProject.Category, invalidNativeProject.RuleId, ToolExitCode.UsageOrManifest),
                            nativePathDiagnostics,
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

                    if (nativeProject is not null && !mapping.ContainsProject(nativeProject))
                    {
                        ToolDiagnostic unmapped = new(
                            "HXW006",
                            ToolPhase.Topology,
                            ToolFailureCategory.TopologyOrLifecycle,
                            $"Native test project '{nativeProject}' is outside the active module and mapped source roots.",
                            nativeProject,
                            "Use a test project in the active module or a mapped source root.");
                        return await WriteResultAsync(
                            "failed",
                            ToolOutcome.Passed().Fail(unmapped.Phase, unmapped.Category, unmapped.RuleId, ToolExitCode.TopologyOrLifecycle),
                            [unmapped],
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
                }

                if (persistedProfile is not null)
                {
                    options = options with { EnableSecondEventStoreInstance = true };
                }

                engine = new CompositionEngine(PersistsAfterExit(command)
                    ? options with { PersistAfterParentExit = true, DebugAttach = command == ModuleInvocationCommand.Debug }
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

                if (PersistsAfterExit(command))
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
                    : await (options.ProfileExecutorOverride?.Invoke(session!, persistedProfile, cancellationToken)
                        ?? PersistedProfileExecutor.ExecuteAsync(session!, persistedProfile, cancellationToken)).ConfigureAwait(false);
                NativeTestExecutionResult? nativeResult = profileResult?.Outcome.ExitCode == ToolExitCode.Success && persistedProfile!.NativeTests is PersistedProfileNativeTests nativeTests ? await NativeTestExecutor.ExecuteAsync(session!, nativeTests, manifestPath, cancellationToken, options.NativeDotnetCommand ?? "dotnet").ConfigureAwait(false) : null;
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

            ToolDiagnostic prerequisiteDiagnostic = NonexecutableTopologyDiagnostic();
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && startupRequest?.Token.IsCancellationRequested == true)
        {
            CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath!);
            CompositionStartResult expired = await new CompositionEngine(options).ReportSetupTimeoutAsync(startedUtc, startupRequest.Deadline).ConfigureAwait(false);
            return await WriteResultAsync(expired.Result.Status, expired.Result.Outcome, expired.Result.Diagnostics, format, writer, command, manifestPath, loadedManifest, profile, filter, evidencePath, startedUtc, CancellationToken.None, expired.RunId).ConfigureAwait(false);
        }
        catch (WorkspaceMappingException exception)
        {
            ToolDiagnostic diagnostic = exception.Diagnostic;
            ToolExitCode exitCode = diagnostic.Category == ToolFailureCategory.PrerequisiteUnavailable
                ? ToolExitCode.PrerequisiteUnavailable
                : ToolExitCode.TopologyOrLifecycle;
            return await WriteResultAsync(
                diagnostic.Category == ToolFailureCategory.PrerequisiteUnavailable ? "unavailable" : "failed",
                ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, exitCode),
                exception.Diagnostics,
                format,
                writer,
                command,
                manifestPath,
                loadedManifest,
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
            _identityWarnings.Value = previousIdentityWarnings;
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
        string? nativePlatform = null,
        ModuleTestRunRecord? selectedTestRun = null,
        CompositionRunResources? remainingResources = null,
        string? testResultPath = null)
    {
        IReadOnlyList<ToolDiagnostic> warnings = _identityWarnings.Value ?? [];
        ToolCommandResult result = new(status, outcome, [.. warnings, .. diagnostics]) { RunId = runId, RemainingResources = remainingResources, TestResultPath = testResultPath };
        if (!string.IsNullOrWhiteSpace(evidencePath))
        {
            ModuleRunTestCounts? testCounts = null;
            Dictionary<string, string>? artifactHashes = null;
            if (nativeTests is { ReportBytes: { } reportBytes }
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
                    if (nativeTests.Report is { } report)
                    {
                        testCounts = new ModuleRunTestCounts(true, report.Total, report.Passed, report.Failed, report.Skipped);
                    }

                    artifactHashes = new Dictionary<string, string>(StringComparer.Ordinal) { [reportPath] = Convert.ToHexString(SHA256.HashData(reportBytes)) };
                }
                else
                {
                    result = MergeEvidenceFailure(result, reportResult.Diagnostic!);
                }
            }

            if (nativeTests?.ObservationBytes is { } observationBytes && evidencePath.EndsWith(".json", StringComparison.Ordinal))
            {
                string detailPath = evidencePath[..^".json".Length] + ".functional-observations.json";
                ModuleRunEvidenceWriteResult detailResult = await ModuleRunEvidenceWriter.WriteArtifactAsync(detailPath, manifestPath, observationBytes, cancellationToken).ConfigureAwait(false);
                if (detailResult.Succeeded)
                {
                    artifactHashes ??= new Dictionary<string, string>(StringComparer.Ordinal);
                    artifactHashes[detailPath] = Convert.ToHexString(SHA256.HashData(observationBytes));
                }
                else
                {
                    result = MergeEvidenceFailure(result, detailResult.Diagnostic!);
                }
            }

            if (selectedTestRun is not null)
            {
                result = result with { Status = selectedTestRun.TerminalStatus, Outcome = selectedTestRun.TerminalOutcome };
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
                artifactHashes) with
            {
                SelectedTestRun = selectedTestRun,
            };
            if (selectedTestRun is not null)
            {
                evidence = evidence with { Invocation = evidence.Invocation with { Command = evidence.Invocation.Command + " --profile " + selectedTestRun.Selection.Name, Profile = selectedTestRun.Selection.Name, ManifestHash = selectedTestRun.Selection.ManifestHash } };
            }

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

        if (selectedTestRun is not null)
        {
            result = result with { Status = selectedTestRun.TerminalStatus, Outcome = selectedTestRun.TerminalOutcome };
        }

        await ToolDiagnosticFormatter.WriteAsync(
            writer,
            result,
            format,
            cancellationToken).ConfigureAwait(false);
        return (int)result.Outcome.ExitCode;
    }

    private static async Task<int> ExecuteExactDownAsync(string manifestPath, string runId, string? evidencePath, DateTimeOffset startedUtc, ToolOutputFormat format, TextWriter writer, string? runnerAssembly, CompositionEngineOptions? options, CancellationToken cancellationToken)
    {
        ToolCommandResult result;
        if (!CompositionRunPlanFactory.IsRunId(runId))
        {
            result = PlatformRefusal("HXR024", "The run identity is malformed.", ToolExitCode.UsageOrManifest);
        }
        else
        {
            options ??= CompositionCommandOptions.Create(runnerAssembly ?? typeof(ModuleCommandExecutionService).Assembly.Location);
            CompositionEngine engine = new(options);
            CompositionRunState? state = await engine.StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
            string expectedWorkspace = Path.Combine(Path.GetFullPath(options.WorkspaceRoot), runId);
            result = state is not null && (state.Schema != CompositionRunState.SupportedSchema || state.RunId != runId || (state.Workspace is not null && state.Workspace != expectedWorkspace))
                ? PlatformRefusal("HXR024", "The recorded run does not match the requested owned identity.", ToolExitCode.UsageOrManifest)
                : (await engine.DownAsync(runId, cancellationToken).ConfigureAwait(false)).Result with { RunId = runId };
        }

        return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, ModuleInvocationCommand.Down, manifestPath, null, null, null, evidencePath, startedUtc, CancellationToken.None, runId, runId, remainingResources: result.RemainingResources, testResultPath: result.TestResultPath).ConfigureAwait(false);
    }

    private static async Task<int> ExecutePlatformAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        string? evidencePath,
        DateTimeOffset startedUtc,
        ToolOutputFormat format,
        TextWriter writer,
        string? descriptorChildEntryAssemblyPath,
        string? runId,
        CompositionEngineOptions? compositionOptions,
        WorkspaceMode mode,
        SourceMapping? preResolvedMapping,
        ModuleTestExecutionBudget? testBudget,
        CompositionEffectiveDeadline? startupDeadline,
        CancellationToken? startupToken,
        IReadOnlyList<string>? profiles,
        string? candidateRequestPath,
        CancellationToken cancellationToken)
    {
        if (command != ModuleInvocationCommand.Test)
        {
            return await ExecutePlatformLifecycleAsync(command, manifestPath, profile, filter, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, runId, compositionOptions, mode, preResolvedMapping, startupToken ?? cancellationToken, cancellationToken).ConfigureAwait(false);
        }

        PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
        ModuleTestSelection? selected = null;
        IReadOnlyList<string> requestedProfiles = profiles ?? [];
        if (requestedProfiles.Count == 0 && profile is not null)
        {
            requestedProfiles = [profile];
        }

        string? selectionRefusal = null;
        IReadOnlyList<ModuleTestSelection> selections = validation.IsValid ? ModuleTestSelection.SelectBatch(validation.Declarations!, requestedProfiles, manifestPath, startedUtc, out selectionRefusal) : [];
        if (selections.Count > 1 && !string.IsNullOrWhiteSpace(evidencePath))
        {
            ToolCommandResult unsupported = PlatformRefusal("HXC002", "Legacy module-run evidence export supports one declared suite only. Run the batch without --evidence; inspected entry reports remain in owned diagnostics.", ToolExitCode.UsageOrManifest);
            return await WriteResultAsync(unsupported.Status, unsupported.Outcome, unsupported.Diagnostics, format, writer, command, manifestPath, null, profile, filter, null, startedUtc, CancellationToken.None).ConfigureAwait(false);
        }

        ToolCommandResult result;
        NativeTestExecutionResult? native = null;
        ModuleTestRunRecord? record = null;
        string? durableResultPath = null;
        if (!validation.IsValid)
        {
            result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest), validation.Diagnostics);
        }
        else if (filter is not null || runId is not null
                || (compositionOptions?.SourceMapping is { } suppliedMapping && suppliedMapping.Mode != mode)
                || (selected = selections.Count == 0 ? null : selections[0]) is null
                || selected.Mode != (mode == WorkspaceMode.Source ? "source" : "package"))
        {
            string reason = selectionRefusal ?? "The selected suite mode does not match the requested execution mode.";
            if (filter is not null)
            {
                reason = "Declared suites do not support filters.";
            }
            else if (runId is not null)
            {
                reason = "Declared suites require a fresh environment; attachment is unsupported.";
            }
            else if (compositionOptions?.SourceMapping is { } supplied && supplied.Mode != mode)
            {
                reason = "The supplied source mapping does not match the requested execution mode.";
            }

            result = PlatformRefusal("HXC002", reason, ToolExitCode.UsageOrManifest);
        }
        else if (descriptorChildEntryAssemblyPath is null)
        {
            result = PlatformRefusal("HXR014", "Platform composition requires the packaged Builds AppHost.", ToolExitCode.PrerequisiteUnavailable);
        }
        else
        {
            CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
            ArgumentNullException.ThrowIfNull(testBudget);
            CompositionEngine? engine = null;
            bool ci = options.ContinuousIntegrationOverride ?? (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase));
            bool retain = selected is not null && !ci;
            options = options with
            {
                SelectedTest = selected,
                SelectedTests = selections,
                StartupRequestedAtUtc = startedUtc,
                SelectedExecutionOwnerToken = cancellationToken,
                RetainOnFailure = retain,
                PersistAfterParentExit = PersistsAfterExit(command) || retain,
                DebugAttach = false,
                StopTimeout = selected is null ? options.StopTimeout : TimeSpan.FromSeconds(selected.StopTimeoutSeconds),
            };
            CompositionRunSession? session = null;
            string? ownedRunId = null;
            long? startupStoppingTimestamp = null;
            string? fallbackTerminalCause = null;
            try
            {
                (startupToken ?? cancellationToken).ThrowIfCancellationRequested();
                SourceMapping mapping = preResolvedMapping ?? options.SourceMapping ?? await (options.MappingResolverOverride?.Invoke(manifestPath, mode, startupToken ?? cancellationToken) ?? WorkspaceRootResolver.ResolveAsync(manifestPath, mode, startupToken ?? cancellationToken)).ConfigureAwait(false);
                (startupToken ?? cancellationToken).ThrowIfCancellationRequested();
                options = options with { SourceMapping = mapping };
                if (candidateRequestPath is not null)
                {
                    try
                    {
                        CandidatePolicy.CandidateGateClient gate = options.CandidateGateClientOverride ?? CandidatePolicy.CandidateGateClient.FromHost();
                        CandidatePolicy.CandidatePolicyBinding candidate = await gate.ResolveAsync(candidateRequestPath, startupToken ?? cancellationToken).ConfigureAwait(false);
                        System.Text.Json.JsonElement context;
                        try
                        {
                            context = System.Text.Json.JsonSerializer.SerializeToElement(TestEnvironment.TestEnvironmentDescriptorWriter.ExecutionContext());
                        }
                        catch (InvalidDataException)
                        {
                            throw new CandidatePolicy.CandidatePolicyException("execution-context-unavailable");
                        }

                        CandidatePolicy.CandidatePolicyValidator.ValidateSelection(candidate, selected!.ModuleId, selected.Mode, context);
                        await gate.VerifyExecutionAsync(candidate, context, startupToken ?? cancellationToken).ConfigureAwait(false);
                        CompositionProcessResult head = await GitWorkspaceProcess.RunAsync(mapping.Root, startupToken ?? cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
                        CompositionProcessResult dirty = await GitWorkspaceProcess.RunAsync(mapping.Root, startupToken ?? cancellationToken, "status", "--porcelain", "--untracked-files=normal").ConfigureAwait(false);
                        if (head.ExitCode != 0 || head.Output.Trim() != candidate.Candidate.GetProperty("committedRevision").GetString() || dirty.ExitCode != 0 || dirty.Output.Length != 0)
                        {
                            throw new CandidatePolicy.CandidatePolicyException("clean-exact-candidate-checkout-required");
                        }

                        options = options with { CandidatePolicy = candidate };
                    }
                    catch (CandidatePolicy.CandidatePolicyException refusal)
                    {
                        result = PlatformRefusal("HXP301", refusal.Message, ToolExitCode.UsageOrManifest);
                        return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None).ConfigureAwait(false);
                    }
                }

                engine = new CompositionEngine(options);
                try
                {
                    TestEnvironment.TestEnvironmentDescriptorWriter.ValidatePreProvisioning(startupDeadline is { IsOverride: true } ? startupDeadline : CompositionStartupDeadlineResolver.Resolve(validation.Declarations, options.ReadinessTimeout));
                }
                catch (InvalidDataException invalid)
                {
                    result = PlatformRefusal("HXC002", invalid.Message, ToolExitCode.UsageOrManifest);
                    return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None).ConfigureAwait(false);
                }

                CompositionStartResult started = await (options.SelectedPlatformStartOverride?.Invoke(manifestPath, selected!, startupToken ?? cancellationToken) ?? options.PlatformStartOverride?.Invoke(manifestPath, startupToken ?? cancellationToken) ?? engine.StartAsync(manifestPath, startupToken ?? cancellationToken)).ConfigureAwait(false);
                session = started.Session;
                startupStoppingTimestamp = started.StoppingTimestamp;
                ownedRunId = session?.RunId ?? started.RunId ?? (engine.CandidateStateIssued ? options.CandidatePolicy?.RunId : null);
                bool ownerAvailable = true;
                if (session is not null)
                {
                    CompositionRunState issued = CompositionRunPlanFactory.CreateInitialState(session.RunId, session.Plan.Workspace, selected!.ManifestHash, startedUtc) with
                    {
                        AttemptId = session.Plan.AttemptId,
                        CandidatePolicy = session.Plan.CandidatePolicy,
                        EnvironmentId = session.Plan.EnvironmentId,
                        SelectedTest = selected,
                        RetainOnFailure = retain,
                        ExecutionContext = System.Text.Json.JsonSerializer.SerializeToElement(TestEnvironment.TestEnvironmentDescriptorWriter.ExecutionContext()),
                    };
                    using CancellationTokenSource recording = new(TimeSpan.FromSeconds(5));
                    ownerAvailable = await engine.EnsureIssuedStateAsync(issued, recording.Token).ConfigureAwait(false);
                }

                result = ownerAvailable
                    ? started.Result with { RunId = ownedRunId }
                    : PlatformRefusal("HXC130", "Exact cleanup already claimed this owner.", ToolExitCode.Cancelled) with { Status = "cancelled", RunId = ownedRunId };
                if (selected is not null && result.Outcome.ExitCode == ToolExitCode.Success)
                {
                    if (session is null)
                    {
                        result = PlatformRefusal("HXT009", "The selected test requires a ready owned session.", ToolExitCode.ProductOrTest);
                    }
                    else
                    {
                        testBudget.Activate(checked((int)ModuleTestSelection.CombinedBudgetSeconds(selections)));
                        List<ToolDiagnostic> batchDiagnostics = [.. started.Result.Diagnostics];
                        List<string> attemptedEntries = [];
                        string? failedEntry = null;
                        foreach (ModuleTestSelection entry in selections)
                        {
                            selected = entry;
                            attemptedEntries.Add(entry.Name);
                            native = null;
                            options = options with { SelectedTest = entry, StopTimeout = TimeSpan.FromSeconds(entry.StopTimeoutSeconds) };
                            engine = new CompositionEngine(options);
                            session.Plan = session.Plan with { SelectedTest = entry, SelectedTests = selections, ExecutionTiming = null };
                            session.Readiness = session.Readiness with { SelectedTest = entry };
                            session.StoppingTimestamp = null;
                            session.FirstObservedOutcome = null;
                            native = await NativeTestExecutor.ExecuteAsync(
                                session,
                                new PersistedProfileNativeTests(entry.Entry, entry.Executor),
                                manifestPath,
                                testBudget.Token,
                                options.NativeDotnetCommand ?? "dotnet",
                                entry,
                                Path.Combine(options.StateDirectory, "diagnostics", session.RunId),
                                async (observed, token) =>
                            {
                                if (entry == selections[^1] || observed.Result.Outcome.ExitCode != ToolExitCode.Success)
                                {
                                    string disposition = retain && observed.Result.Outcome.ExitCode is not (ToolExitCode.Success or ToolExitCode.Cancelled) ? "retained" : "unknown";
                                    string? cause = observed.TerminalCause;
                                    record = await engine.RecordTestOutcomeAsync(session.RunId, session.Readiness.Resources, observed.Result, disposition, new CompositionRunResources([], [], Unverified: true), token, observed, executionTiming: session.Plan.ExecutionTiming, environmentBindings: session.EnvironmentBindings, terminalCause: cause, testObservation: session.Readiness.TestObservation).ConfigureAwait(false);
                                }
                            },
                                ownerCancellationToken: cancellationToken).ConfigureAwait(false);
                            batchDiagnostics.AddRange(native.Result.Diagnostics);
                            result = native.Result with { RunId = ownedRunId, Diagnostics = [.. batchDiagnostics] };
                            if (result.Outcome.ExitCode != ToolExitCode.Success)
                            {
                                failedEntry = entry.Name;
                                break;
                            }
                        }

                        if (selections.Count > 1)
                        {
                            string notAttempted = string.Join(", ", selections.Skip(attemptedEntries.Count).Select(entry => entry.Name));
                            ToolDiagnostic entries = new("HXI002", ToolPhase.Test, ToolFailureCategory.None, $"Batch entries attempted: {string.Join(", ", attemptedEntries)}; failed: {failedEntry ?? "none"}; not attempted: {(notAttempted.Length == 0 ? "none" : notAttempted)}.", "profiles");
                            result = result with { Diagnostics = [.. result.Diagnostics, entries] };
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                bool ownerCancelled = cancellationToken.IsCancellationRequested;
                if (!ownerCancelled && startupToken?.IsCancellationRequested == true && session?.Plan.ExecutionTiming is null)
                {
                    engine ??= new CompositionEngine(options);
                    CompositionStartResult expired = await engine.ReportSetupTimeoutAsync(startedUtc, startupDeadline ?? CompositionStartupDeadlineResolver.Resolve(validation.Declarations, options.ReadinessTimeout)).ConfigureAwait(false);
                    result = expired.Result;
                    ownedRunId = expired.RunId;
                    startupStoppingTimestamp = expired.StoppingTimestamp;
                }
                else
                {
                    fallbackTerminalCause = ownerCancelled ? "run-cancelled" : "execution-timeout";
                    result = ownerCancelled
                        ? PlatformRefusal("HXC130", "The owner cancelled the selected execution.", ToolExitCode.Cancelled) with { Status = "cancelled", RunId = ownedRunId }
                        : PlatformRefusal("HXT007", "The selected execution exhausted its finite execution lifetime.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
                }
            }
            catch (CandidatePolicy.CandidatePolicyException refusal)
            {
                result = PlatformRefusal("HXP301", refusal.Message, ToolExitCode.UsageOrManifest);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = PlatformRefusal("HXT007", "The owned execution failed; inspect retained diagnostics.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
            }

            if (native is null && session?.FirstObservedOutcome is { } observedOutcome)
            {
                native = observedOutcome;
                ToolDiagnostic retry = new("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The first native observation requires a durable recording retry.", "evidence");
                result = observedOutcome.Result with { RunId = ownedRunId, Diagnostics = [.. observedOutcome.Result.Diagnostics, retry] };
            }

            if (selected is not null && result.Status is "cancelled" or "withdrawn" && !cancellationToken.IsCancellationRequested && native?.TerminalCause is null)
            {
                result = PlatformRefusal("HXT007", "The selected execution exhausted its original finite lifetime.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
            }

            using CancellationTokenSource stopping = new();
            TimeSpan stopRemaining = TimeSpan.FromSeconds(selected!.StopTimeoutSeconds);
            if ((native?.StoppingTimestamp ?? session?.StoppingTimestamp ?? startupStoppingTimestamp) is long stoppingAt)
            {
                stopRemaining -= System.Diagnostics.Stopwatch.GetElapsedTime(stoppingAt);
            }

            if (stopRemaining > TimeSpan.Zero)
            {
                stopping.CancelAfter(stopRemaining);
            }
            else
            {
                await stopping.CancelAsync().ConfigureAwait(false);
            }

            bool passed = result.Outcome.ExitCode == ToolExitCode.Success;
            bool withdrawn = result.Status is "cancelled" or "withdrawn";
            if (selected is not null && ownedRunId is not null)
            {
                try
                {
                    string initialDisposition = retain && !passed && !withdrawn ? "retained" : "unknown";
                    using CancellationTokenSource recording = new(TimeSpan.FromSeconds(5));
                    record = session is null
                        ? await engine!.ReadCommittedOutcomeAsync(ownedRunId, recording.Token, waitForLock: true).ConfigureAwait(false)
                        : await engine!.RecordTestOutcomeAsync(ownedRunId, session.Readiness.Resources, result, initialDisposition, new CompositionRunResources([], [], Unverified: true), recording.Token, native, waitForLock: false, executionTiming: session.Plan.ExecutionTiming, environmentBindings: session.EnvironmentBindings, terminalCause: native?.TerminalCause ?? fallbackTerminalCause, testObservation: session.Readiness.TestObservation, archiveMetadata: false).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The selected run result could not be durably retained.", "evidence"));
                    passed = false;
                }
            }

            if (record is null && ownedRunId is not null && engine is not null)
            {
                try
                {
                    // A failed immediate callback already spent part of its finite metadata allowance.
                    TimeSpan recoveryRemaining = TimeSpan.FromSeconds(5);
                    if (session?.FirstObservedOutcome?.MetadataRecordingTimestamp is long firstObservation)
                    {
                        recoveryRemaining -= System.Diagnostics.Stopwatch.GetElapsedTime(firstObservation);
                    }

                    using CancellationTokenSource recovery = new();
                    if (recoveryRemaining > TimeSpan.Zero)
                    {
                        recovery.CancelAfter(recoveryRemaining);
                    }
                    else
                    {
                        await recovery.CancelAsync().ConfigureAwait(false);
                    }

                    record = await engine.ReadCommittedOutcomeAsync(ownedRunId, recovery.Token, waitForLock: true).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The committed first observation could not be recovered.", "evidence"));
                }
            }

            if (record is not null)
            {
                result = result with { Status = record.TerminalStatus, Outcome = record.TerminalOutcome };
                passed = record.TerminalOutcome.ExitCode == ToolExitCode.Success;
                withdrawn = record.TerminalStatus is "cancelled" or "withdrawn";
            }

            bool attachedFailure = false;
            bool holdsVerified = true;
            bool cleanupPending = false;
            bool cancellationWithdrawn = false;
            string? attachmentDirectory = ownedRunId is null ? null : Path.Combine(options.StateDirectory, "diagnostics", ownedRunId);
            if (!ci && attachmentDirectory is not null)
            {
                try
                {
                    using CancellationTokenSource attachmentMetadata = new(TimeSpan.FromSeconds(5));
                    IReadOnlyList<CompositionAttachment> attachments = cancellationToken.IsCancellationRequested
                        ? await CompositionAttachmentStore.ReadAsync(attachmentDirectory, ownedRunId!, attachmentMetadata.Token).ConfigureAwait(false)
                        : await CompositionAttachmentStore.ObserveAsync(attachmentDirectory, ownedRunId!, DateTimeOffset.UtcNow, attachmentMetadata.Token).ConfigureAwait(false);
                    result = result with { Diagnostics = [.. result.Diagnostics.Where(item => item.RuleId != "HXI011"), .. CompositionAttachmentStore.WithdrawalDiagnostics(attachments)] };

                    attachedFailure = attachments.Any(item => item.Outcome == "failed");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException)
                {
                    holdsVerified = false;
                    cleanupPending = true;
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Attachment outcomes could not be verified; cleanup remains pending and retryable with down for this owner.", "evidence"));
                }
            }

            bool retentionEstablished = attachedFailure || record?.FailureRetained == true || (record is null && retain && !passed && !withdrawn);
            bool cleanup = session is not null && !(PersistsAfterExit(command) && passed)
                && holdsVerified && !retentionEstablished && record?.CleanupDisposition != "complete";
            if (cleanup && !ci && record is null)
            {
                cleanup = false;
                result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Automatic cleanup remains pending because the first terminal result could not be retained; retry down for this owner.", "evidence"));
            }

            using CancellationTokenSource deferredStopping = new();
            bool waitedForHolds = false;
            if (cleanup && !ci && record is { TerminalOutcome.ExitCode: ToolExitCode.Success } && ownedRunId is not null)
            {
                // The first outcome is already recorded; success-cleanup waits until every accepted hold has ended or passed its deadline.
                string holdDirectory = Path.Combine(options.StateDirectory, "diagnostics", ownedRunId);
                try
                {
                    attachedFailure = await CompositionAttachmentStore.WaitForHoldsOrFailureAsync(holdDirectory, ownedRunId, options.HoldPollInterval, cancellationToken).ConfigureAwait(false);
                    IReadOnlyList<CompositionAttachment> completedHolds = await CompositionAttachmentStore.ReadAsync(holdDirectory, ownedRunId, cancellationToken).ConfigureAwait(false);
                    result = result with { Diagnostics = [.. result.Diagnostics.Where(item => item.RuleId != "HXI011"), .. CompositionAttachmentStore.WithdrawalDiagnostics(completedHolds)] };
                    cleanup = !attachedFailure;
                    waitedForHolds = true;
                    deferredStopping.CancelAfter(TimeSpan.FromSeconds(selected!.StopTimeoutSeconds));
                }
                catch (OperationCanceledException)
                {
                    // Cancellation during the success hold wait withdraws holds within a fresh stop budget.
                    waitedForHolds = true;
                    deferredStopping.CancelAfter(TimeSpan.FromSeconds(selected!.StopTimeoutSeconds));
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                {
                    cleanup = false;
                    cleanupPending = true;
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Attachment holds could not be verified; cleanup remains pending and retryable with down for this owner.", "evidence"));
                }
            }

            if (!ci && cancellationToken.IsCancellationRequested && ownedRunId is not null && attachmentDirectory is not null)
            {
                try
                {
                    using CancellationTokenSource withdrawalMetadata = new(TimeSpan.FromSeconds(5));
                    CancellationToken withdrawalToken = withdrawalMetadata.Token;
                    await engine!.RecordOwnerCancellationAsync(ownedRunId, withdrawalToken).ConfigureAwait(false);
                    IReadOnlyList<CompositionAttachment> ended = await CompositionAttachmentStore.WithdrawAsync(attachmentDirectory, ownedRunId, "owner-cancelled", DateTimeOffset.UtcNow, withdrawalToken).ConfigureAwait(false);
                    result = result with { Diagnostics = [.. result.Diagnostics.Where(item => item.RuleId != "HXI011"), .. CompositionAttachmentStore.WithdrawalDiagnostics(ended)] };
                    attachedFailure = (await CompositionAttachmentStore.ReadAsync(attachmentDirectory, ownedRunId, withdrawalToken).ConfigureAwait(false)).Any(item => item.Outcome == "failed");
                    cleanup = cleanup && !attachedFailure;
                    cancellationWithdrawn = true;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException or OperationCanceledException)
                {
                    cleanup = false;
                    cleanupPending = true;
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Attachment withdrawal or its report could not be durably recorded; cleanup remains pending and retryable with down for this owner.", "evidence"));
                }
            }

            if (cleanupPending && record is not null && ownedRunId is not null)
            {
                try
                {
                    using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                    record = await engine!.RecordTestOutcomeAsync(ownedRunId, record.Resources, result, record.ResourceDisposition, record.RemainingResources, metadata.Token, waitForLock: false, cleanupDisposition: "pending", cleanupAttemptId: record.CleanupAttemptId ?? CompositionRunPlanFactory.NewRunId(), archiveMetadata: false).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Pending cleanup state could not be durably recorded; retry down for this owner.", "evidence"));
                }
            }

            if ((record?.FailureRetained == true || attachedFailure) && record is { CleanupDisposition: "not-requested" } && ownedRunId is not null)
            {
                try
                {
                    using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                    CompositionRunResources survivors = await engine!.ObserveOwnedResourcesAsync(ownedRunId, metadata.Token).ConfigureAwait(false);
                    record = await engine.RecordTestOutcomeAsync(ownedRunId, record.Resources, result, "retained", survivors, metadata.Token, executionTiming: session?.Plan.ExecutionTiming, environmentBindings: session?.EnvironmentBindings, archiveMetadata: false).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Retained resources could not be fully recorded.", "evidence"));
                }
            }

            if (cleanup && !ci && record is not null)
            {
                using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                string published = Path.Combine(options.StateDirectory, "diagnostics", record.RunId, "test-result.json");
                TestEnvironment.TestEnvironmentContractReadResult? publication = null;
                try
                {
                    if (SelectedNativeTestReport.IsOwnedRegularPath(published, Path.GetDirectoryName(published)!))
                    {
                        publication = TestEnvironment.TestEnvironmentContractReader.ReadResult(published, metadata.Token);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    // Failed inspection leaves cleanup pending and preserves the committed outcome.
                }

                string expectedOutcome = (record.TerminalOutcome.ExitCode == ToolExitCode.Success, record.TerminalOutcome.ExitCode == ToolExitCode.Cancelled) switch
                {
                    (true, _) => "passed",
                    (_, true) => "cancelled",
                    _ => "failed",
                };
                bool matchingPublication = publication?.IsValid == true
                    && publication.Document!.Value.GetProperty("bindings").GetProperty("run").GetProperty("runId").GetString() == record.RunId
                    && publication.Document.Value.GetProperty("bindings").GetProperty("run").GetProperty("attemptId").GetString() == session!.Plan.AttemptId
                    && publication.Document.Value.GetProperty("bindings").GetProperty("run").GetProperty("environmentId").GetString() == session.Plan.EnvironmentId
                    && publication.Document.Value.GetProperty("terminal").GetProperty("outcome").GetString() == expectedOutcome
                    && publication.Document.Value.GetProperty("terminal").GetProperty("recordedAt").GetDateTimeOffset() == record.TerminalAtUtc;
                if (!matchingPublication)
                {
                    cleanup = false;
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Automatic cleanup is pending until a valid terminal result is durably retained; retry down for this owner.", "evidence"));
                    try
                    {
                        record = await engine!.RecordTestOutcomeAsync(record.RunId, record.Resources, result, record.ResourceDisposition, record.RemainingResources, metadata.Token, waitForLock: false, cleanupDisposition: "pending", cleanupAttemptId: record.CleanupAttemptId ?? CompositionRunPlanFactory.NewRunId()).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        // The committed pending observation and active owner remain discoverable for explicit retry.
                    }
                }
            }

            if (cleanup)
            {
                CompositionDownResult down;
                try
                {
                    if (cancellationWithdrawn)
                    {
                        await engine!.StopWithdrawnSessionAsync(session!, (waitedForHolds ? deferredStopping : stopping).Token).ConfigureAwait(false);
                    }

                    down = await (options.PlatformDownOverride?.Invoke(session!, (waitedForHolds ? deferredStopping : stopping).Token) ?? engine!.DownWithinBudgetAsync(session!, (waitedForHolds ? deferredStopping : stopping).Token)).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    down = new CompositionDownResult(PlatformRefusal("HXR023", "Owned cleanup could not be verified; retry down for the same run.", ToolExitCode.TopologyOrLifecycle), new CompositionRunResources([], [], Unverified: true));
                }

                ToolCommandResult cleanupResult = down.Result;
                if (!down.Remaining.IsEmpty && cleanupResult.Outcome.ExitCode == ToolExitCode.Success)
                {
                    cleanupResult = PlatformRefusal("HXR023", "Owned resources remain or their scan is unavailable.", ToolExitCode.TopologyOrLifecycle);
                }

                result = CombineTestResults(result, null, cleanupResult) with { RunId = ownedRunId };
                result = result with { Diagnostics = [.. result.Diagnostics.Distinct()] };
                if (record is not null)
                {
                    string disposition = down.Remaining.Unverified ? "unknown" : "remaining";
                    if (cleanupResult.Outcome.ExitCode == ToolExitCode.Success)
                    {
                        disposition = "removed";
                    }

                    record = record with { ResourceDisposition = disposition, RemainingResources = down.Remaining };
                    try
                    {
                        using CancellationTokenSource metadata = new(TimeSpan.FromSeconds(5));
                        record = await engine!.RecordTestOutcomeAsync(record.RunId, record.Resources, result, disposition, down.Remaining, metadata.Token, waitForLock: false, executionTiming: session?.Plan.ExecutionTiming, environmentBindings: session?.EnvironmentBindings, cleanupDisposition: cleanupResult.Outcome.ExitCode == ToolExitCode.Success && down.Remaining.IsEmpty ? "complete" : "incomplete", cleanupAttemptId: record.CleanupAttemptId ?? CompositionRunPlanFactory.NewRunId(), archiveMetadata: false).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The selected cleanup result could not be durably retained.", "evidence"));
                    }
                }
            }
        }

        if (record is not null)
        {
            string candidatePath = Path.Combine(compositionOptions?.StateDirectory ?? CompositionRunStateStore.DefaultDirectory(), "diagnostics", record.RunId, "test-result.json");
            durableResultPath = File.Exists(candidatePath) ? candidatePath : null;
        }

        return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None, result.RunId, runId, nativeTests: native, nativePlatform: selected?.Executor, selectedTestRun: record, remainingResources: record?.RemainingResources, testResultPath: durableResultPath).ConfigureAwait(false);
    }

    private static ToolCommandResult PlatformRefusal(string ruleId, string message, ToolExitCode exit)
    {
        ToolPhase phase = ToolPhase.Test;
        ToolFailureCategory category = ToolFailureCategory.ProductOrTest;
        if (exit == ToolExitCode.UsageOrManifest)
        {
            phase = ToolPhase.Usage;
            category = ToolFailureCategory.Usage;
        }
        else if (exit == ToolExitCode.Cancelled)
        {
            phase = ToolPhase.Cleanup;
            category = ToolFailureCategory.Cancelled;
        }
        else if (exit == ToolExitCode.TopologyOrLifecycle)
        {
            phase = ToolPhase.Cleanup;
            category = ToolFailureCategory.TopologyOrLifecycle;
        }

        ToolDiagnostic diagnostic = new(ruleId, phase, category, message, "profile");
        return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(phase, category, ruleId, exit), [diagnostic]);
    }

    private static async Task<int> ExecutePlatformLifecycleAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filter,
        string? evidencePath,
        DateTimeOffset startedUtc,
        ToolOutputFormat format,
        TextWriter writer,
        string? descriptorChildEntryAssemblyPath,
        string? runId,
        CompositionEngineOptions? compositionOptions,
        WorkspaceMode mode,
        SourceMapping? preResolvedMapping,
        CancellationToken startupToken,
        CancellationToken cancellationToken)
    {
        CompositionEngine? engine = null;
        CompositionRunSession? ownedSession = null;
        bool handedOff = false;
        Func<CompositionRunSession, CancellationToken, Task<CompositionDownResult>>? cleanupSession = null;
        try
        {
            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
            ToolCommandResult result;
            if (!validation.IsValid && !(command == ModuleInvocationCommand.Down && runId is not null))
            {
                result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest), validation.Diagnostics);
            }
            else if (!string.IsNullOrWhiteSpace(profile))
            {
                ToolDiagnostic absent = new("HXC002", ToolPhase.Usage, ToolFailureCategory.Usage, "The requested qualification profile is not declared by this v2 manifest.", "profile");
                result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(absent.Phase, absent.Category, absent.RuleId, ToolExitCode.UsageOrManifest), [absent]);
            }
            else if (command == ModuleInvocationCommand.Test)
            {
                ToolDiagnostic absent = new("HXR028", ToolPhase.Topology, ToolFailureCategory.TopologyOrLifecycle, "The v2 declaration has no executable test profile; readiness alone does not prove tests completed.", "profile");
                result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(absent.Phase, absent.Category, absent.RuleId, ToolExitCode.TopologyOrLifecycle), [absent]);
            }
            else if (descriptorChildEntryAssemblyPath is null)
            {
                ToolDiagnostic unavailable = new("HXR014", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "Platform composition requires the packaged Builds AppHost.", "apphost");
                result = new ToolCommandResult("unavailable", ToolOutcome.Passed().Fail(unavailable.Phase, unavailable.Category, unavailable.RuleId, ToolExitCode.PrerequisiteUnavailable), [unavailable]);
            }
            else
            {
                CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
                SourceMapping? mapping = null;
                if (command != ModuleInvocationCommand.Down)
                {
                    mapping = preResolvedMapping ?? await (options.MappingResolverOverride?.Invoke(manifestPath, mode, startupToken) ?? (options.SourceMapping is not null ? Task.FromResult(options.SourceMapping) : WorkspaceRootResolver.ResolveAsync(manifestPath, mode, startupToken))).ConfigureAwait(false);
                }

                if (command == ModuleInvocationCommand.Debug && mapping is { Mode: not WorkspaceMode.Source })
                {
                    mapping = mapping with { Mode = WorkspaceMode.Source };
                }

                engine = new CompositionEngine(options with
                {
                    SourceMapping = mapping,
                    PersistAfterParentExit = PersistsAfterExit(command),
                    DebugAttach = command == ModuleInvocationCommand.Debug,
                });
                cleanupSession = options.PlatformDownOverride ?? engine.DownAsync;
                if (command == ModuleInvocationCommand.Down)
                {
                    string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false)));
                    IReadOnlyList<CompositionRunState> matches = runId is null ? await engine.StateStore.FindByManifestHashAsync(hash, cancellationToken).ConfigureAwait(false) : [];
                    if ((runId is not null && !CompositionRunPlanFactory.IsRunId(runId)) || (runId is null && matches.Count > 1))
                    {
                        ToolDiagnostic ambiguous = new("HXR024", ToolPhase.Usage, ToolFailureCategory.Usage, "A matching v2 run identity is required.", "runId");
                        result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ambiguous.Phase, ambiguous.Category, ambiguous.RuleId, ToolExitCode.UsageOrManifest), [ambiguous]);
                    }
                    else
                    {
                        CompositionRunState? target = runId is null ? matches.SingleOrDefault() : await engine.StateStore.TryReadAsync(runId, cancellationToken).ConfigureAwait(false);
                        if (target is not null && (target.Schema != CompositionRunState.SupportedSchema || (runId is not null && target.RunId != runId)))
                        {
                            result = PlatformRefusal("HXR024", "The recorded run does not match the requested owned identity.", ToolExitCode.UsageOrManifest);
                        }
                        else if (target is not null || runId is not null)
                        {
                            string exactRunId = runId ?? target!.RunId;
                            result = (await engine.DownAsync(exactRunId, cancellationToken).ConfigureAwait(false)).Result with { RunId = exactRunId };
                        }
                        else
                        {
                            result = new ToolCommandResult("completed", ToolOutcome.Passed(), [new ToolDiagnostic("HXI001", ToolPhase.Cleanup, ToolFailureCategory.None, "Runner-owned invocation cleanup completed.", "down")]);
                        }
                    }
                }
                else
                {
                    CompositionStartResult started = await (options.PlatformStartOverride?.Invoke(manifestPath, cancellationToken) ?? engine.StartAsync(manifestPath, cancellationToken)).ConfigureAwait(false);
                    ownedSession = started.Session;
                    result = started.Result with { RunId = started.Session?.Plan.RunId ?? started.RunId };
                }
            }

            int exit = await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, cancellationToken, result.RunId, runId).ConfigureAwait(false);
            handedOff = PersistsAfterExit(command) && exit == (int)ToolExitCode.Success;
            return exit;
        }
        finally
        {
            if (ownedSession is not null && !handedOff && cleanupSession is not null)
            {
                try
                {
                    _ = await cleanupSession(ownedSession, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    // Bounded engine cleanup retains exact run state when cleanup cannot be verified.
                }
            }
        }
    }

    private static bool PersistsAfterExit(ModuleInvocationCommand command) =>
        command is ModuleInvocationCommand.Run or ModuleInvocationCommand.Debug;

    private static ToolCommandResult MergeEvidenceFailure(ToolCommandResult result, ToolDiagnostic diagnostic)
    {
        if (result.Outcome.ExitCode == ToolExitCode.Success)
        {
            ToolOutcome evidenceOutcome = ToolOutcome.Passed().Fail(
                ToolPhase.Evidence,
                ToolFailureCategory.EvidencePolicy,
                diagnostic.RuleId,
                ToolExitCode.EvidenceSchemaOrPolicy);
            return result with { Status = "failed", Outcome = evidenceOutcome, Diagnostics = [.. result.Diagnostics, diagnostic] };
        }

        return result with { Diagnostics = [.. result.Diagnostics, diagnostic] };
    }

    /// <summary>
    /// Evaluates the pinned package catalog in a private scratch workspace before any descriptor child runs.
    /// </summary>
    /// <param name="mapping">The resolved mapping.</param>
    /// <param name="workspaceRoot">The runner-owned workspace root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The refusal diagnostics, or null when the catalog is usable or not in package mode.</returns>
    private static async Task<IReadOnlyList<ToolDiagnostic>?> PreflightPinnedCatalogAsync(SourceMapping mapping, string workspaceRoot, CancellationToken cancellationToken)
    {
        if (mapping.Mode != WorkspaceMode.Package || mapping.PlatformIdentity is null)
        {
            return null;
        }

        string root = Path.GetFullPath(workspaceRoot);
        bool rootExisted = Directory.Exists(root);
        string workspace = Path.Combine(root, "catalog-preflight-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<ToolDiagnostic>? refusal = null;
        bool removed;
        try
        {
            CompositionWorkspace.CreatePrivateDirectory(root);
            CompositionWorkspace.CreatePrivateDirectory(workspace);
            await CatalogVersionMaterializer.WriteAsync(mapping, workspace, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceMappingException exception)
        {
            refusal = exception.Diagnostics;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            refusal = [new ToolDiagnostic("HXW009", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, $"I/O failure preparing the pinned catalog preflight under '{workspace}': {exception.Message}", workspace)];
        }
        finally
        {
            removed = await CompositionWorkspace.TryDeleteAsync(workspace, CancellationToken.None).ConfigureAwait(false);
            if (removed && !rootExisted)
            {
                try
                {
                    // Leave no workspace footprint before composition.
                    Directory.Delete(root);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // A non-empty root now belongs to another run.
                }
            }
        }

        // A usable catalog continues even if the scratch copy lingers; the engine materializes its own.
        return refusal is null || removed
            ? refusal
            : [.. refusal, new ToolDiagnostic("HXW009", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, $"Private catalog workspace '{workspace}' could not be removed after refusal.", workspace, "Remove this private workspace before retrying.")];
    }

    private static Task<int> WriteLifecycleFailureAsync(
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
        return WriteResultAsync(
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
            cancellationToken);
    }

    private static ToolDiagnostic NonexecutableTopologyDiagnostic() => new(
        "HXR003",
        ToolPhase.Prerequisite,
        ToolFailureCategory.PrerequisiteUnavailable,
        "The approved descriptor ABI cannot yet be composed into a supported runtime.",
        "runtime",
        "Qualify the runner-owned G-6 topology before retrying.");

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