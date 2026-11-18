// <copyright file="TestEnvironmentDescriptorWriter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestEnvironment;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>Writes and validates the credential-free v1 handoff from an actually ready, owned environment.</summary>
public static class TestEnvironmentDescriptorWriter
{
    /// <summary>Writes a private descriptor and refuses missing catalog, composition, readiness or task observations.</summary>
    /// <param name="session">The ready runner-owned session.</param>
    /// <param name="cancellationToken">The execution cancellation token.</param>
    /// <returns>The absolute validated descriptor path.</returns>
    /// <exception cref="InvalidDataException">A required real observation or immutable binding is unavailable.</exception>
    public static Task<string> WriteAsync(CompositionRunSession session, CancellationToken cancellationToken = default)
        => WriteAsync(session, null, cancellationToken);

    /// <summary>Writes a private descriptor that also carries the owner's accepted attachment holds.</summary>
    /// <param name="session">The ready runner-owned session.</param>
    /// <param name="ownerDiagnosticsDirectory">The owner's durable diagnostics directory holding accepted attachments, when known.</param>
    /// <param name="cancellationToken">The execution cancellation token.</param>
    /// <returns>The absolute validated descriptor path.</returns>
    /// <exception cref="InvalidDataException">A required real observation or immutable binding is unavailable.</exception>
    public static Task<string> WriteAsync(CompositionRunSession session, string? ownerDiagnosticsDirectory, CancellationToken cancellationToken = default)
        => WriteCoreAsync(session, ownerDiagnosticsDirectory, null, cancellationToken);

