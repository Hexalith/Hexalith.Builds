// <copyright file="PersistedProfileClient.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

/// <summary>
/// Uses the real authenticated EventStore gateway for profile writes and reads.
/// </summary>
internal sealed class PersistedProfileClient : IDisposable
{
    private readonly CompositionRunSession _session;

    private readonly HttpClient _primary;

    private readonly HttpClient? _second;

    /// <summary>Initializes a new instance of the <see cref="PersistedProfileClient"/> class.</summary>
    /// <param name="session">The owning session.</param>
    public PersistedProfileClient(CompositionRunSession session)
    {
        _session = session;
        _primary = new HttpClient { BaseAddress = session.Readiness.EventStoreEndpoint, Timeout = TimeSpan.FromSeconds(30) };
        _second = session.Plan.Ports.SecondEventStoreHttp is int port
            ? new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(30) }
            : null;
    }

    /// <summary>Gets a value indicating whether the peer host exists.</summary>
    public bool HasSecondInstance => _second is not null;

    /// <summary>Mints a run-scoped bearer token.</summary>
    /// <param name="tenants">The granted tenants.</param>
    /// <param name="permissions">The granted permissions.</param>
    /// <returns>The token.</returns>
    public string Token(IReadOnlyList<string> tenants, IReadOnlyList<string>? permissions = null) => CompositionTokenFactory.Create(
        _session.SigningKey,
        new CompositionTokenRequest(
            "g4-persisted-profile",
            tenants,
            [.. _session.Plan.Modules.Select(module => module.Domain)],
            permissions ?? ["command:submit", "query:read"],
            false,
            TimeSpan.FromMinutes(15)),
        DateTimeOffset.UtcNow);

    /// <summary>Submits an authenticated command.</summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The module domain.</param>
    /// <param name="aggregateId">The aggregate ID.</param>
    /// <param name="commandType">The command type.</param>
    /// <param name="quantity">The command quantity.</param>
    /// <param name="messageId">The stable message ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The gateway status.</returns>
    public async Task<HttpStatusCode> SubmitAsync(
        string? token,
        string tenant,
        string domain,
        string aggregateId,
        string commandType,
        int quantity,
        string messageId,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/commands")
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

        using HttpResponseMessage response = await _primary.SendAsync(request, cancellationToken).ConfigureAwait(false);
        return response.StatusCode;
    }

    /// <summary>Waits for a command to complete.</summary>
    /// <param name="token">The bearer token.</param>
    /// <param name="messageId">The message ID.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the command completed.</returns>
    public async Task<bool> WaitForCompletedAsync(string token, string messageId, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, "/api/v1/commands/status/" + messageId);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage response = await _primary.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                string? status = document.RootElement.GetProperty("status").GetString();
                if (string.Equals(status, "Completed", StringComparison.Ordinal))
                {
                    return true;
                }

                if (status is "Rejected" or "PublishFailed" or "TimedOut")
                {
                    return false;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>Reads a persisted projection through a selected host.</summary>
    /// <param name="secondInstance">Whether to use the peer host.</param>
    /// <param name="token">The bearer token.</param>
    /// <param name="tenant">The tenant.</param>
    /// <param name="domain">The module domain.</param>
    /// <param name="aggregateId">The aggregate ID.</param>
    /// <param name="projectionType">The projection type.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The status and payload.</returns>
    /// <exception cref="InvalidOperationException">The peer host is absent.</exception>
    public async Task<(HttpStatusCode Status, JsonElement Payload)> QueryAsync(
        bool secondInstance,
        string token,
        string tenant,
        string domain,
        string aggregateId,
        string projectionType,
        CancellationToken cancellationToken)
    {
        HttpClient client = secondInstance ? _second ?? throw new InvalidOperationException("The second EventStore instance is absent.") : _primary;
        using HttpRequestMessage request = new(HttpMethod.Post, "/api/v1/queries")
        {
            Content = JsonContent.Create(new
            {
                tenant,
                domain,
                aggregateId,
                queryType = "get-summary",
                projectionType,
                entityId = aggregateId,
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, default);
        }

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        JsonElement root = document.RootElement;
        return root.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.True
            && root.TryGetProperty("payload", out JsonElement payload)
                ? (response.StatusCode, payload.Clone())
                : (response.StatusCode, default);
    }

    /// <summary>Checks that the primary host stopped serving.</summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether it is unavailable.</returns>
    public async Task<bool> PrimaryIsUnavailableAsync(CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await _primary.GetAsync(new Uri("/health", UriKind.Relative), cancellationToken).ConfigureAwait(false);
            return !response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return true;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _primary.Dispose();
        _second?.Dispose();
    }
}
