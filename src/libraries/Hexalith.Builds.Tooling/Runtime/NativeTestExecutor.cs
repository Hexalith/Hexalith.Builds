// <copyright file="NativeTestExecutor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.TestReports;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Runs a product-owned native test project against a ready run and binds only its native TRX result.
/// </summary>
internal static class NativeTestExecutor
{
    /// <summary>The native report file name requested from either platform.</summary>
    private const string _reportFileName = "native.trx";

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Executes the declared native tests with the run handoff and evaluates their native report.
    /// </summary>
    /// <param name="session">The ready run session.</param>
    /// <param name="tests">The declared native tests.</param>
    /// <param name="manifestPath">The manifest path that anchors repository-relative paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="dotnetCommand">The native dotnet command; tests may supply a local process double.</param>
    /// <param name="selection">The optional v2 binding; legacy native tests retain their existing limit.</param>
    /// <param name="archiveDirectory">The optional durable owned diagnostic directory outside the child workspace.</param>
    /// <param name="onOutcome">The runner callback that commits the observed terminal result before optional archives.</param>
    /// <param name="ownerCancellationToken">The owner cancellation, distinct from the execution lifetime.</param>
    /// <returns>A result that passes only for a zero native exit and a passing native report.</returns>
    public static async Task<NativeTestExecutionResult> ExecuteAsync(
        CompositionRunSession session,
        PersistedProfileNativeTests tests,
        string manifestPath,
        CancellationToken cancellationToken,
        string dotnetCommand = "dotnet",
        ModuleTestSelection? selection = null,
        string? archiveDirectory = null,
        Func<NativeTestExecutionResult, CancellationToken, Task>? onOutcome = null,
        CancellationToken? ownerCancellationToken = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        if (selection is not null)
        {
            NativeTestExecutionResult selected = await ExecuteSelectedAsync(session, tests, manifestPath, selection, dotnetCommand, archiveDirectory, onOutcome, ownerCancellationToken ?? cancellationToken, cancellationToken).ConfigureAwait(false);
            return selected with { OwnerTermination = selected.OwnerTermination ?? session.NativeTermination };
        }

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        List<ToolDiagnostic> pathDiagnostics = [];
        string? project = ManifestPathValidator.ValidateExistingFile(tests.Project, root, "profile.nativeTests.project", pathDiagnostics);
        if (project is null)
        {
            return Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        if (session.Plan.SourceMapping is { } sourceMapping && !sourceMapping.ContainsProject(project))
        {
            ToolDiagnostic diagnostic = new(
                "HXW006",
                ToolPhase.Topology,
                ToolFailureCategory.TopologyOrLifecycle,
                $"Native test project '{project}' is outside the active module and mapped source roots.",
                project,
                "Use a test project in the active module or a mapped source root.");
            return new NativeTestExecutionResult(
                new ToolCommandResult(
                    "failed",
                    ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.TopologyOrLifecycle),
                    [diagnostic]),
                null,
                null);
        }

        string results = Path.Combine(session.Plan.Workspace, "native-tests");
        CompositionWorkspace.CreatePrivateDirectory(results);
        string token = CompositionTokenFactory.Create(
            session.SigningKey,
            new CompositionTokenRequest(
                "g4-native-tests",
                [session.Plan.TenantNamespace],
                [.. session.Plan.Modules.Select(module => module.Domain)],
                ["command:submit", "query:read"],
                false,
                _timeout + TimeSpan.FromMinutes(5)),
            DateTimeOffset.UtcNow);
        Dictionary<string, string> environment = new(NativeTestHandoff.Create(session.Plan, session.Readiness, token), StringComparer.Ordinal);
        if (session.Plan.SourceMapping is not null)
        {
            foreach (KeyValuePair<string, string> pair in SourceMappingMaterializer.Environment(session.Plan.SourceMapping, session.Plan.Workspace))
            {
                environment[pair.Key] = pair.Value;
            }
        }

        CompositionProcessResult process = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo(
                dotnetCommand,
                CreateArguments(tests.Platform, project, results, session.Plan.SourceMapping),
                Path.GetDirectoryName(project),
                environment),
            _timeout,
            cancellationToken).ConfigureAwait(false);
        string reportPath = Path.Combine(results, _reportFileName);
        string[] handoffSecrets = [token, session.SigningKey];
        byte[]? reportBytes = File.Exists(reportPath)
            ? await File.ReadAllBytesAsync(reportPath, cancellationToken).ConfigureAwait(false)
            : null;

        // A secret-bearing report stays raw so Evaluate rejects it; any other report is redacted before it is parsed, hashed, or retained.
        if (reportBytes is not null
            && !ContainsRetainedSecret(reportBytes, handoffSecrets)
            && NativeTestReportRedactor.Redact(reportBytes) is { } redacted)
        {
            reportBytes = redacted;
            await File.WriteAllBytesAsync(reportPath, reportBytes, cancellationToken).ConfigureAwait(false);
        }

