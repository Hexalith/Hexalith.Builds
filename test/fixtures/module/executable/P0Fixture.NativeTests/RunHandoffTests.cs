// <copyright file="RunHandoffTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.P0Fixture.NativeTests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Shouldly;

using Xunit;

/// <summary>
/// Product-side assertions that consume only the runner handoff; the runner owns the topology.
/// </summary>
public sealed class RunHandoffTests
{
    /// <summary>
    /// Checks the persisted boundary rejects an unauthenticated command.
    /// </summary>
    /// <returns>A task that completes after the request.</returns>
    [Fact]
    public async Task AnonymousCommandIsRejectedAsync()
    {
        RunHandoff handoff = RunHandoff.Read();
        using HttpClient client = new() { BaseAddress = handoff.EventStore, Timeout = TimeSpan.FromSeconds(30) };

        using JsonContent command = Command(handoff);
        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/commands", UriKind.Relative),
            command,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Checks an authenticated command submitted through the handoff completes.
    /// </summary>
    /// <returns>A task that completes after the command status is terminal.</returns>
    [Fact]
    public async Task AuthenticatedCommandCompletesAsync()
    {
        RunHandoff handoff = RunHandoff.Read();
        using HttpClient client = new() { BaseAddress = handoff.EventStore, Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", handoff.AccessToken);
        string messageId = Guid.NewGuid().ToString("N");

        using JsonContent command = Command(handoff, messageId);
        using HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/v1/commands", UriKind.Relative),
            command,
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await TerminalStatusAsync(client, messageId).ConfigureAwait(true)).ShouldBe("Completed");
    }

    private static JsonContent Command(RunHandoff handoff, string? messageId = null) => JsonContent.Create(new
    {
        messageId = messageId ?? Guid.NewGuid().ToString("N"),
        tenant = handoff.Tenant,
        domain = "p0-orders",
        aggregateId = handoff.ResourceNamespace + "-native",
        commandType = "PlaceOrder",
        payload = new { quantity = 1 },
    });

    private static async Task<string?> TerminalStatusAsync(HttpClient client, string messageId)
    {
        string? status = null;
        for (int attempt = 0; attempt < 120; attempt++)
        {
            using HttpResponseMessage response = await client.GetAsync(
                new Uri("/api/v1/commands/status/" + messageId, UriKind.Relative),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            if (response.IsSuccessStatusCode)
            {
                using JsonDocument document = JsonDocument.Parse(
                    await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken).ConfigureAwait(true));
                status = document.RootElement.GetProperty("status").GetString();
                if (status is "Completed" or "Rejected" or "PublishFailed" or "TimedOut")
                {
                    return status;
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        return status;
    }
}