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
        WorkspaceMode mode = WorkspaceMode.Source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(writer);

        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        using ModuleTestExecutionBudget? testBudget = command == ModuleInvocationCommand.Test ? new ModuleTestExecutionBudget(cancellationToken) : null;
        if (testBudget is not null && PlatformManifestCommand.IsV2(manifestPath))
        {
            PlatformManifestValidationResult provisional = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
            if (provisional.IsValid && ModuleTestSelection.Select(provisional.Declarations!, profile, manifestPath, startedUtc) is { } provisionalTest)
            {
                testBudget.Restrict(provisionalTest.ExecutionTimeoutSeconds);
            }
        }

        CancellationToken setupToken = testBudget?.Token ?? cancellationToken;
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
            if (command == ModuleInvocationCommand.Down && runId is not null)
            {
                return await ExecuteExactDownAsync(manifestPath, runId, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, compositionOptions, cancellationToken).ConfigureAwait(false);
            }

            // A direct reference may contain a stale or absent working-tree manifest.
            // Resolve its recorded gitlink before parsing any of its bytes.
            SourceMapping? preResolvedMapping = null;
            if (command != ModuleInvocationCommand.Down
                && !string.IsNullOrWhiteSpace(descriptorChildEntryAssemblyPath)
                && !await (compositionOptions?.RootManifestCheckOverride?.Invoke(manifestPath, setupToken) ?? WorkspaceRootResolver.IsRootManifestAsync(manifestPath, setupToken)).ConfigureAwait(false))
            {
                preResolvedMapping = await (compositionOptions?.MappingResolverOverride?.Invoke(manifestPath, mode, setupToken) ?? WorkspaceRootResolver.ResolveAsync(manifestPath, mode, setupToken)).ConfigureAwait(false);
                _identityWarnings.Value = PlatformIdentityResolver.Diagnostics(preResolvedMapping.IdentityFindings, warning: true);
            }

            if (PlatformManifestCommand.IsV2(manifestPath))
            {
                return await ExecutePlatformAsync(command, manifestPath, profile, filter, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, runId, compositionOptions, mode, preResolvedMapping, testBudget, cancellationToken).ConfigureAwait(false);
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
                    earlyOptions.AspireCommand, earlyOptions.AspireProbeTimeout, cancellationToken).ConfigureAwait(false);
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
                    ?? await WorkspaceRootResolver.ResolveAsync(manifestPath, mode, cancellationToken).ConfigureAwait(false);
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

                ToolDiagnostic? aspire = preResolvedMapping is null ? preResolutionAspire : await CompositionPrerequisiteProbe.ProbeAspireAsync(
                    options.AspireCommand, options.AspireProbeTimeout, cancellationToken).ConfigureAwait(false);
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
                IReadOnlyList<ToolDiagnostic>? catalogRefusal = await PreflightPinnedCatalogAsync(mapping, options.WorkspaceRoot, cancellationToken).ConfigureAwait(false);
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
                    cancellationToken,
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
                NativeTestExecutionResult? nativeResult = profileResult?.Outcome.ExitCode == ToolExitCode.Success
                    && persistedProfile!.NativeTests is PersistedProfileNativeTests nativeTests
                        ? await NativeTestExecutor.ExecuteAsync(session!, nativeTests, manifestPath, cancellationToken, options.NativeDotnetCommand ?? "dotnet").ConfigureAwait(false)
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
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && testBudget?.Token.IsCancellationRequested == true)
        {
            ToolCommandResult expired = PlatformRefusal("HXT007", "The selected execution exhausted its original finite lifetime during setup.", ToolExitCode.ProductOrTest);
            return await WriteResultAsync(expired.Status, expired.Outcome, expired.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None).ConfigureAwait(false);
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
        ModuleTestRunRecord? selectedTestRun = null)
    {
        IReadOnlyList<ToolDiagnostic> warnings = _identityWarnings.Value ?? [];
        ToolCommandResult result = new(status, outcome, [.. warnings, .. diagnostics]) { RunId = runId };
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
            string expectedWorkspace = Path.GetFullPath(Path.Combine(options.WorkspaceRoot, runId));
            result = state is not null && (state.Schema != CompositionRunState.SupportedSchema || state.RunId != runId || Path.GetFullPath(state.Workspace) != expectedWorkspace)
                ? PlatformRefusal("HXR024", "The recorded run does not match the requested owned identity.", ToolExitCode.UsageOrManifest)
                : (await engine.DownAsync(runId, cancellationToken).ConfigureAwait(false)).Result with { RunId = runId };
        }

        return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, ModuleInvocationCommand.Down, manifestPath, null, null, null, evidencePath, startedUtc, CancellationToken.None, runId, runId).ConfigureAwait(false);
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
        CancellationToken cancellationToken)
    {
        if (command != ModuleInvocationCommand.Test)
        {
            return await ExecutePlatformLifecycleAsync(command, manifestPath, profile, filter, evidencePath, startedUtc, format, writer, descriptorChildEntryAssemblyPath, runId, compositionOptions, mode, preResolvedMapping, cancellationToken).ConfigureAwait(false);
        }

        PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
        ModuleTestSelection? selected = null;
        ToolCommandResult result;
        NativeTestExecutionResult? native = null;
        ModuleTestRunRecord? record = null;
        if (!validation.IsValid)
        {
            result = new ToolCommandResult("failed", ToolOutcome.Passed().Fail(ToolPhase.Manifest, ToolFailureCategory.Manifest, validation.Diagnostics[0].RuleId, ToolExitCode.UsageOrManifest), validation.Diagnostics);
        }
        else if (filter is not null || runId is not null
                || (selected = ModuleTestSelection.Select(validation.Declarations!, profile, manifestPath, startedUtc)) is null
                || selected.Mode != (mode == WorkspaceMode.Source ? "source" : "package"))
        {
            result = PlatformRefusal("HXC002", "Select one declared test using --profile and its declared mode. Filters, attachment, and hosted reuse are unsupported.", ToolExitCode.UsageOrManifest);
        }
        else if (descriptorChildEntryAssemblyPath is null)
        {
            result = PlatformRefusal("HXR014", "Platform composition requires the packaged Builds AppHost.", ToolExitCode.PrerequisiteUnavailable);
        }
        else
        {
            CompositionEngineOptions options = compositionOptions ?? CompositionCommandOptions.Create(descriptorChildEntryAssemblyPath);
            testBudget!.Restrict(selected.ExecutionTimeoutSeconds);
            CompositionEngine? engine = null;
            bool ci = options.ContinuousIntegrationOverride ?? (string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase));
            bool retain = selected is not null && mode == WorkspaceMode.Source && !ci;
            options = options with
            {
                SelectedTest = selected,
                SelectedExecutionOwnerToken = cancellationToken,
                RetainOnFailure = retain,
                PersistAfterParentExit = PersistsAfterExit(command) || retain,
                DebugAttach = false,
                StopTimeout = selected is null ? options.StopTimeout : TimeSpan.FromSeconds(selected.StopTimeoutSeconds),
            };
            CompositionRunSession? session = null;
            string? ownedRunId = null;
            long? startupStoppingTimestamp = null;
            try
            {
                testBudget.Token.ThrowIfCancellationRequested();
                SourceMapping mapping = preResolvedMapping ?? options.SourceMapping ?? await (options.MappingResolverOverride?.Invoke(manifestPath, mode, testBudget.Token) ?? WorkspaceRootResolver.ResolveAsync(manifestPath, mode, testBudget.Token)).ConfigureAwait(false);
                testBudget.Token.ThrowIfCancellationRequested();
                options = options with { SourceMapping = mapping };
                engine = new CompositionEngine(options);
                CompositionStartResult started = await (options.SelectedPlatformStartOverride?.Invoke(manifestPath, selected!, testBudget.Token) ?? options.PlatformStartOverride?.Invoke(manifestPath, testBudget.Token) ?? engine.StartAsync(manifestPath, testBudget.Token)).ConfigureAwait(false);
                session = started.Session;
                startupStoppingTimestamp = started.StoppingTimestamp;
                ownedRunId = session?.RunId ?? started.RunId;
                result = started.Result with { RunId = ownedRunId };
                if (selected is not null && result.Outcome.ExitCode == ToolExitCode.Success)
                {
                    if (session is null)
                    {
                        result = PlatformRefusal("HXT009", "The selected test requires a ready owned session.", ToolExitCode.ProductOrTest);
                    }
                    else
                    {
                        native = await NativeTestExecutor.ExecuteAsync(session, new PersistedProfileNativeTests(selected.Entry, selected.Executor), manifestPath, testBudget.Token, options.NativeDotnetCommand ?? "dotnet", selected).ConfigureAwait(false);
                        result = native.Result with { RunId = ownedRunId, Diagnostics = [.. started.Result.Diagnostics, .. native.Result.Diagnostics] };
                    }
                }
            }
            catch (OperationCanceledException)
            {
                result = cancellationToken.IsCancellationRequested
                    ? PlatformRefusal("HXC130", "The owner withdrew the selected execution.", ToolExitCode.Cancelled) with { Status = "withdrawn", RunId = ownedRunId }
                    : PlatformRefusal("HXT007", "The selected execution exhausted its original finite lifetime.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                result = PlatformRefusal("HXT007", "The owned execution failed; inspect retained diagnostics.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
            }

            if (selected is not null && result.Status is "cancelled" or "withdrawn" && !cancellationToken.IsCancellationRequested)
            {
                result = PlatformRefusal("HXT007", "The selected execution exhausted its original finite lifetime.", ToolExitCode.ProductOrTest) with { RunId = ownedRunId };
            }

            using CancellationTokenSource stopping = new();
            TimeSpan stopRemaining = TimeSpan.FromSeconds(selected!.StopTimeoutSeconds);
            if ((native?.StoppingTimestamp ?? startupStoppingTimestamp) is long stoppingAt)
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
                    record = session is null
                        ? await CompositionDocumentStore.TryReadAsync<ModuleTestRunRecord>(Path.Combine(options.StateDirectory, "diagnostics", ownedRunId, "selected-test.json"), stopping.Token).ConfigureAwait(false)
                        : await engine!.RecordTestOutcomeAsync(ownedRunId, session.Readiness.Resources, result, initialDisposition, new CompositionRunResources([], [], Unverified: true), stopping.Token, native, waitForLock: false).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The selected run result could not be durably retained.", "evidence"));
                    passed = false;
                }
            }

            if (record is { TerminalOutcome.ExitCode: not ToolExitCode.Success })
            {
                result = result with { Status = record.TerminalStatus, Outcome = record.TerminalOutcome };
                passed = false;
                withdrawn = record.TerminalStatus == "withdrawn";
            }

            bool cleanup = session is not null && !(PersistsAfterExit(command) && passed)
                && !(retain && !passed && !withdrawn);
            if (cleanup)
            {
                CompositionDownResult down;
                try
                {
                    down = await (options.PlatformDownOverride?.Invoke(session!, stopping.Token) ?? engine!.DownWithinBudgetAsync(session!, stopping.Token)).ConfigureAwait(false);
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
                        record = await engine!.RecordTestOutcomeAsync(record.RunId, record.Resources, result, disposition, down.Remaining, stopping.Token, native, waitForLock: false).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                    {
                        result = MergeEvidenceFailure(result, new ToolDiagnostic("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "The selected cleanup result could not be durably retained.", "evidence"));
                    }
                }
            }
        }

        return await WriteResultAsync(result.Status, result.Outcome, result.Diagnostics, format, writer, command, manifestPath, null, profile, filter, evidencePath, startedUtc, CancellationToken.None, result.RunId, runId, nativeTests: native, nativePlatform: selected?.Executor, selectedTestRun: record).ConfigureAwait(false);
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
                SourceMapping? mapping = command == ModuleInvocationCommand.Down ? null : preResolvedMapping ?? options.SourceMapping ?? await WorkspaceRootResolver.ResolveAsync(manifestPath, mode, cancellationToken).ConfigureAwait(false);
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
            return new ToolCommandResult("failed", evidenceOutcome, [diagnostic]) { RunId = result.RunId };
        }

        return new ToolCommandResult(result.Status, result.Outcome, [.. result.Diagnostics, diagnostic]) { RunId = result.RunId };
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