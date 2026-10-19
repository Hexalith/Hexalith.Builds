// <copyright file="CompositionEngineOptions.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>
/// Options for the runner composition engine.
/// </summary>
/// <param name="AppHostAssemblyPath">The absolute path of the built Builds-owned AppHost assembly.</param>
/// <param name="DescriptorChildEntryAssemblyPath">The absolute path of the runner assembly hosting the descriptor child command.</param>
public sealed record CompositionEngineOptions(string AppHostAssemblyPath, string DescriptorChildEntryAssemblyPath)
{
    /// <summary>Gets the original command request time, including source mapping and other setup.</summary>
    public DateTimeOffset? StartupRequestedAtUtc { get; init; }

    /// <summary>Gets the already-resolved workspace source mapping.</summary>
    public SourceMapping? SourceMapping { get; init; }

    /// <summary>
    /// Gets the verified Dapr home, or null to read <c>HEXALITH_DAPR_HOME</c>.
    /// </summary>
    public string? DaprHome { get; init; }

    /// <summary>
    /// Gets the NuGet global packages folder holding the catalog Aspire DCP and dashboard packages, or null for
    /// <c>NUGET_PACKAGES</c> or the user default.
    /// </summary>
    public string? AspirePackagesRoot { get; init; }

    /// <summary>Gets the Aspire CLI executable used for the startup version probe.</summary>
    public string AspireCommand { get; init; } = "aspire";

    /// <summary>Gets the finite bound on the Aspire version probe.</summary>
    public TimeSpan AspireProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Gets the Docker CLI command.
    /// </summary>
    public string DockerCommand { get; init; } = "docker";

    /// <summary>
    /// Gets the per-user run-state directory.
    /// </summary>
    public string StateDirectory { get; init; } = CompositionRunStateStore.DefaultDirectory();

    /// <summary>
    /// Gets the per-user root of run workspaces.
    /// </summary>
    public string WorkspaceRoot { get; init; } = CompositionWorkspace.DefaultRoot();

    /// <summary>
    /// Gets the startup deadline used when no module declares a finite override. The clock starts at the start request.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds);

    /// <summary>
    /// Gets a value indicating whether this start is <c>debug</c>: source projects build in Debug and process ids are reported.
    /// </summary>
    public bool DebugAttach { get; init; }

    /// <summary>
    /// Gets the bound on a graceful AppHost stop before the process tree is killed.
    /// </summary>
    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>
    /// Gets an optional absolute file that mirrors AppHost and resource console output for local diagnosis. Null discards it.
    /// </summary>
    public string? AppHostLogPath { get; init; }

    /// <summary>
    /// Gets a value indicating whether the AppHost stays up after the public run command exits.
    /// </summary>
    public bool PersistAfterParentExit { get; init; }

    /// <summary>
    /// Gets a value indicating whether the full persisted profile starts a second EventStore instance.
    /// </summary>
    public bool EnableSecondEventStoreInstance { get; init; }

    /// <summary>Gets the immutable selected module-owned test binding, when present.</summary>
    public ModuleTestSelection? SelectedTest { get; init; }

    /// <summary>Gets a value indicating whether local selected-test failures retain surviving resources until explicit down.</summary>
    public bool RetainOnFailure { get; init; }

    /// <summary>Gets the owner's cancellation separately from the finite selected execution clock.</summary>
    internal CancellationToken? SelectedExecutionOwnerToken { get; init; }

    /// <summary>Gets a test-only profile execution replacement for process-boundary verification.</summary>
    internal Func<CompositionRunSession, PersistedProfileDefinition, CancellationToken, Task<Diagnostics.ToolCommandResult>>? ProfileExecutorOverride { get; init; }

    /// <summary>Gets a test-only native dotnet command replacement.</summary>
    internal string? NativeDotnetCommand { get; init; }

    /// <summary>Gets an offline replacement for the existing root-owned manifest check.</summary>
    internal Func<string, CancellationToken, Task<bool>>? RootManifestCheckOverride { get; init; }

    /// <summary>Gets an offline replacement for the existing source-mapping resolver.</summary>
    internal Func<string, WorkspaceMode, CancellationToken, Task<SourceMapping>>? MappingResolverOverride { get; init; }

    /// <summary>Gets a test-only replacement for the selected v2 session start boundary.</summary>
    internal Func<string, ModuleTestSelection, CancellationToken, Task<CompositionStartResult>>? SelectedPlatformStartOverride { get; init; }

    /// <summary>Gets a test-only CI policy override.</summary>
    internal bool? ContinuousIntegrationOverride { get; init; }

    /// <summary>Gets a test-only boundary immediately before the final ready deadline check.</summary>
    internal Func<CancellationToken, Task>? BeforeReadyCommitOverride { get; init; }

    /// <summary>Gets a test-only replacement for the v2 session start boundary.</summary>
    internal Func<string, CancellationToken, Task<CompositionStartResult>>? PlatformStartOverride { get; init; }

    /// <summary>Gets a test-only replacement for compiling the Platform model.</summary>
    internal Func<SourceMapping, string, CancellationToken, Task<string>>? PlatformModelBuildOverride { get; init; }

    /// <summary>Gets a test-only replacement for building the run-scoped McpCli tool.</summary>
    internal Func<SourceMapping, string, CancellationToken, Task<string>>? McpCliBuildOverride { get; init; }

    /// <summary>Gets a test-only replacement for bounded v2 session cleanup.</summary>
    internal Func<CompositionRunSession, CancellationToken, Task<CompositionDownResult>>? PlatformDownOverride { get; init; }
}
