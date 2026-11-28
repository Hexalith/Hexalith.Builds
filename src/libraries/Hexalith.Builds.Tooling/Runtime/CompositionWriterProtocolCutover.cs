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

    private const string _operatorTokenStatus = "operator-token-unavailable;operator=";

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
    public static Task ActivateAsync(Func<CancellationToken, Task<Uri>> endpointResolver, string runId, string signingKey, CancellationToken cancellationToken)
        => ActivateAsync(endpointResolver, runId, signingKey, TimeSpan.FromMinutes(2), cancellationToken);

    /// <summary>Activates the writer protocol within the remaining declared startup deadline.</summary>
    /// <param name="endpointResolver">Resolves the allocated gateway endpoint.</param>
    /// <param name="runId">The isolated fresh-store run identity.</param>
    /// <param name="signingKey">The in-memory operator signing key.</param>
    /// <param name="activationTimeout">The remaining owning startup bound.</param>
    /// <param name="cancellationToken">The owning startup cancellation.</param>
    /// <returns>A task that completes once the protocol is active.</returns>
    /// <exception cref="TimeoutException">The declared startup bound expired before activation.</exception>
    public static Task ActivateAsync(Func<CancellationToken, Task<Uri>> endpointResolver, string runId, string signingKey, TimeSpan activationTimeout, CancellationToken cancellationToken)
        => ActivateAsync(endpointResolver, runId, signingKey, null, activationTimeout, cancellationToken);

    /// <summary>Activates the writer protocol with the model's operator token source, or the per-run signing key without one.</summary>
    /// <param name="endpointResolver">Resolves the allocated gateway endpoint.</param>
    /// <param name="runId">The isolated fresh-store run identity.</param>
    /// <param name="signingKey">The in-memory operator signing key, used only when no token source is registered.</param>
    /// <param name="tokenSource">The model-registered operator token source, or null for the signing-key token.</param>
    /// <param name="activationTimeout">The remaining owning startup bound.</param>
    /// <param name="cancellationToken">The owning startup cancellation.</param>
    /// <returns>A task that completes once the protocol is active.</returns>
    /// <exception cref="TimeoutException">The declared startup bound expired before activation.</exception>
    /// <exception cref="InvalidOperationException">The token source refused the operator, or the gateway refused activation.</exception>
    public static async Task ActivateAsync(
        Func<CancellationToken, Task<Uri>> endpointResolver,
        string runId,
        string signingKey,
        ICompositionOperatorTokenSource? tokenSource,
        TimeSpan activationTimeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpointResolver);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(activationTimeout);
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
                        string token;
                        if (tokenSource is null)
                        {
                            token = SigningKeyToken(signingKey);
                        }
                        else
                        {
                            // A refused or unreachable operator never falls back to the signing key.
                            lastStatus = OperatorTokenStatus(tokenSource.OperatorName);
                            token = await OperatorTokenAsync(tokenSource, lastStatus, probe).ConfigureAwait(false);
                        }

                        await ActivateProtocolAsync(client, runId, token, probe.Token).ConfigureAwait(false);
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

    /// <summary>Formats the support-safe status naming an operator whose token is unavailable.</summary>
    /// <param name="operatorName">The token source's operator principal name.</param>
    /// <returns>The bounded status, naming the operator only when it is a plain identifier.</returns>
    internal static string OperatorTokenStatus(string? operatorName) =>
        _operatorTokenStatus + (IsOperatorName(operatorName) ? operatorName : "unnamed");

    /// <summary>Tests whether a cutover failure status names an unavailable operator token.</summary>
    /// <param name="value">The failure status.</param>
    /// <returns>True for a refusal or deadline status with a plain operator identifier.</returns>
    internal static bool IsOperatorTokenFailure(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        string? prefix = new[] { "cutover-" + _operatorTokenStatus, "cutover-timeout;" + _operatorTokenStatus }
            .FirstOrDefault(candidate => value.StartsWith(candidate, StringComparison.Ordinal));
        return prefix is not null && IsOperatorName(value[prefix.Length..]);
    }

    private static bool IsOperatorName(string? value) =>
        value is { Length: > 0 and <= 64 }
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private static async Task<string> OperatorTokenAsync(ICompositionOperatorTokenSource tokenSource, string status, CancellationTokenSource probe)
    {
        string? token;
        try
        {
            token = await tokenSource.CreateGlobalAdministratorTokenAsync(probe.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!probe.IsCancellationRequested && exception is not OutOfMemoryException)
        {
            throw new InvalidOperationException("cutover-" + status, exception);
        }

        return string.IsNullOrWhiteSpace(token) ? throw new InvalidOperationException("cutover-" + status) : token;
    }

    private static string SigningKeyToken(string signingKey) =>
        CompositionTokenFactory.Create(
            signingKey,
            new CompositionTokenRequest("g4-runner-operator", [], [], [], GlobalAdministrator: true, TimeSpan.FromMinutes(5)),
            DateTimeOffset.UtcNow);

    private static bool OnlyWriterProtocolIsUnhealthy(string healthBody)
    {
        using JsonDocument document = JsonDocument.Parse(healthBody);
        if (!document.RootElement.TryGetProperty("results", out JsonElement results) || results.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        string[] unhealthy =
        [
            .. results.EnumerateObject()
                .Where(entry => entry.Value.TryGetProperty("status", out JsonElement status)
                    && string.Equals(status.GetString(), "Unhealthy", StringComparison.Ordinal))
                .Select(entry => entry.Name),
        ];
        return unhealthy.Length == 1 && string.Equals(unhealthy[0], _writerProtocolCheck, StringComparison.Ordinal);
    }

    private static async Task ActivateProtocolAsync(HttpClient client, string runId, string token, CancellationToken cancellationToken)
    {
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
