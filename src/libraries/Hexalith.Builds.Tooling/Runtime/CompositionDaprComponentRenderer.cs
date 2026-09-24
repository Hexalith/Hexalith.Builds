// <copyright file="CompositionDaprComponentRenderer.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Text;

/// <summary>
/// Renders the per-run Dapr state store, pub/sub, and configuration documents. No module supplies them.
/// </summary>
public static class CompositionDaprComponentRenderer
{
    /// <summary>
    /// Renders the state-store component bound to the run-scoped Redis endpoint.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <returns>The YAML document.</returns>
    public static string RenderStateStore(CompositionRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Lines(
            "apiVersion: dapr.io/v1alpha1",
            "kind: Component",
            "metadata:",
            "  name: statestore",
            "spec:",
            "  type: state.redis",
            "  version: v1",
            "  metadata:",
            "    - name: redisHost",
            $"      value: \"{RedisHost(plan)}\"",
            "    - name: redisPassword",
            "      value: \"\"",
            "    - name: actorStateStore",
            "      value: \"true\"",
            "    - name: keyPrefix",
            "      value: \"none\"",
            "scopes:",
            "  - eventstore");
    }

    /// <summary>
    /// Renders the pub/sub component bound to the run-scoped Redis endpoint.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <returns>The YAML document.</returns>
    public static string RenderPubSub(CompositionRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return Lines(
            "apiVersion: dapr.io/v1alpha1",
            "kind: Component",
            "metadata:",
            "  name: pubsub",
            "spec:",
            "  type: pubsub.redis",
            "  version: v1",
            "  metadata:",
            "    - name: redisHost",
            $"      value: \"{RedisHost(plan)}\"",
            "    - name: redisPassword",
            "      value: \"\"",
            "scopes:",
            "  - eventstore");
    }

    /// <summary>
    /// Renders the Dapr configuration that gives the run its own name-resolution registry, so identical
    /// application identities in two concurrent runs never resolve to each other.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <returns>The YAML document.</returns>
    public static string RenderConfiguration(CompositionRunPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        string registry = Path.Combine(Path.GetDirectoryName(plan.DaprConfigPath)!, "name-resolution.db").Replace('\\', '/');
        return Lines(
            "apiVersion: dapr.io/v1alpha1",
            "kind: Configuration",
            "metadata:",
            $"  name: hexalith-g4-{plan.RunId}",
            "spec:",
            "  nameResolution:",
            "    component: \"sqlite\"",
            "    version: \"v1\"",
            "    configuration:",
            $"      connectionString: \"{registry}\"");
    }

    /// <summary>
    /// Writes every rendered document and the empty isolated resources directory.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the documents are written.</returns>
    public static async Task WriteAsync(CompositionRunPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _ = Directory.CreateDirectory(plan.IsolatedResourcesPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(plan.StateStoreComponentPath)!);
        UTF8Encoding encoding = new(false);
        await File.WriteAllTextAsync(plan.StateStoreComponentPath, RenderStateStore(plan), encoding, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(plan.PubSubComponentPath, RenderPubSub(plan), encoding, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(plan.DaprConfigPath, RenderConfiguration(plan), encoding, cancellationToken).ConfigureAwait(false);
    }

    private static string RedisHost(CompositionRunPlan plan) =>
        "localhost:" + plan.Ports.Redis.ToString(CultureInfo.InvariantCulture);

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
}