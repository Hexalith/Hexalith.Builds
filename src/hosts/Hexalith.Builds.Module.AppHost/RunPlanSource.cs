// <copyright file="RunPlanSource.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Reads the runner-written run plan and the per-run signing key from the AppHost environment.
/// </summary>
internal static class RunPlanSource
{
    /// <summary>
    /// Loads and checks the run plan named by the runner.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The run plan.</returns>
    /// <exception cref="InvalidOperationException">The plan is missing, malformed, or not for this run.</exception>
    public static async Task<CompositionRunPlan> LoadAsync(CancellationToken cancellationToken)
    {
        string? path = Environment.GetEnvironmentVariable(CompositionEnvironment.PlanPath);
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("The runner did not name an absolute run plan.");
        }

        CompositionRunPlan? plan = await CompositionDocumentStore.TryReadAsync<CompositionRunPlan>(path, cancellationToken).ConfigureAwait(false);
        return plan is not null
            && string.Equals(plan.Schema, CompositionRunPlan.SupportedSchema, StringComparison.Ordinal)
            && string.Equals(plan.RunId, Environment.GetEnvironmentVariable(CompositionEnvironment.RunId), StringComparison.Ordinal)
            && CompositionRunPlanFactory.IsRunId(plan.RunId)
            ? plan
            : throw new InvalidOperationException("The run plan is malformed or does not describe this run.");
    }

    /// <summary>
    /// Reads the per-run signing key and removes it from this process environment, so the orchestrator and
    /// every resource it starts never inherit it. It reaches only the resources that validate tokens.
    /// </summary>
    /// <returns>The signing key.</returns>
    /// <exception cref="InvalidOperationException">The key is missing.</exception>
    public static string TakeSigningKey()
    {
        string? key = Environment.GetEnvironmentVariable(CompositionEnvironment.SigningKey);
        Environment.SetEnvironmentVariable(CompositionEnvironment.SigningKey, null);
        return string.IsNullOrWhiteSpace(key)
            ? throw new InvalidOperationException("The runner did not supply the per-run signing key.")
            : key;
    }

    /// <summary>
    /// Gets the verified Dapr CLI path.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <returns>The CLI path.</returns>
    public static string DaprCliPath(CompositionRunPlan plan) =>
        Path.Combine(plan.DaprHome, "tools", OperatingSystem.IsWindows() ? "dapr.exe" : "dapr");

    /// <summary>
    /// Gets a verified Dapr runtime binary path.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="name">The binary name.</param>
    /// <returns>The binary path.</returns>
    public static string DaprBinaryPath(CompositionRunPlan plan, string name) =>
        Path.Combine(plan.DaprHome, ".dapr", "bin", OperatingSystem.IsWindows() ? name + ".exe" : name);
}