    /// <summary>Writes the private descriptor of a runner-owned attached run into its own attachment workspace.</summary>
    /// <param name="session">The attached run's session view over the ready owner environment.</param>
    /// <param name="ownerDiagnosticsDirectory">The owner's durable diagnostics directory holding accepted attachments.</param>
    /// <param name="attached">The attached run identity and private paths.</param>
    /// <param name="cancellationToken">The execution cancellation token.</param>
    /// <returns>The absolute validated descriptor path.</returns>
    /// <exception cref="InvalidDataException">A required real observation or immutable binding is unavailable.</exception>
    internal static Task<string> WriteAttachedAsync(CompositionRunSession session, string ownerDiagnosticsDirectory, AttachedSuiteIdentity attached, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attached);
        return WriteCoreAsync(session, ownerDiagnosticsDirectory, attached, cancellationToken);
    }

    /// <summary>Gets the attached-role run binding, which names the producing attached run and its owner.</summary>
    /// <param name="attachedRunId">The attached run.</param>
    /// <param name="attemptId">The issued attached attempt.</param>
    /// <param name="ownerRunId">The owner.</param>
    /// <param name="environmentId">The owner's environment.</param>
    /// <returns>The v1 attached run binding.</returns>
    internal static object AttachedRun(string attachedRunId, string attemptId, string ownerRunId, string environmentId) =>
        new { runId = attachedRunId, attemptId, attemptSequence = 1, ownerRunId, environmentId, role = "attached", attachedRunId };

    /// <summary>Gets the explicit baseline-only classification of an attached run; attachment never inherits an owner's candidate decision.</summary>
    /// <returns>The v1 baseline-only candidate binding.</returns>
    internal static object AttachedCandidate() => new { state = "populated", classification = "baseline-only", reason = "Runner-owned attached runs are diagnostic baseline-only runs." };

    /// <summary>Writes the owner's or an attached run's private descriptor from an actually ready, owned environment.</summary>
    /// <param name="session">The ready session, or an attached run's view of it.</param>
    /// <param name="ownerDiagnosticsDirectory">The owner's durable diagnostics directory holding accepted attachments, when known.</param>
    /// <param name="attached">The attached run identity, or null for the owner.</param>
    /// <param name="cancellationToken">The execution cancellation token.</param>
    /// <returns>The absolute validated descriptor path.</returns>
    /// <exception cref="InvalidDataException">A required real observation or immutable binding is unavailable.</exception>
    internal static async Task<string> WriteCoreAsync(CompositionRunSession session, string? ownerDiagnosticsDirectory, AttachedSuiteIdentity? attached, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        CompositionRunPlan plan = session.Plan;
        CompositionReadiness ready = session.Readiness;
        ModuleTestSelection selected = plan.SelectedTest ?? throw new InvalidDataException("A declared suite is required.");
        ModuleTestExecutionTiming timing = plan.ExecutionTiming ?? throw new InvalidDataException("Execution must be activated before handoff.");
        SourceMapping mapping = plan.SourceMapping ?? throw new InvalidDataException("The resolved mapping is unavailable.");
        ValidatePreProvisioning(plan.EffectiveDeadline ?? throw new InvalidDataException("The startup deadline is unavailable."));
        if (ready.Resources.Any(resource => string.IsNullOrWhiteSpace(resource.ModuleId)))
        {
            throw new InvalidDataException("Every composed resource requires explicit declaration ownership. Shared runner infrastructure requires the selected module's declaration.");
        }

        if (!plan.IsPlatformComposition || selected.Mode != (mapping.Mode == WorkspaceMode.Source ? "source" : "package") || ready.RunId != plan.RunId || ready.ObservedAt is null
            || ready.Resources.Count == 0 || ready.Resources.Any(resource => resource.State != "Healthy" || resource.ObservedAt is null)
            || ready.Resources.Select(resource => resource.Name).Distinct(StringComparer.Ordinal).Count() != ready.Resources.Count
            || !ready.Resources.Any(resource => resource.Endpoints.Any(endpoint => endpoint.Uri == ready.EventStoreEndpoint))
            || plan.AttemptId == plan.RunId || plan.EnvironmentId == plan.RunId
            || plan.StartupDeadlineUtc is null || plan.EffectiveDeadline is null || ready.StartupDurationSeconds is null
            || timing.EffectiveSeconds != selected.ExecutionTimeoutSeconds || DateTimeOffset.UtcNow >= timing.DeadlineUtc)
        {
            throw new InvalidDataException("The owned environment has incomplete or inconsistent observations.");
        }

        foreach (IReadOnlyList<CompositionResourceEndpoint> endpoints in ready.Resources.Select(resource => resource.Endpoints))
        {
            if (endpoints.Select(endpoint => endpoint.Name).Distinct(StringComparer.Ordinal).Count() != endpoints.Count
                || endpoints.Any(endpoint => !SafeEndpoint(endpoint)))
            {
                throw new InvalidDataException("An observed endpoint is invalid or unsafe.");
            }
        }

        string[] requiredTasks = [.. plan.StartupTasks.Select(task => task.ModuleId + "/" + task.Name)];
        if (ready.StartupTaskResults.Count != requiredTasks.Length
            || ready.StartupTaskResults.Any(task => task.Status != "passed" || task.StartedAt is null || task.FinishedAt < task.StartedAt || task.FinishedAt is null)
            || !ready.StartupTaskResults.Select(task => task.TaskId).Order(StringComparer.Ordinal).SequenceEqual(requiredTasks.Order(StringComparer.Ordinal)))
        {
            throw new InvalidDataException("Required startup tasks have not all passed.");
        }

        CatalogVersionSnapshot catalog;
        IReadOnlyList<PlatformModuleDeclaration> closure;
        try
        {
            catalog = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(plan.Workspace, "source-mapping", "catalog-versions.json"));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Verified catalog identity is unavailable or inconsistent.");
        }

        try
        {
            closure = await CompositionDeclarationClosure.LoadForConsumerAsync(new CompositionDeclarationOptions(mapping, plan.PlatformDeclarationPaths) { ActiveModule = plan.PlatformActiveModule, QualificationOwners = plan.QualificationOwners ?? [], Workspace = plan.Workspace, ConsumerProjectName = CompositionDeclarationOptions.PlatformCompositionProjectName }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Composition or suite sources changed before handoff.");
        }

        string composition = CompositionIdentity(closure, selected, plan.QualificationOwners);
        if (composition != plan.CompositionContentIdentity || !selected.MatchesSource(plan.PlatformDeclarationPaths[0]))
        {
            throw new InvalidDataException("Composition or suite sources changed before handoff.");
        }

        string instance = plan.EnvironmentId + "-catalog";
        object unavailable = new { state = "not-yet-populated" };
        DateTimeOffset startupOrigin = plan.StartupDeadlineUtc.Value - plan.EffectiveDeadline.Timeout;
        object startupOverride = plan.EffectiveDeadline.IsOverride
            ? new { state = "declared", requestedSeconds = plan.EffectiveDeadline.TimeoutSeconds, justification = plan.EffectiveDeadline.Justification }
            : new { state = "not-overridden" };
        List<object> diagnostics = [];
        if (plan.ExecutingTool is { } executor)
        {
            diagnostics.Add(await executor.RetainAsync(attached?.DiagnosticsDirectory ?? ownerDiagnosticsDirectory ?? plan.Workspace, cancellationToken).ConfigureAwait(false));
        }

        object descriptor = new
        {
            schema = TestEnvironmentContractReader.DescriptorSchema,
            policyId = TestEnvironmentContractReader.PolicyId,
            bindings = new
            {
                run = attached is null
                    ? new { runId = plan.RunId, attemptId = plan.AttemptId, attemptSequence = plan.CandidatePolicy?.AttemptSequence ?? 1, ownerRunId = plan.RunId, environmentId = plan.EnvironmentId, role = "owner" }
                    : AttachedRun(attached.RunId, attached.AttemptId, plan.RunId, plan.EnvironmentId),
                environment = new
                {
                    composition = new { id = plan.PlatformActiveModule ?? selected.ModuleId, contentIdentity = composition },
                    mode = selected.Mode,
                    executionContext = ExecutionContext(),
                    catalog = new { commit = catalog.CatalogCommit, instanceId = instance },
                    isolation = new { state = "populated", syntheticTenantId = plan.TenantNamespace, dataIsolationId = plan.ResourceNamespace, resourceScopeId = plan.EnvironmentId, catalogScopeId = instance },
                    resources = ready.Resources.Select(resource => new { resourceId = resource.Name, moduleId = resource.ModuleId, endpoints = resource.Endpoints.Select(endpoint => new { name = endpoint.Name, protocol = endpoint.Protocol, uri = endpoint.Uri.AbsoluteUri }) }),
                    readiness = new { state = "populated", status = "ready", observedAt = Utc(ready.ObservedAt.Value), resources = ready.Resources.Select(resource => new { resourceId = resource.Name, status = "ready", observedAt = Utc(resource.ObservedAt!.Value), diagnosticIds = Array.Empty<string>() }) },
                    startupTasks = new { state = "populated", tasks = ready.StartupTaskResults.Select(task => new { taskId = task.TaskId, required = true, status = task.Status, startedAt = Utc(task.StartedAt!.Value), finishedAt = Utc(task.FinishedAt!.Value), diagnosticIds = Array.Empty<string>() }) },
                },
                tool = plan.ExecutingTool?.ToContract() ?? unavailable,
                candidate = CandidateBinding(plan, attached),
                decision = attached is null && plan.CandidatePolicy is { } decision ? decision.Decision : unavailable,
                reuseLineage = attached is null && plan.CandidatePolicy is { } lineage ? lineage.ReuseLineage : unavailable,
                artifacts = new { expected = unavailable, loaded = unavailable },
                suite = new { declared = unavailable, completion = unavailable },
                attachments = System.Text.Json.Nodes.JsonNode.Parse(CompositionAttachmentStore.ReadContractBinding(ownerDiagnosticsDirectory, plan.RunId, DateTimeOffset.UtcNow).ToJsonString()),
                deadlines = new
                {
                    // Attached suites bind the owner's measured environment startup, excluding attachment preparation.
                    startup = new { state = "populated", origin = "environment-requested", originAt = Utc(startupOrigin), effectiveSeconds = plan.EffectiveDeadline.TimeoutSeconds, expiresAt = Utc(plan.StartupDeadlineUtc.Value), actualDurationSeconds = ready.StartupDurationSeconds.Value, @override = startupOverride },
                    execution = new { state = "populated", origin = "test-execution-started", originAt = Utc(timing.StartedAtUtc), effectiveSeconds = timing.EffectiveSeconds, expiresAt = Utc(timing.DeadlineUtc), configurationContentIdentity = timing.ConfigurationContentIdentity },
                },
            },
            diagnostics,
        };
        string path = attached?.DescriptorPath ?? Path.GetFullPath(Path.Combine(plan.Workspace, "test-environment.json"));
        await CompositionDocumentStore.WriteAsync(path, descriptor, cancellationToken).ConfigureAwait(false);
        TestEnvironmentContractReadResult validated = TestEnvironmentContractReader.ReadDescriptor(path, cancellationToken);
        if (!validated.IsValid)
        {
            File.Delete(path);
            throw new InvalidDataException("The generated environment descriptor failed contract validation.");
        }

        return path;
    }

    /// <summary>Hashes the actual authoritative closure and selected resource/runtime requirements.</summary>
    /// <param name="declarations">The authoritative declaration closure.</param>
    /// <param name="selected">The optional selected suite.</param>
    /// <param name="qualificationOwners">The explicit additional owners, when requested.</param>
    /// <returns>The deterministic composition content identity.</returns>
    internal static string CompositionIdentity(IReadOnlyList<PlatformModuleDeclaration> declarations, ModuleTestSelection? selected, IReadOnlyList<string>? qualificationOwners = null)
    {
        IReadOnlyList<string> owners = CompositionQualificationOwners.Normalize(qualificationOwners);
        string original = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            modules = declarations.OrderBy(declaration => declaration.Declaration.GetProperty("identity").GetProperty("moduleId").GetString(), StringComparer.Ordinal).Select(declaration => new { declaration.Declaration, declaration.SourceHash }),
            selected?.Mode,
            selected?.DaprRuntimeVersion,
            retention = selected is { ModuleId: "memories", Name: "functional-retention" },
        })));
        return owners.Count == 0
            ? original
            : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { purpose = "hexalith.qualification-owners-composition.v1", original, owners })));
    }

    /// <summary>Refuses handoff configurations the v1 producer cannot represent before provisioning.</summary>
    /// <param name="deadline">The resolved actual startup policy.</param>
    /// <exception cref="InvalidDataException">The fallback, override or actual execution context is unsupported.</exception>
    internal static void ValidatePreProvisioning(CompositionEffectiveDeadline deadline)
    {
        if (!deadline.IsOverride && deadline.Timeout != TimeSpan.FromSeconds(SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds))
        {
            throw new InvalidDataException("Declared suite handoff requires the 600-second startup default or an existing justified lifecycle.startup.override declaration.");
        }

        if (deadline.IsOverride && string.IsNullOrWhiteSpace(deadline.Justification))
        {
            throw new InvalidDataException("Declared suite handoff requires justification for its lifecycle.startup.override declaration.");
        }

        _ = ExecutionContext();
    }

    /// <summary>Reports whether <see cref="ExecutionContext"/> records a local context for owners started by this process.</summary>
    /// <returns>Whether neither GitHub Actions nor CI execution is declared.</returns>
    internal static bool IsLocalExecution() =>
        !string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Captures actual supported execution context without inventing CI metadata.</summary>
    /// <returns>The local or complete actual CI identity.</returns>
    /// <exception cref="InvalidDataException">Actual CI metadata is incomplete or unsupported.</exception>
    internal static object ExecutionContext()
    {
        if (IsLocalExecution())
        {
            return new { kind = "local" };
        }

        bool github = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);

        string? repository = Environment.GetEnvironmentVariable("GITHUB_REPOSITORY");
        string? run = Environment.GetEnvironmentVariable("GITHUB_RUN_ID");
        string? job = Environment.GetEnvironmentVariable("GITHUB_JOB");
        string? os = Environment.GetEnvironmentVariable("RUNNER_OS");
        string? runner = Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT");
        bool validAttempt = int.TryParse(Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), NumberStyles.None, CultureInfo.InvariantCulture, out int attempt) && attempt > 0;
        bool incomplete = !github || !ValidIdentity(repository) || !ValidIdentity(run) || !ValidIdentity(job) || !ValidIdentity(os)
            || runner is not ("github-hosted" or "self-hosted") || !validAttempt;
        string runnerKind = runner == "github-hosted" ? "github-hosted" : "other";
        return incomplete
            ? throw new InvalidDataException("Declared suite handoff supports local execution or complete actual GitHub Actions metadata: repository, run ID, positive attempt, job, runner OS and a github-hosted or self-hosted runner environment are required.")
            : new { kind = "ci", repository, workflowRunId = run, workflowAttempt = attempt, jobId = job, runnerKind, operatingSystem = os, disposable = runner == "github-hosted" };
    }

    /// <summary>Resolves attacher job identity from this process, or from issued owner state when GitHub variables are absent.</summary>
    /// <returns>The process or issued owner execution context.</returns>
    /// <exception cref="InvalidDataException">Neither complete process metadata nor verified issued owner context is available.</exception>
    internal static JsonElement ResolveAttacherContext()
    {
        JsonElement? issued = null;
        InvalidDataException? issuedError = null;
        if (HandoffDeclared())
        {
            try
            {
                issued = ReadIssuedOwnerContext();
            }
            catch (InvalidDataException exception)
            {
                issuedError = exception;
            }
        }

        try
        {
            JsonElement process = JsonSerializer.SerializeToElement(ExecutionContext());
            return IsLocalExecution() && issued is { } recorded && Kind(recorded) == "ci" ? recorded : process;
        }
        catch (InvalidDataException processError)
        {
            return issued ?? throw (issuedError ?? processError);
        }
    }

    private static bool HandoffDeclared() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEXALITH_TEST_ENVIRONMENT_PATH"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEXALITH_G4_PLAN_PATH"))
        || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEXALITH_G4_RUN_ID"));

    private static JsonElement ReadIssuedOwnerContext()
    {
        string runId = Required("HEXALITH_G4_RUN_ID");
        if (runId.Length != 32 || runId.Any(value => value is not ((>= '0' and <= '9') or (>= 'a' and <= 'f'))))
        {
            throw new InvalidDataException("Issued owner run identity is malformed.");
        }

        string descriptorPath = Required("HEXALITH_TEST_ENVIRONMENT_PATH");
        string planPath = Required("HEXALITH_G4_PLAN_PATH");
        using JsonDocument descriptor = ReadDocument(descriptorPath);
        using JsonDocument plan = ReadDocument(planPath);
        JsonElement run = Property(descriptor.RootElement, "bindings", "run");
        JsonElement context = Property(descriptor.RootElement, "bindings", "environment", "executionContext");
        string workspace = Path.GetDirectoryName(Path.GetFullPath(descriptorPath))
            ?? throw new InvalidDataException("Issued owner descriptor has no workspace.");
        RequireEqual(Path.GetFullPath(descriptorPath), Path.Combine(workspace, "test-environment.json"));
        RequireEqual(Path.GetFullPath(planPath), Path.Combine(workspace, "plan.json"));
        RequireEqual(Text(plan.RootElement, "schema"), "hexalith.g4-run-plan.v1");
        RequireEqual(Text(plan.RootElement, "runId"), runId);
        RequireEqual(Text(plan.RootElement, "workspace"), workspace);
        RequireEqual(Text(run, "runId"), runId);
        RequireEqual(Text(run, "ownerRunId"), runId);
        RequireEqual(Text(run, "role"), "owner");
        string stateDirectory = Text(plan.RootElement, "resourceClaimStateDirectory")
            ?? throw new InvalidDataException("Issued owner plan has no resource claim state directory.");
        using JsonDocument state = ReadDocument(Path.Combine(stateDirectory, runId + ".json"));
        RequireEqual(Text(state.RootElement, "schema"), "hexalith.g4-run-state.v1");
        RequireEqual(Text(state.RootElement, "runId"), runId);
        RequireEqual(Text(state.RootElement, "workspace"), workspace);
        foreach (string name in new[] { "attemptId", "environmentId" })
        {
            string value = Text(run, name) ?? throw new InvalidDataException("Issued owner run binding is incomplete.");
            RequireEqual(Text(state.RootElement, name), value);
            RequireEqual(Text(plan.RootElement, name), value);
        }

        JsonElement recorded = Property(state.RootElement, "executionContext");
        if (!JsonElement.DeepEquals(context, recorded))
        {
            throw new InvalidDataException("Issued owner descriptor and state execution contexts differ.");
        }

        VerifyIssuedContext(recorded);
        VerifyExecutingTool(plan.RootElement, state.RootElement);
        return recorded.Clone();
    }

    private static void VerifyIssuedContext(JsonElement context)
    {
        string? kind = Kind(context);
        string[] names = kind == "local"
            ? ["kind"]
            : ["kind", "repository", "workflowRunId", "workflowAttempt", "jobId", "runnerKind", "operatingSystem", "disposable"];
        if (context.ValueKind != JsonValueKind.Object
            || !context.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(names.Order(StringComparer.Ordinal))
            || kind is not ("local" or "ci"))
        {
            throw new InvalidDataException("Issued owner execution context has unexpected fields.");
        }

        if (kind == "local")
        {
            return;
        }

        RequireEqual(Text(context, "runnerKind"), "github-hosted");
        RequireEqual(Text(context, "operatingSystem"), "Linux");
        if (!context.TryGetProperty("disposable", out JsonElement disposable) || disposable.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("Issued owner CI context is not disposable.");
        }

        if (!context.TryGetProperty("workflowAttempt", out JsonElement attempt) || attempt.ValueKind != JsonValueKind.Number
            || attempt.GetInt32() <= 0
            || string.IsNullOrWhiteSpace(Text(context, "repository"))
            || string.IsNullOrWhiteSpace(Text(context, "workflowRunId"))
            || string.IsNullOrWhiteSpace(Text(context, "jobId")))
        {
            throw new InvalidDataException("Issued owner CI context is incomplete.");
        }
    }

    private static void VerifyExecutingTool(JsonElement plan, JsonElement state)
    {
        string? planIdentity = Text(plan, "executingTool", "contentIdentity");
        string? stateIdentity = Text(state, "executingTool", "contentIdentity");
        if (planIdentity is not null && stateIdentity is not null)
        {
            RequireEqual(planIdentity, stateIdentity);
        }
    }

    private static JsonDocument ReadDocument(string path)
    {
        FileInfo file = new(Path.GetFullPath(path));
        if (!Path.IsPathFullyQualified(file.FullName) || file.LinkTarget is not null || !file.Exists || file.Length is <= 0 or > 4_194_304)
        {
            throw new InvalidDataException("Issued owner metadata must be a bounded regular non-linked file.");
        }

        for (DirectoryInfo? parent = file.Directory; parent is not null; parent = parent.Parent)
        {
            if (parent.LinkTarget is not null)
            {
                throw new InvalidDataException("Issued owner metadata cannot traverse links.");
            }
        }

        return JsonDocument.Parse(File.ReadAllBytes(file.FullName));
    }

    private static JsonElement Property(JsonElement root, params string[] names)
    {
        JsonElement current = root;
        foreach (string name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                throw new InvalidDataException("Issued owner metadata is missing a required binding.");
            }
        }

        return current;
    }

    private static string? Kind(JsonElement context) => Text(context, "kind");

    private static string? Text(JsonElement element, params string[] names)
    {
        JsonElement current = element;
        foreach (string name in names)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(name, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidDataException("Issued owner handoff is incomplete.");

    private static void RequireEqual(string? actual, string? expected)
    {
        if (string.IsNullOrEmpty(actual) || actual != expected)
        {
            throw new InvalidDataException("Issued owner metadata binding mismatch.");
        }
    }

    private static object CandidateBinding(CompositionRunPlan plan, AttachedSuiteIdentity? attached) => (attached, plan.CandidatePolicy) switch
    {
        (not null, _) => AttachedCandidate(),
        (null, { } candidate) => candidate.Candidate,
        _ => new { state = "populated", classification = "baseline-only", reason = "No authenticated candidate request was selected." },
    };

    private static bool SafeEndpoint(CompositionResourceEndpoint endpoint) => endpoint.Uri.IsAbsoluteUri
        && Uri.CheckHostName(endpoint.Uri.Host) != UriHostNameType.Unknown
        && (endpoint.Uri.IsLoopback || endpoint.Uri.Host == "localhost")
        && endpoint.Uri.UserInfo.Length == 0 && endpoint.Uri.Query.Length == 0 && endpoint.Uri.Fragment.Length == 0
        && !ManifestSecretDetector.ContainsSecret(endpoint.Uri.AbsoluteUri);

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static bool ValidIdentity(string? value) => value is { Length: > 0 and <= 2048 } && value == value.Trim()
        && !value.Any(character => character is <= '\u001f' or (>= '\u007f' and <= '\u009f'))
        && !ManifestSecretDetector.ContainsSecret(value);
}