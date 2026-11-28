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
        return CompositionWriterProtocolCutover.ActivateAsync(
            token => RunEndpoints.WaitForAsync(app, RunTopology.EventStoreName, token),
            plan.RunId,
            signingKey,
            app.Services.GetService<ICompositionOperatorTokenSource>(),
            CompositionStartupClock.Remaining(plan.StartupDeadlineUtc, DateTimeOffset.UtcNow),
            cancellationToken);
    }
}
