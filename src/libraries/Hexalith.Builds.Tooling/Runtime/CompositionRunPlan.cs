// <copyright file="CompositionRunPlan.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The metadata-only plan the runner hands to the Builds-owned AppHost for one run.
/// </summary>
/// <remarks>
/// The plan never contains a credential. The per-run signing key travels only through the AppHost
/// child-process environment and the in-memory <see cref="CompositionRunSession"/>.
/// </remarks>
/// <param name="Schema">The plan schema identity.</param>
/// <param name="RunId">The unique run identity carried by every resource.</param>
/// <param name="TenantNamespace">The run-unique tenant used for authorized writes.</param>
/// <param name="ForeignTenantNamespace">A run-unique tenant the run identity is never granted.</param>
/// <param name="ResourceNamespace">The run-unique resource namespace (container and aggregate prefix).</param>
/// <param name="Workspace">The absolute, runner-owned run workspace directory.</param>
/// <param name="DaprHome">The absolute verified Dapr home (CLI in <c>tools/dapr</c>, runtime in <c>.dapr/bin</c>).</param>
/// <param name="ReadinessPath">The absolute path at which the AppHost writes its readiness document.</param>
/// <param name="DaprConfigPath">The rendered per-run Dapr configuration (name resolution) path.</param>
/// <param name="StateStoreComponentPath">The rendered per-run state-store component path.</param>
/// <param name="PubSubComponentPath">The rendered per-run pub/sub component path.</param>
/// <param name="IsolatedResourcesPath">The empty per-run resources directory used by isolated module sidecars.</param>
/// <param name="RedisImage">The digest-pinned Redis image reference (<c>repository:tag@sha256:digest</c>).</param>
/// <param name="Ports">The allocated run-scoped ports.</param>
/// <param name="Modules">The deterministically ordered modules.</param>
/// <param name="UiMarkers">The deterministically ordered FrontComposer marker bindings.</param>
public sealed record CompositionRunPlan(
    string Schema,
    string RunId,
    string TenantNamespace,
    string ForeignTenantNamespace,
    string ResourceNamespace,
    string Workspace,
    string DaprHome,
    string ReadinessPath,
    string DaprConfigPath,
    string StateStoreComponentPath,
    string PubSubComponentPath,
    string IsolatedResourcesPath,
    string RedisImage,
    CompositionRunPorts Ports,
    IReadOnlyList<CompositionRunModule> Modules,
    IReadOnlyList<CompositionRunUiMarker> UiMarkers)
{
    /// <summary>
    /// The supported run-plan schema identity.
    /// </summary>
    public const string SupportedSchema = "hexalith.g4-run-plan.v1";

    /// <summary>
    /// Gets the Redis container name, which carries the run identity.
    /// </summary>
    public string RedisContainerName => $"hexalith-g4-{RunId}-redis";
}