        NativeTestReportLoadResult report = await NativeTestReportLoader.LoadAsync(reportPath, cancellationToken).ConfigureAwait(false);
        return Evaluate(process, report, reportBytes, handoffSecrets);
    }

    /// <summary>
    /// Creates the native <c>dotnet test</c> arguments for a platform.
    /// </summary>
    /// <param name="platform">The declared platform.</param>
    /// <param name="projectPath">The test project path.</param>
    /// <param name="resultsDirectory">The private results directory.</param>
    /// <param name="mapping">The optional workspace mapping.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The platform is not supported.</exception>
    internal static IReadOnlyList<string> CreateArguments(string platform, string projectPath, string resultsDirectory, SourceMapping? mapping = null)
    {
        IReadOnlyList<string> arguments = platform switch
        {
            PersistedProfileNativeTests.VsTest =>
                ["test", projectPath, "--logger", "trx;LogFileName=" + _reportFileName, "--results-directory", resultsDirectory],
            PersistedProfileNativeTests.MicrosoftTestingPlatform =>
                ["test", "--project", projectPath, "--results-directory", resultsDirectory, "--report-xunit-trx", "--report-xunit-trx-filename", _reportFileName],
            _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported native test platform."),
        };
        if (mapping is null)
        {
            return arguments;
        }

        string configuration = mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release";
        return [.. arguments, "--configuration", configuration];
    }

    /// <summary>
    /// Gets the repository-relative path of the report retained beside an evidence artifact.
    /// </summary>
    /// <param name="evidencePath">The repository-relative evidence path ending in <c>.json</c>.</param>
    /// <param name="platform">The native test platform.</param>
    /// <returns>The retained report path.</returns>
    internal static string RetainedReportPath(string evidencePath, string platform) =>
        evidencePath[..^".json".Length] + "." + platform + ".trx";

    /// <summary>
    /// Maps a native process result and its report to a fail-closed outcome.
    /// </summary>
    /// <param name="process">The native process result.</param>
    /// <param name="report">The loaded native report.</param>
    /// <param name="reportBytes">The raw report bytes, when present.</param>
    /// <param name="handoffSecrets">Values handed to the tests that must never be retained.</param>
    /// <returns>The outcome; report data is present only when the step passed.</returns>
    internal static NativeTestExecutionResult Evaluate(
        CompositionProcessResult process,
        NativeTestReportLoadResult report,
        byte[]? reportBytes,
        IReadOnlyList<string> handoffSecrets)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(handoffSecrets);

        if (!process.Started)
        {
            return Failed("HXR030", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, ToolExitCode.PrerequisiteUnavailable);
        }

        if (reportBytes is not null && ContainsRetainedSecret(reportBytes, handoffSecrets))
        {
            return Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
        }

        if (process.TimedOut)
        {
            return Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        if (!report.IsValid)
        {
            ToolDiagnostic diagnostic = report.Diagnostic!;
            return new NativeTestExecutionResult(
                new ToolCommandResult(
                    "failed",
                    ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.ProductOrTest),
                    [diagnostic]),
                null,
                null);
        }

        return process.ExitCode != 0 || reportBytes is null
            ? Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest)
            : new NativeTestExecutionResult(
                new ToolCommandResult(
                    "completed",
                    ToolOutcome.Passed(),
                    [new ToolDiagnostic("HXI005", ToolPhase.Test, ToolFailureCategory.None, "The native test report passed.", "nativeTests")]),
                report.Report,
                reportBytes);
    }

    /// <summary>Preserves owner withdrawal separately from an internal gate renewal failure.</summary>
    /// <param name="ownerWithdrawn">Whether the original owner/deadline token was cancelled.</param>
    /// <returns>The first interruption outcome, before optional closure diagnostics.</returns>
    internal static NativeTestExecutionResult SelectedInterruption(bool ownerWithdrawn)
    {
        if (!ownerWithdrawn)
        {
            return Failed("HXT009", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, ToolExitCode.TopologyOrLifecycle);
        }

        ToolDiagnostic withdrawn = new("HXC130", ToolPhase.Cleanup, ToolFailureCategory.Cancelled, "The owner or execution deadline stopped the native test.", "cancellation");
        return new NativeTestExecutionResult(new ToolCommandResult("cancelled", ToolOutcome.Passed().Fail(withdrawn.Phase, withdrawn.Category, withdrawn.RuleId, ToolExitCode.Cancelled), [withdrawn]), null, null);
    }

    /// <summary>Archives only pre-launch descriptor and inspected report snapshots within the existing processing bound.</summary>
    /// <param name="inspected">The first child outcome and its original stopping timestamp.</param>
    /// <param name="session">The active entry and execution timing.</param>
    /// <param name="descriptorSnapshot">The bounded descriptor bytes validated before child launch.</param>
    /// <param name="archiveDirectory">The durable owned diagnostics, when provided by the runner.</param>
    /// <param name="processingToken">The original remaining processing budget; this method never renews it.</param>
    /// <returns>The original outcome with any subsequent evidence diagnostic.</returns>
    internal static Task<NativeTestExecutionResult> ArchiveSelectedAsync(NativeTestExecutionResult inspected, CompositionRunSession session, byte[] descriptorSnapshot, string? archiveDirectory, CancellationToken processingToken)
        => ArchiveSelectedAsync(inspected, session, descriptorSnapshot, archiveDirectory, session.Plan.Workspace, processingToken);

    /// <summary>Archives pre-launch descriptor and inspected report snapshots, writing workspace copies only inside the executing run's own workspace.</summary>
    /// <param name="inspected">The first child outcome and its original stopping timestamp.</param>
    /// <param name="session">The active entry and execution timing.</param>
    /// <param name="descriptorSnapshot">The bounded descriptor bytes validated before child launch.</param>
    /// <param name="archiveDirectory">The durable owned diagnostics, when provided by the runner.</param>
    /// <param name="privateWorkspace">The owner workspace, or an attached run's own workspace.</param>
    /// <param name="processingToken">The original remaining processing budget; this method never renews it.</param>
    /// <returns>The original outcome with any subsequent evidence diagnostic.</returns>
    internal static async Task<NativeTestExecutionResult> ArchiveSelectedAsync(NativeTestExecutionResult inspected, CompositionRunSession session, byte[] descriptorSnapshot, string? archiveDirectory, string privateWorkspace, CancellationToken processingToken)
    {
        try
        {
            processingToken.ThrowIfCancellationRequested();
            string identity = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(session.Plan.SelectedTest!.Name)));
            ModuleTestExecutionTiming timing = session.Plan.ExecutionTiming!;
            byte[] timingBytes = Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(timing));
            string entry = Path.Combine(privateWorkspace, "native-tests", identity);
            if (archiveDirectory is not null)
            {
                string durableEntry = Path.Combine(archiveDirectory, "entries", identity);
                await ArchiveAsync(durableEntry, archiveDirectory).ConfigureAwait(false);
                await WriteSnapshotAsync(Path.Combine(archiveDirectory, "test-environment.json"), archiveDirectory, descriptorSnapshot, processingToken).ConfigureAwait(false);
                await WriteSnapshotAsync(Path.Combine(archiveDirectory, "execution-timing.json"), archiveDirectory, timingBytes, processingToken).ConfigureAwait(false);
                await ArchiveRoutingAsync(archiveDirectory, archiveDirectory).ConfigureAwait(false);
            }

            await WriteSnapshotAsync(Path.Combine(privateWorkspace, "execution-timing.json"), privateWorkspace, timingBytes, processingToken).ConfigureAwait(false);
            await ArchiveAsync(entry, privateWorkspace).ConfigureAwait(false);

            async Task ArchiveAsync(string directory, string ownedRoot)
            {
                await WriteSnapshotAsync(Path.Combine(directory, "test-environment.json"), ownedRoot, descriptorSnapshot, processingToken).ConfigureAwait(false);
                await WriteSnapshotAsync(Path.Combine(directory, "execution-timing.json"), ownedRoot, timingBytes, processingToken).ConfigureAwait(false);
                if (inspected.ReportBytes is { } reportBytes)
                {
                    await WriteSnapshotAsync(Path.Combine(directory, _reportFileName), ownedRoot, reportBytes, processingToken).ConfigureAwait(false);
                }

                if (inspected.ObservationBytes is { } observationBytes)
                {
                    await WriteSnapshotAsync(Path.Combine(directory, "functional-observations.json"), ownedRoot, observationBytes, processingToken).ConfigureAwait(false);
                }

                await ArchiveRoutingAsync(directory, ownedRoot).ConfigureAwait(false);
            }

            async Task ArchiveRoutingAsync(string directory, string ownedRoot)
            {
                foreach (KeyValuePair<string, byte[]> file in inspected.RoutingCatalogSnapshot ?? new Dictionary<string, byte[]>())
                {
                    await WriteSnapshotAsync(Path.Combine(directory, "routing-catalog", file.Key), ownedRoot, file.Value, processingToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or OperationCanceledException or NotSupportedException)
        {
            ToolDiagnostic diagnostic = new("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Inspected selected-entry output could not be archived within the original stopping budget.", "evidence");
            ToolCommandResult prior = inspected.Result;
            inspected = inspected with
            {
                Result = prior with
                {
                    Diagnostics = [.. prior.Diagnostics, diagnostic],
                },
            };
        }

        return inspected;
    }

    /// <summary>Runs one declared suite as a runner-owned attached run inside a live owner environment.</summary>
    /// <param name="session">The attached run's session view; its plan carries the attached selection over the owner's environment.</param>
    /// <param name="selection">The attached suite selection.</param>
    /// <param name="manifestPath">The owner's manifest path.</param>
    /// <param name="dotnetCommand">The native dotnet command.</param>
    /// <param name="attached">The attached identity, private paths and narrowed access.</param>
    /// <param name="onStarted">Receives the child process identity once launched.</param>
    /// <param name="onStopFailure">Receives surviving processes when a stop cannot be confirmed.</param>
    /// <param name="cancellationToken">Stops the attached child: expiry, withdrawal, own cancellation or owner drain.</param>
    /// <returns>The inspected native result; the caller decides the first outcome from its own hold record.</returns>
    internal static Task<NativeTestExecutionResult> ExecuteAttachedAsync(CompositionRunSession session, ModuleTestSelection selection, string manifestPath, string dotnetCommand, AttachedSuiteIdentity attached, Action<int>? onStarted, Action<IReadOnlyList<int>>? onStopFailure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(attached);
        return ExecuteSelectedAsync(session, new PersistedProfileNativeTests(selection.Entry, selection.Executor), manifestPath, selection, dotnetCommand, attached.DiagnosticsDirectory, null, cancellationToken, cancellationToken, attached, onStarted, onStopFailure);
    }

    /// <summary>Publishes held bytes through an owned destination and cleans temporary files only within that owner.</summary>
    /// <param name="path">The owned destination.</param>
    /// <param name="ownedRoot">The trusted owner directory.</param>
    /// <param name="bytes">The validated held snapshot.</param>
    /// <param name="cancellationToken">The existing publication allowance.</param>
    /// <returns>The bounded atomic publication.</returns>
    /// <exception cref="IOException">The destination is outside the owned path policy.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "Durable publication requires Flush(true); FlushAsync does not flush the device cache.")]
    internal static async Task WriteSnapshotAsync(string path, string ownedRoot, byte[] bytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureOwnedDestination();
        string directory = Path.GetDirectoryName(path)!;
        CompositionWorkspace.CreatePrivateDirectory(directory);
        EnsureOwnedDestination();
        string temporary = Path.Combine(directory, ".archive-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            FileStream stream = new(temporary, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CompositionDurableFlush.Flush(stream, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            EnsureOwnedDestination();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (SelectedNativeTestReport.IsOwnedParent(temporary, ownedRoot) && File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        void EnsureOwnedDestination()
        {
            if (!SelectedNativeTestReport.IsOwnedParent(path, ownedRoot) || new FileInfo(path).LinkTarget is not null
                || (File.Exists(path) && !SelectedNativeTestReport.IsOwnedRegularPath(path, ownedRoot)))
            {
                throw new IOException("The selected archive destination is outside its owned, link-free directory.");
            }
        }
    }

    private static async Task<NativeTestExecutionResult> ExecuteSelectedAsync(CompositionRunSession session, PersistedProfileNativeTests tests, string manifestPath, ModuleTestSelection selection, string dotnetCommand, string? archiveDirectory, Func<NativeTestExecutionResult, CancellationToken, Task>? onOutcome, CancellationToken ownerCancellationToken, CancellationToken cancellationToken, AttachedSuiteIdentity? attached = null, Action<int>? onStarted = null, Action<IReadOnlyList<int>>? onStopFailure = null)
    {
        // An attached run writes only inside its own workspace; the owner's workspace stays the shared read-only source mapping.
        string privateWorkspace = attached?.Workspace ?? session.Plan.Workspace;
        if (attached is not null && (session.Readiness.TestObservation is not null || session.Readiness.RoutingCatalogObservationsRequired
            || attached.Hold.OwnerRunId != session.Plan.RunId || attached.Hold.EnvironmentId != session.Plan.EnvironmentId))
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, "Attached suites cannot use the owner's gate, access or routing observations, or another environment.");
        }

        if (tests.Project != selection.Entry || tests.Platform != selection.Executor || !selection.MatchesSource(manifestPath)
            || session.Plan.SelectedTest != selection || session.Readiness.RunId != session.Plan.RunId
            || session.Readiness.Resources.Count == 0 || session.Readiness.Resources.Any(resource => resource.State != "Healthy")
            || (selection.DaprRuntimeVersion is not null && (session.Readiness.TestObservation is not { IsComplete: true } observed || observed.RunId != session.Plan.RunId))
            || (session.Plan.ExecutionTiming is { } existingTiming && DateTimeOffset.UtcNow >= existingTiming.DeadlineUtc))
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        ModuleTestExecutionTiming timing = session.Plan.ExecutionTiming ?? ModuleTestExecutionTiming.Start(selection, DateTimeOffset.UtcNow);
        session.Plan = session.Plan with { ExecutionTiming = timing };
        if (archiveDirectory is not null)
        {
            await WriteSnapshotAsync(Path.Combine(archiveDirectory, "execution-activation.json"), archiveDirectory, Encoding.UTF8.GetBytes(CompositionDocumentStore.Serialize(timing)), cancellationToken).ConfigureAwait(false);
        }

        await CompositionDocumentStore.WriteAsync(Path.Combine(privateWorkspace, "execution-timing.json"), timing, cancellationToken).ConfigureAwait(false);
        await CompositionDocumentStore.WriteAsync(attached?.PlanPath ?? Path.Combine(session.Plan.Workspace, "plan.json"), session.Plan, cancellationToken).ConfigureAwait(false);
        await CompositionDocumentStore.WriteAsync(attached?.ReadinessPath ?? session.Plan.ReadinessPath, session.Readiness, cancellationToken).ConfigureAwait(false);
        string descriptorPath;
        byte[] descriptorSnapshot;
        IReadOnlyDictionary<string, byte[]> routingCatalogSnapshot;
        try
        {
            descriptorPath = attached is null
                ? await TestEnvironment.TestEnvironmentDescriptorWriter.WriteAsync(session, archiveDirectory, cancellationToken).ConfigureAwait(false)
                : await TestEnvironment.TestEnvironmentDescriptorWriter.WriteAttachedAsync(session, Path.Combine(Path.GetDirectoryName(attached.DiagnosticsDirectory)!, session.Plan.RunId), attached, cancellationToken).ConfigureAwait(false);
            TestEnvironment.TestEnvironmentContractReadResult validated = TestEnvironment.TestEnvironmentContractReader.ReadDescriptor(descriptorPath, cancellationToken);
            if (!validated.IsValid)
            {
                throw new InvalidDataException("The handoff descriptor could not be snapshotted.");
            }

            descriptorSnapshot = Encoding.UTF8.GetBytes(validated.Document!.Value.GetRawText());
            session.EnvironmentBindings = validated.Document.Value.GetProperty("bindings").Clone();
        }
        catch (InvalidDataException exception)
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        string project = Path.Combine(root, selection.Entry);
        string results = Path.Combine(privateWorkspace, "native-tests");
        CompositionWorkspace.CreatePrivateDirectory(results);
        string reportPath = Path.Combine(results, _reportFileName);
        string observationPath = Path.Combine(results, "functional-observations.json");
        File.Delete(observationPath);
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }

        DateTimeOffset issued = DateTimeOffset.UtcNow;
        CompositionNativeTestScope? nativeScope = session.Readiness.NativeTestScope;
        if (nativeScope is not null && (nativeScope.RunId != session.Plan.RunId || nativeScope.ModuleId != selection.ModuleId
            || nativeScope.RoutingCatalogRoot.Length != 64 || nativeScope.Domains.Count == 0 || nativeScope.Tenants.Count == 0
            || nativeScope.Domains.Concat(nativeScope.Tenants).Any(value => string.IsNullOrWhiteSpace(value) || value.Contains('*', StringComparison.Ordinal))
            || !nativeScope.Tenants.Contains(session.Plan.TenantNamespace, StringComparer.Ordinal)))
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        // An attached bearer is narrowed to its run and never outlives its hold: exp = min(execution deadline, expiresAt).
        TimeSpan credentialLifetime = attached is null ? timing.CredentialExpiryUtc - issued : attached.AccessExpiry(timing) - issued;
        if (attached is not null && DateTimeOffset.FromUnixTimeSeconds(issued.Add(credentialLifetime).ToUnixTimeSeconds()) <= issued)
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, "The attached hold or execution deadline leaves no positive access lifetime.");
        }

        CompositionTokenRequest nativeIdentity = new(attached is null ? "g4-native-tests" : "g4-attached-tests:" + attached.RunId, nativeScope?.Tenants ?? [session.Plan.TenantNamespace], nativeScope?.Domains ?? [selection.ModuleId], ["command:submit", "query:read"], false, credentialLifetime)
        {
            Surface = nativeScope is null ? null : "agent",
        };
        string token = CompositionTokenFactory.Create(session.SigningKey, nativeIdentity, issued);
        DateTimeOffset credentialExpiry = DateTimeOffset.FromUnixTimeSeconds(issued.Add(credentialLifetime).ToUnixTimeSeconds());
        List<string> handoffSecrets = [token, session.SigningKey];
        IReadOnlyList<string>? endpointSecrets = await CompositionEndpointCredentials.ReadSecretsAsync(session.Plan, session.Readiness, attached, cancellationToken).ConfigureAwait(false);
        if (endpointSecrets is null)
        {
            return Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
        }

        handoffSecrets.AddRange(endpointSecrets);
        if (session.Readiness.TestObservation?.AccessPath is { } accessPath)
        {
            IReadOnlyList<string>? accessSecrets = await SelectedNativeTestReport.ReadAccessSecretsAsync(accessPath, session.Plan.Workspace, cancellationToken).ConfigureAwait(false);
            if (accessSecrets is null)
            {
                return Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
            }

            handoffSecrets.AddRange(accessSecrets);
        }

        try
        {
            routingCatalogSnapshot = await SelectedRoutingCatalogSnapshot.ReadAsync(session.Plan, descriptorSnapshot, cancellationToken, session.Readiness.RoutingCatalogObservationsRequired, handoffSecrets).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest, exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return Failed("HXT009", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        Dictionary<string, string> environment = new(NativeTestHandoff.Create(session.Plan, session.Readiness, token, attached), StringComparer.Ordinal)
        {
            ["HEXALITH_G4_TEST_REPORT_PATH"] = reportPath,
            [NativeTestHandoff.TestEnvironmentPath] = descriptorPath,
        };
        if (session.Plan.SourceMapping is { } mapping)
        {
            foreach (KeyValuePair<string, string> pair in SourceMappingMaterializer.Environment(mapping, session.Plan.Workspace))
            {
                environment[pair.Key] = pair.Value;
            }
        }

        if (attached is not null && selection.Executor != "python3")
        {
            // An attached dotnet child starts no reusable MSBuild node, MSBuild server or compiler server: those would inherit
            // the attached run tag and bearer, and the run-tag kill at attachment end would break owner or peer builds reusing them.
            environment["MSBUILDDISABLENODEREUSE"] = "1";
            environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
            environment["UseSharedCompilation"] = "false";
        }

        CompositionSelectedTestGate gate = new(session, cancellationToken);

        CompositionProcessResult process;
        bool cancelled = false;
        long firstStop = 0;
        IReadOnlyList<int> survivors = [];
        int? launchedProcessId = null;
        bool stopObserved = false;
        bool launchMetadataFailed = false;
        string? terminationPath = attached is null && archiveDirectory is not null ? Path.Combine(archiveDirectory, "native-termination.json") : null;
        CompositionOwnerTermination? history = session.NativeTermination;
        CompositionOwnerTermination pending = history ?? new(
            [new("native:" + session.RunId, "pending", DateTimeOffset.UtcNow, "Native launch preparation began; launch has not yet been observed.")],
            [new("access:" + session.RunId, "not-applicable", DateTimeOffset.UtcNow, "Credentials were prepared but no access has been issued.")],
            []);
        try
        {
            if (terminationPath is not null)
            {
                if (history is null)
                {
                    history = await CompositionDocumentStore.ReadIfExistsAsync<CompositionOwnerTermination>(terminationPath, cancellationToken).ConfigureAwait(false);
                    pending = history ?? pending;
                }

                await CompositionDocumentStore.WriteAsync(terminationPath, pending, cancellationToken).ConfigureAwait(false);
            }

            gate.Start();
            cancellationToken.ThrowIfCancellationRequested();
            System.Diagnostics.ProcessStartInfo childStart = CompositionProcess.CreateStartInfo(selection.Executor == "python3" ? "python3" : dotnetCommand, selection.Executor == "python3" ? ["-E", "-s", "-B", project] : [.. CreateArguments(selection.Executor, project, results, session.Plan.SourceMapping), "--artifacts-path", Path.Combine(privateWorkspace, "test-builds")], Path.GetDirectoryName(project), environment);
            TimeSpan remainingExecution = timing.DeadlineUtc - DateTimeOffset.UtcNow;
            process = remainingExecution <= TimeSpan.Zero
                ? new CompositionProcessResult(true, -1, string.Empty, true)
                : await CompositionProcess.RunSelectedAsync(
                    childStart,
                    remainingExecution,
                    TimeSpan.FromSeconds(selection.StopTimeoutSeconds),
                    timestamp => Interlocked.CompareExchange(ref firstStop, timestamp, 0),
                    gate.Token,
                    surviving =>
                    {
                        survivors = surviving;
                        onStopFailure?.Invoke(surviving);
                    },
                    pid =>
                    {
                        launchedProcessId = pid;
                        onStarted?.Invoke(pid);
                    },
                    async (pid, launchToken) =>
                    {
                        pending = CompositionOwnerTermination.ObserveNative(session.RunId, history, new NativeTestExecutionResult(new ToolCommandResult("running", ToolOutcome.Passed(), []), null, null) { Launched = true, ProcessIds = [pid], CredentialExpiresAtUtc = credentialExpiry }, running: true);
                        session.NativeTermination = pending;
                        if (terminationPath is not null)
                        {
                            try
                            {
                                await CompositionDocumentStore.WriteAsync(terminationPath, pending, launchToken).ConfigureAwait(false);
                            }
                            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
                            {
                                launchMetadataFailed = true;
                            }
                        }
                    }).ConfigureAwait(false);
            stopObserved = process.Started && process.SurvivingProcessIds.Count == 0;
        }
        catch (OperationCanceledException)
        {
            cancelled = ownerCancellationToken.IsCancellationRequested;
            process = new CompositionProcessResult(true, -1, string.Empty, true);
            stopObserved = launchedProcessId is not null && survivors.Count == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException or System.ComponentModel.Win32Exception)
        {
            process = new CompositionProcessResult(true, -1, string.Empty, false);
        }

        DateTimeOffset nativeObservedAt = DateTimeOffset.UtcNow;
        long stoppingTimestamp = firstStop == 0 ? System.Diagnostics.Stopwatch.GetTimestamp() : firstStop;
        session.StoppingTimestamp ??= stoppingTimestamp;
        pending = CompositionOwnerTermination.ObserveNative(session.RunId, pending, new NativeTestExecutionResult(new ToolCommandResult("observed", ToolOutcome.Passed(), []), null, null)
        {
            Launched = launchedProcessId is not null,
            StopObserved = stopObserved,
            ProcessIds = launchedProcessId is { } observedPid ? [observedPid] : [],
            SurvivingProcessIds = [.. survivors.Concat(process.SurvivingProcessIds).Distinct()],
            CredentialExpiresAtUtc = launchedProcessId is null ? null : credentialExpiry,
            ObservedAtUtc = nativeObservedAt,
        });
        session.NativeTermination = pending;
        try
        {
            // Cancellation, timeout and a nonzero process exit are causal before report/closure diagnostics.
            if (onOutcome is not null && (cancelled || process.TimedOut || process.ExitCode != 0))
            {
                // Committing an observed outcome has its own finite metadata allowance. It never
                // changes the original stopping timestamp or renews execution, archival or cleanup.
                long metadataRecordingTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                using CancellationTokenSource immediate = new(TimeSpan.FromSeconds(5));

                NativeTestExecutionResult first = process.Started
                    ? Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest)
                    : Failed("HXR030", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, ToolExitCode.PrerequisiteUnavailable);
                first = first with { Result = first.Result with { Status = "failed" } };
                if (cancelled || gate.Failed)
                {
                    first = SelectedInterruption(cancelled);
                }

                first = first with
                {
                    StoppingTimestamp = stoppingTimestamp,
                    MetadataRecordingTimestamp = metadataRecordingTimestamp,
                    TerminalCause = process.TimedOut && !cancelled && !gate.Failed ? "execution-timeout" : null,
                    Launched = launchedProcessId is not null,
                    StopObserved = stopObserved,
                    ObservedAtUtc = nativeObservedAt,
                    ProcessIds = launchedProcessId is { } pid ? [pid] : [],
                    SurvivingProcessIds = [.. survivors.Concat(process.SurvivingProcessIds).Distinct()],
                    CredentialExpiresAtUtc = launchedProcessId is null ? null : credentialExpiry,
                    DescriptorSnapshot = descriptorSnapshot,
                    OwnerTermination = session.NativeTermination,
                };
                session.FirstObservedOutcome ??= first;
                await onOutcome(first, immediate.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            await gate.DisposeAsync().ConfigureAwait(false);
        }

        if (terminationPath is not null)
        {
            using CancellationTokenSource recording = new(TimeSpan.FromSeconds(5));
            try
            {
                await CompositionDocumentStore.WriteAsync(terminationPath, pending, recording.Token).ConfigureAwait(false);
                launchMetadataFailed = false;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                launchMetadataFailed = true;
            }
        }

        TimeSpan processingRemaining = TimeSpan.FromSeconds(Math.Min(selection.StopTimeoutSeconds, 30)) - System.Diagnostics.Stopwatch.GetElapsedTime(stoppingTimestamp);
        using CancellationTokenSource processing = new();
        if (processingRemaining > TimeSpan.Zero)
        {
            processing.CancelAfter(processingRemaining);
        }
        else
        {
            await processing.CancelAsync().ConfigureAwait(false);
        }

        (NativeTestReportLoadResult report, byte[]? bytes) = await SelectedNativeTestReport.ReadAsync(reportPath, privateWorkspace, handoffSecrets, processing.Token).ConfigureAwait(false);
        bool requiresObservations = selection is { ModuleId: "memories", Name: "functional-retention" }
            or { ModuleId: "parties", Name: "platform-source" or "platform-package" } || File.Exists(observationPath);
        byte[]? observations = requiresObservations
            ? await SelectedNativeTestReport.ReadObservationsAsync(observationPath, privateWorkspace, attached?.RunId ?? session.Plan.RunId, handoffSecrets, processing.Token).ConfigureAwait(false)
            : null;
        NativeTestExecutionResult evaluated;
        if (report.Diagnostic?.RuleId == "HXT008")
        {
            evaluated = Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
        }
        else if (cancelled || (gate.Failed && process.TimedOut))
        {
            evaluated = SelectedInterruption(cancelled);
        }
        else
        {
            evaluated = Evaluate(process, report, bytes, handoffSecrets);
            if (!process.Started)
            {
                evaluated = evaluated with { Result = evaluated.Result with { Status = "failed" } };
            }

            if (evaluated.Result.Outcome.ExitCode == ToolExitCode.Success && report.Report?.Skipped > 0)
            {
                evaluated = Failed("HXT004", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
            }
        }

        if (requiresObservations && observations is null && evaluated.Result.Outcome.ExitCode == ToolExitCode.Success)
        {
            evaluated = Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
        }

        if (launchMetadataFailed)
        {
            ToolDiagnostic tracking = new("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, "Native launch metadata could not be durably retained.", "evidence");
            evaluated = evaluated with { Result = evaluated.Result with { Diagnostics = [.. evaluated.Result.Diagnostics, tracking] } };
        }

        if (gate.Failed)
        {
            ToolDiagnostic closure = new("HXT009", ToolPhase.Cleanup, ToolFailureCategory.TopologyOrLifecycle, "The selected gate could not be safely renewed or closed; its owner changed or storage was unavailable.", "selectedGate");
            ToolCommandResult prior = evaluated.Result;
            evaluated = evaluated with { Result = new ToolCommandResult(prior.Outcome.ExitCode == ToolExitCode.Success ? "failed" : prior.Status, prior.Outcome.Fail(closure.Phase, closure.Category, closure.RuleId, ToolExitCode.TopologyOrLifecycle), [.. prior.Diagnostics, closure]) };
        }

        timing = timing with { ActualDurationSeconds = Math.Max(0, (DateTimeOffset.UtcNow - timing.StartedAtUtc).TotalSeconds - System.Diagnostics.Stopwatch.GetElapsedTime(stoppingTimestamp).TotalSeconds) };
        session.Plan = session.Plan with { ExecutionTiming = timing };
        NativeTestExecutionResult inspected = evaluated with { Report = report.Report, ReportBytes = bytes, ObservationBytes = observations, RoutingCatalogSnapshot = routingCatalogSnapshot, StoppingTimestamp = stoppingTimestamp, TerminalCause = process.TimedOut && !cancelled && !gate.Failed ? "execution-timeout" : null, SurvivingProcessIds = [.. survivors.Concat(process.SurvivingProcessIds).Distinct()], CredentialExpiresAtUtc = launchedProcessId is null ? null : credentialExpiry, DescriptorSnapshot = descriptorSnapshot, OwnerTermination = session.NativeTermination, Launched = launchedProcessId is not null, StopObserved = stopObserved, ObservedAtUtc = nativeObservedAt, ProcessIds = launchedProcessId is { } nativePid ? [nativePid] : [] };
        if (onOutcome is not null)
        {
            inspected = inspected with { MetadataRecordingTimestamp = System.Diagnostics.Stopwatch.GetTimestamp() };
            session.FirstObservedOutcome ??= inspected;
            using CancellationTokenSource recording = new(TimeSpan.FromSeconds(5));
            await onOutcome(inspected, recording.Token).ConfigureAwait(false);
        }

        return await ArchiveSelectedAsync(inspected, session, descriptorSnapshot, archiveDirectory, privateWorkspace, processing.Token).ConfigureAwait(false);
    }

    private static bool ContainsRetainedSecret(byte[] reportBytes, IReadOnlyList<string> handoffSecrets)
    {
        string text = Encoding.UTF8.GetString(reportBytes);
        return handoffSecrets.Any(secret => !string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal))
            || ReportValues(text).Any(ManifestSecretDetector.ContainsSecret);
    }

    /// <summary>
    /// Splits a report into the individual XML values the credential detector can anchor on.
    /// </summary>
    /// <param name="text">The report text.</param>
    /// <returns>Every attribute and text value, or the whole text when it is not well-formed XML.</returns>
    private static IEnumerable<string> ReportValues(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException)
        {
            return [text];
        }

        return document.Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.Nodes().OfType<XText>().Select(node => node.Value)));
    }

    private static NativeTestExecutionResult Failed(string ruleId, ToolPhase phase, ToolFailureCategory category, ToolExitCode exitCode, string? message = null)
    {
        ToolDiagnostic diagnostic = new(ruleId, phase, category, message ?? MessageFor(ruleId), "nativeTests", HintFor(ruleId));
        return new NativeTestExecutionResult(
            new ToolCommandResult(
                exitCode == ToolExitCode.PrerequisiteUnavailable ? "unavailable" : "failed",
                ToolOutcome.Passed().Fail(phase, category, ruleId, exitCode),
                [diagnostic]),
            null,
            null);
    }

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXT009" => "The selected test source, readiness, or execution lifetime no longer matches the owned run.",
        "HXR030" => "The native test process could not be started.",
        "HXT008" => "Native output was missing, malformed, unsafe, contained prohibited material, or could not be retained under the inspection policy.",
        _ => "The native test process did not complete successfully.",
    };

    private static string HintFor(string ruleId) => ruleId switch
    {
        "HXR030" => "Install the repository-pinned .NET SDK so dotnet test is available.",
        "HXT008" => "Provide bounded valid native output in the owned private paths and never include handoff credentials.",
        _ => "Inspect the native test project and its platform configuration locally.",
    };
}