// <copyright file="CompositionEngineOptions.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Options for the runner composition engine.
/// </summary>
/// <param name="AppHostAssemblyPath">The absolute path of the built Builds-owned AppHost assembly.</param>
/// <param name="DescriptorChildEntryAssemblyPath">The absolute path of the runner assembly hosting the descriptor child command.</param>
public sealed record CompositionEngineOptions(string AppHostAssemblyPath, string DescriptorChildEntryAssemblyPath)
{
    /// <summary>
    /// Gets the verified Dapr home, or null to read <c>HEXALITH_DAPR_HOME</c>.
    /// </summary>
    public string? DaprHome { get; init; }

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
    /// Gets the bound on AppHost readiness.
    /// </summary>
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromMinutes(5);

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
}
