// <copyright file="RetainedApplicationLifecycle.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>The AppHost's failure-retention wait and bounded stop orchestration, usable with inert offline delegates.</summary>
internal static class RetainedApplicationLifecycle
{
    /// <summary>Retains a failed local application until an explicit stop, then bounds its stop operation.</summary>
    /// <param name="plan">The selected run plan.</param>
    /// <param name="exitCode">The startup outcome.</param>
    /// <param name="stopRequested">Whether an explicit stop has already arrived.</param>
    /// <param name="waitForShutdown">The application's shutdown wait.</param>
    /// <param name="stopApplication">The application's actual stop operation.</param>
    /// <param name="stopToken">The explicit owner stop signal.</param>
    /// <returns>The orchestration task.</returns>
    internal static async Task StopAsync(CompositionRunPlan plan, int exitCode, bool stopRequested, Func<CancellationToken, Task> waitForShutdown, Func<CancellationToken, Task> stopApplication, CancellationToken stopToken)
    {
        try
        {
            if (exitCode != 0 && plan.RetainOnFailure && !stopRequested)
            {
                try
                {
                    await waitForShutdown(stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
                {
                    // Explicit down owns final cleanup after retention.
                }
            }
        }
        finally
        {
            using CancellationTokenSource bound = new(TimeSpan.FromSeconds(plan.SelectedTest?.StopTimeoutSeconds ?? 90));
            await stopApplication(bound.Token).WaitAsync(bound.Token).ConfigureAwait(false);
        }
    }
}
