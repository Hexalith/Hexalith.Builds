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
    public static async Task<string> WriteAsync(CompositionRunSession session, string? ownerDiagnosticsDirectory, CancellationToken cancellationToken = default)
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
            closure = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, plan.PlatformDeclarationPaths) { ActiveModule = plan.PlatformActiveModule });
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException("Composition or suite sources changed before handoff.");
        }

        string composition = CompositionIdentity(closure, selected);
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
        object descriptor = new
        {
            schema = TestEnvironmentContractReader.DescriptorSchema,
            policyId = TestEnvironmentContractReader.PolicyId,
            bindings = new
            {
                run = new { runId = plan.RunId, attemptId = plan.AttemptId, attemptSequence = 1, ownerRunId = plan.RunId, environmentId = plan.EnvironmentId, role = "owner" },
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
                tool = unavailable,
                candidate = unavailable,
                decision = unavailable,
                reuseLineage = unavailable,
                artifacts = new { expected = unavailable, loaded = unavailable },
                suite = new { declared = unavailable, completion = unavailable },
                attachments = System.Text.Json.Nodes.JsonNode.Parse(CompositionAttachmentStore.ReadContractBinding(ownerDiagnosticsDirectory, plan.RunId, DateTimeOffset.UtcNow).ToJsonString()),
                deadlines = new
                {
                    startup = new { state = "populated", origin = "environment-requested", originAt = Utc(startupOrigin), effectiveSeconds = plan.EffectiveDeadline.TimeoutSeconds, expiresAt = Utc(plan.StartupDeadlineUtc.Value), actualDurationSeconds = ready.StartupDurationSeconds.Value, @override = startupOverride },
                    execution = new { state = "populated", origin = "test-execution-started", originAt = Utc(timing.StartedAtUtc), effectiveSeconds = timing.EffectiveSeconds, expiresAt = Utc(timing.DeadlineUtc), configurationContentIdentity = timing.ConfigurationContentIdentity },
                },
            },
            diagnostics = Array.Empty<object>(),
        };
        string path = Path.GetFullPath(Path.Combine(plan.Workspace, "test-environment.json"));
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
    /// <returns>The deterministic composition content identity.</returns>
    internal static string CompositionIdentity(IReadOnlyList<PlatformModuleDeclaration> declarations, ModuleTestSelection? selected) =>
        "sha256:" + Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            modules = declarations.OrderBy(declaration => declaration.Declaration.GetProperty("identity").GetProperty("moduleId").GetString(), StringComparer.Ordinal).Select(declaration => new { declaration.Declaration, declaration.SourceHash }),
            selected?.Mode,
            selected?.DaprRuntimeVersion,
            retention = selected is { ModuleId: "memories", Name: "functional-retention" },
        })));

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

    /// <summary>Captures actual supported execution context without inventing CI metadata.</summary>
    /// <returns>The local or complete actual CI identity.</returns>
    /// <exception cref="InvalidDataException">Actual CI metadata is incomplete or unsupported.</exception>
    internal static object ExecutionContext()
    {
        bool github = string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        bool ci = github || string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        if (!ci)
        {
            return new { kind = "local" };
        }

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