// <copyright file="CompositionWriterProtocolCutover.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Performs the EventStore projection-delivery writer-protocol cutover for the run-scoped store.
/// </summary>
/// <remarks>
/// EventStore stays fail-closed until an operator activates the v2 writer protocol. A run-scoped Redis store
/// is created empty by this run and has no legacy writers, so the runner (the operator of the run) attests
/// quiescence and activates it once every other EventStore dependency is healthy.
/// </remarks>
public static class CompositionWriterProtocolCutover
{
    private const string _writerProtocolCheck = "projection-delivery-writer-protocol";

    private static readonly TimeSpan _retryDelay = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Activates the writer protocol once the EventStore endpoint is allocated and otherwise healthy.
    /// </summary>
    /// <param name="endpointResolver">Resolves the allocated gateway endpoint.</param>
    /// <param name="runId">The isolated fresh-store run identity.</param>
    /// <param name="signingKey">The per-run signing key used to mint the in-memory operator token.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the protocol is active.</returns>
    /// <exception cref="TimeoutException">The writer-protocol cutover exceeded its own bound.</exception>
    public static async Task ActivateAsync(Func<CancellationToken, Task<Uri>> endpointResolver, string runId, string signingKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpointResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromMinutes(2));
        string lastStatus = "endpoint-unallocated";
        try
        {
            Uri endpoint = await endpointResolver(probe.Token).ConfigureAwait(false);
            using HttpClient client = new() { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(10) };
            while (true)
            {
                probe.Token.ThrowIfCancellationRequested();
                try
                {
                    using HttpResponseMessage health = await client.GetAsync(new Uri("/health", UriKind.Relative), probe.Token).ConfigureAwait(false);
                    lastStatus = "health-HTTP-" + ((int)health.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    if (health.IsSuccessStatusCode)
                    {
                        return;
                    }

                    string body = await health.Content.ReadAsStringAsync(probe.Token).ConfigureAwait(false);
                    if (OnlyWriterProtocolIsUnhealthy(body))
                    {
                        await ActivateProtocolAsync(client, runId, signingKey, probe.Token).ConfigureAwait(false);
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    lastStatus = "endpoint-unavailable";
                }
                catch (JsonException)
                {
                    lastStatus = "health-body-incomplete";
                }
                catch (TaskCanceledException) when (!probe.IsCancellationRequested)
                {
                    lastStatus = "health-request-timeout";
                }

                await Task.Delay(_retryDelay, probe.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && probe.IsCancellationRequested)
        {
            throw new TimeoutException("cutover-timeout;" + lastStatus);
        }
    }

    private static bool OnlyWriterProtocolIsUnhealthy(string healthBody)
    {
        using JsonDocument document = JsonDocument.Parse(healthBody);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string[] unhealthy = [.. results.EnumerateObject()
            .Where(entry => entry.Value.TryGetProperty("status", out JsonElement status)
                && string.Equals(status.GetString(), "Unhealthy", StringComparison.Ordinal))
            .Select(entry => entry.Name)];
        return unhealthy.Length == 1 && string.Equals(unhealthy[0], _writerProtocolCheck, StringComparison.Ordinal);
    }

    private static async Task ActivateProtocolAsync(HttpClient client, string runId, string signingKey, CancellationToken cancellationToken)
    {
        string token = CompositionTokenFactory.Create(
            signingKey,
            new CompositionTokenRequest("g4-runner-operator", [], [], [], GlobalAdministrator: true, TimeSpan.FromMinutes(5)),
            DateTimeOffset.UtcNow);
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/admin/projections/delivery-writer-protocol/activate", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                CutoverCommit = "hexalith-g4-run-" + runId,
                BackupReference = "run-scoped-empty-store-" + runId,
                WritersQuiesced = true,
                RetryWorkersQuiesced = true,
                DowngradeProhibitedAcknowledged = true,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.OK)
        {
            throw new InvalidOperationException("cutover-activation-HTTP-" + ((int)response.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }
}
