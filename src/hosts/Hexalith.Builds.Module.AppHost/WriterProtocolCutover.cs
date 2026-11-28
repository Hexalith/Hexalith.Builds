// <copyright file="WriterProtocolCutover.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;

/// <summary>Preserves the runner's fresh-store writer-protocol startup operation.</summary>
internal static class WriterProtocolCutover
{
    /// <summary>Activates the protocol after the gateway's other dependencies are healthy.</summary>
    /// <remarks>An operator token source registered by the model supplies the bearer; otherwise the signing key mints it.</remarks>
    /// <param name="app">The distributed application.</param>
    /// <param name="plan">The isolated run plan.</param>
    /// <param name="signingKey">The in-memory operator signing key, used only without a model token source.</param>
    /// <param name="cancellationToken">The bounded readiness cancellation.</param>
    /// <returns>The activation task.</returns>
    public static Task ActivateAsync(DistributedApplication app, CompositionRunPlan plan, string signingKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(plan);
        return ActivateAsync(
            app.Services,
            token => RunEndpoints.WaitForAsync(app, RunTopology.EventStoreName, token),
            plan.RunId,
            plan.StartupDeadlineUtc,
            signingKey,
            cancellationToken);
    }

    /// <summary>Activates the protocol with the token source the model registered in the AppHost services, if any.</summary>
    /// <param name="services">The started application services.</param>
    /// <param name="endpointResolver">Resolves the allocated gateway endpoint.</param>
    /// <param name="runId">The isolated run identity.</param>
    /// <param name="startupDeadlineUtc">The run's startup deadline.</param>
    /// <param name="signingKey">The in-memory operator signing key, used only without a model token source.</param>
    /// <param name="cancellationToken">The bounded readiness cancellation.</param>
    /// <returns>The activation task.</returns>
    internal static Task ActivateAsync(
        IServiceProvider services,
        Func<CancellationToken, Task<Uri>> endpointResolver,
        string runId,
        DateTimeOffset? startupDeadlineUtc,
        string signingKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(services);
        return CompositionWriterProtocolCutover.ActivateAsync(
            endpointResolver,
            runId,
            signingKey,
            services.GetService<ICompositionOperatorTokenSource>(),
            CompositionStartupClock.Remaining(startupDeadlineUtc, DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
