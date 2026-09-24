// <copyright file="CompositionResourceController.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;

/// <summary>
/// Sends bounded resource commands to the AppHost of one in-memory test session.
/// </summary>
public static class CompositionResourceController
{
    /// <summary>
    /// Stops or starts the run's EventStore instance while its Redis store and peer remain available.
    /// </summary>
    /// <param name="session">The owning live session.</param>
    /// <param name="command">Either stop-eventstore or start-eventstore.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>True only when the AppHost confirms the required resource state.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The command is not supported.</exception>
    public static async Task<bool> ExecuteAsync(
        CompositionRunSession session,
        string command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (command is not ("stop-eventstore" or "start-eventstore"))
        {
            throw new ArgumentOutOfRangeException(nameof(command));
        }

        string workspace = session.Plan.Workspace;
        string requestPath = CompositionControlRequest.PathFor(workspace);
        string responsePath = CompositionControlResponse.PathFor(workspace);
        if (File.Exists(responsePath))
        {
            File.Delete(responsePath);
        }

        string nonce = CompositionRunPlanFactory.NewRunId();
        await CompositionDocumentStore.WriteAsync(
            requestPath,
            new CompositionControlRequest(CompositionControlRequest.SupportedSchema, session.RunId, nonce, command),
            cancellationToken).ConfigureAwait(false);

        Stopwatch elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(90))
        {
            cancellationToken.ThrowIfCancellationRequested();
            CompositionControlResponse? response = await CompositionDocumentStore
                .TryReadAsync<CompositionControlResponse>(responsePath, cancellationToken).ConfigureAwait(false);
            if (response is not null)
            {
                bool matches = string.Equals(response.Schema, CompositionControlRequest.SupportedSchema, StringComparison.Ordinal)
                    && string.Equals(response.RunId, session.RunId, StringComparison.Ordinal)
                    && string.Equals(response.Nonce, nonce, StringComparison.Ordinal)
                    && string.Equals(response.Command, command, StringComparison.Ordinal);
                if (matches && !response.Success)
                {
                    string detail = response.FailureStep is "project-stop" or "sidecar-stop" or "stopped-state" or "project-start" or "sidecar-start" or "resource-command" or "invalid-request"
                        ? response.FailureStep
                        : "resource-command";
                    await Console.Error.WriteLineAsync("g4-resource-control: " + detail).ConfigureAwait(false);
                }

                return matches && response.Success;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }
}
