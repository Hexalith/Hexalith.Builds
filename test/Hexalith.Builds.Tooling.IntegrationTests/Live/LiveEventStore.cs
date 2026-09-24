// <copyright file="LiveEventStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Talks to one run's EventStore host with in-memory per-run identities.
/// </summary>
/// <param name="session">The ready session.</param>
internal sealed class LiveEventStore(CompositionRunSession session) : IDisposable
{
    private readonly HttpClient _client = new() { BaseAddress = session.Readiness.EventStoreEndpoint, Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>
    /// Mints an in-memory token for the run tenant with command and query permissions on both fixture domains.
    /// </summary>
    /// <param name="tenants">The granted tenants.</param>
    /// <param name="permissions">The granted permissions.</param>
    /// <param name="signingKey">An optional key override (for a wrong-key control).</param>
    /// <returns>The token.</returns>
    public string Token(IReadOnlyList<string> tenants, IReadOnlyList<string>? permissions = null, string? signingKey = null) =>
        CompositionTokenFactory.Create(
            signingKey ?? session.SigningKey,
            new CompositionTokenRequest(
                "g4-live-user",
                tenants,
                ["p0-orders", "p0-inventory"],
                permissions ?? ["command:submit", "query:read"],
                false,
                TimeSpan.FromMinutes(15)),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Submits one command.
    /// </summary>
    /// <param name="token">The bearer token, or null for an anonymous request.</param>
    /// <param name="tenant">The command tenant.</param>
    /// <param name="domain">The command domain.</param>
    /// <param name="aggregateId">The aggregate identity.</param>
    /// <param name="commandType">The command type.</param>
    /// <param name="quantity">The command quantity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code and message identity.</returns>
    public async Task<(HttpStatusCode Status, string MessageId)> SubmitAsync(
        string? token,
        string tenant,
        string domain,
        string aggregateId,
        string commandType,
        int quantity,
        CancellationToken cancellationToken)
    {
        string messageId = Guid.NewGuid().ToString();
        using HttpRequestMessage request = new(HttpMethod.Post, new Uri("/api/v1/commands", UriKind.Relative))
        {
            Content = JsonContent.Create(new
            {
                messageId,
                tenant,
                domain,
                aggregateId,
                commandType,
                payload = new { quantity },
            }),
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, messageId);
    }

    /// <summary>
    /// Submits one command and waits until it reaches a terminal status.
    /// </summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="tenant">The command tenant.</param>
    /// <param name="domain">The command domain.</param>
    /// <param name="aggregateId">The aggregate identity.</param>
    /// <param name="commandType">The command type.</param>
    /// <param name="quantity">The command quantity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The terminal status text.</returns>
    /// <exception cref="InvalidOperationException">The command was not accepted.</exception>
    public async Task<string> SubmitAndWaitAsync(
        string token,
        string tenant,
        string domain,
        string aggregateId,
        string commandType,
        int quantity,
        CancellationToken cancellationToken)
    {
        (HttpStatusCode status, string messageId) = await SubmitAsync(token, tenant, domain, aggregateId, commandType, quantity, cancellationToken).ConfigureAwait(false);
        if (status != HttpStatusCode.Accepted)
        {
            throw new InvalidOperationException($"The command was not accepted: {(int)status}.");
        }

        for (int attempt = 0; attempt < 120; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, new Uri("/api/v1/commands/status/" + messageId, UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                string? text = document.RootElement.GetProperty("status").GetString();
                if (text is "Completed" or "Rejected" or "PublishFailed" or "TimedOut")
                {
                    return text;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return "Pending";
    }

    /// <summary>
    /// Gets a relative path from the EventStore host.
    /// </summary>
    /// <param name="path">The relative path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status code.</returns>
    public async Task<HttpStatusCode> GetStatusAsync(string path, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _client.GetAsync(new Uri(path, UriKind.Relative), cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}