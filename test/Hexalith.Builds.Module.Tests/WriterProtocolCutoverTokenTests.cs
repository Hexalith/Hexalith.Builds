// <copyright file="WriterProtocolCutoverTokenTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks the writer-protocol cutover bearer: the model token source when registered, otherwise the signing key.</summary>
public sealed class WriterProtocolCutoverTokenTests
{
    private const string _signingKey = "0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>Verifies a run without a model token source keeps the HS256 global-administrator bearer.</summary>
    /// <returns>The fallback assertions.</returns>
    [Fact]
    public async Task WithoutTokenSourceTheSigningKeyMintsTheOperatorBearerAsync()
    {
        await using FakeCutoverGateway gateway = FakeCutoverGateway.Start();

        await CompositionWriterProtocolCutover.ActivateAsync(_ => Task.FromResult(gateway.Endpoint), "run-1", _signingKey, null, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);

        string bearer = gateway.Authorizations.ShouldHaveSingleItem().ShouldNotBeNull();
        bearer.ShouldStartWith("Bearer ");
        string[] parts = bearer["Bearer ".Length..].Split('.');
        parts.Length.ShouldBe(3);
        using JsonDocument header = JsonDocument.Parse(Decode(parts[0]));
        header.RootElement.GetProperty("alg").GetString().ShouldBe("HS256");
        using JsonDocument payload = JsonDocument.Parse(Decode(parts[1]));
        payload.RootElement.GetProperty("role").GetString().ShouldBe("GlobalAdministrator");
        payload.RootElement.GetProperty("sub").GetString().ShouldBe("g4-runner-operator");
        string expected = Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(_signingKey), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1])))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        parts[2].ShouldBe(expected);
        gateway.Bodies.ShouldHaveSingleItem().ShouldContain("hexalith-g4-run-run-1");
    }

    /// <summary>Verifies a registered model token source supplies the bearer and the signing key is not used.</summary>
    /// <returns>The model token source assertions.</returns>
    [Fact]
    public async Task RegisteredTokenSourceSuppliesTheOperatorBearerAsync()
    {
        await using FakeCutoverGateway gateway = FakeCutoverGateway.Start();
        StubTokenSource source = new("platform-operator", _ => Task.FromResult("realm.operator.token"));

        await CompositionWriterProtocolCutover.ActivateAsync(_ => Task.FromResult(gateway.Endpoint), "run-2", _signingKey, source, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken).ConfigureAwait(true);

        source.Calls.ShouldBe(1);
        gateway.Authorizations.ShouldHaveSingleItem().ShouldBe("Bearer realm.operator.token");
    }

    /// <summary>Verifies a refusing token source fails the cutover, names the operator and never falls back to the signing key.</summary>
    /// <param name="refusal">The refusal shape.</param>
    /// <returns>The refusal assertions.</returns>
    [Theory]
    [InlineData("throw")]
    [InlineData("empty")]
    public async Task RefusedOperatorTokenFailsNamedWithoutFallbackAsync(string refusal)
    {
        await using FakeCutoverGateway gateway = FakeCutoverGateway.Start();
        StubTokenSource source = new("platform-operator", _ => refusal == "throw"
            ? Task.FromException<string>(new HttpRequestException("invalid_client"))
            : Task.FromResult(string.Empty));

        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => CompositionWriterProtocolCutover.ActivateAsync(
            _ => Task.FromResult(gateway.Endpoint), "run-3", _signingKey, source, TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        failure.Message.ShouldBe("cutover-operator-token-unavailable;operator=platform-operator");
        gateway.Authorizations.ShouldBeEmpty();
        RunPreparationDiagnostic.SupportSafeStatus(failure, "writer-cutover").ShouldBe("cutover-operator-token-unavailable;operator=platform-operator;stage=writer-cutover");
    }

    /// <summary>Verifies an unavailable operator token expires with the cutover bound and names the operator.</summary>
    /// <returns>The deadline assertions.</returns>
    [Fact]
    public async Task UnavailableOperatorTokenExpiresWithinTheBoundAsync()
    {
        await using FakeCutoverGateway gateway = FakeCutoverGateway.Start();
        StubTokenSource source = new("platform-operator", async token =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
            return "never";
        });
        System.Diagnostics.Stopwatch elapsed = System.Diagnostics.Stopwatch.StartNew();

        TimeoutException failure = await Should.ThrowAsync<TimeoutException>(() => CompositionWriterProtocolCutover.ActivateAsync(
            _ => Task.FromResult(gateway.Endpoint), "run-4", _signingKey, source, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ConfigureAwait(true);

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(15));
        failure.Message.ShouldBe("cutover-timeout;operator-token-unavailable;operator=platform-operator");
        gateway.Authorizations.ShouldBeEmpty();
        RunPreparationDiagnostic.SupportSafeStatus(failure, "writer-cutover").ShouldBe("cutover-timeout;operator-token-unavailable;operator=platform-operator;stage=writer-cutover");
    }

    /// <summary>Verifies only plain operator identifiers are exported in support-safe statuses.</summary>
    [Fact]
    public void OperatorStatusExportsOnlyPlainIdentifiers()
    {
        CompositionWriterProtocolCutover.OperatorTokenStatus("bad name/../x").ShouldBe("operator-token-unavailable;operator=unnamed");
        CompositionWriterProtocolCutover.IsOperatorTokenFailure("cutover-operator-token-unavailable;operator=platform-operator").ShouldBeTrue();
        CompositionWriterProtocolCutover.IsOperatorTokenFailure("cutover-operator-token-unavailable;operator=secret value").ShouldBeFalse();
        RunPreparationDiagnostic.SupportSafeStatus(new InvalidOperationException("cutover-operator-token-unavailable;operator=a b"), "writer-cutover")
            .ShouldBe("fault-InvalidOperationException;stage=writer-cutover");
    }

    private static byte[] Decode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '='));
    }

    /// <summary>A loopback gateway whose only unhealthy check is the writer protocol until activation succeeds.</summary>
    private sealed class FakeCutoverGateway : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private volatile bool _activated;

        private FakeCutoverGateway(HttpListener listener, Uri endpoint)
        {
            _listener = listener;
            Endpoint = endpoint;
            _loop = Task.Run(ServeAsync, CancellationToken.None);
        }

        public Uri Endpoint { get; }

        public ConcurrentQueue<string?> Authorizations { get; } = new();

        public ConcurrentQueue<string> Bodies { get; } = new();

        public static FakeCutoverGateway Start()
        {
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Uri endpoint = new("http://127.0.0.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/");
            HttpListener listener = new();
            listener.Prefixes.Add(endpoint.AbsoluteUri);
            listener.Start();
            return new FakeCutoverGateway(listener, endpoint);
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            _listener.Close();
            try
            {
#pragma warning disable VSTHRD003 // This double owns its listener loop and closes the listener before draining it.
                await _loop.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
            catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or OperationCanceledException)
            {
                // The listener is closed by design.
            }

            _stop.Dispose();
        }

        private static async Task RespondAsync(HttpListenerContext context, int status, string body)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            context.Response.Close();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is ObjectDisposedException or HttpListenerException or InvalidOperationException)
                {
                    return;
                }

                string path = context.Request.Url!.AbsolutePath;
                if (path == "/health")
                {
                    string health = _activated
                        ? "{\"results\":{}}"
                        : "{\"results\":{\"redis\":{\"status\":\"Healthy\"},\"projection-delivery-writer-protocol\":{\"status\":\"Unhealthy\"}}}";
                    await RespondAsync(context, _activated ? 200 : 503, health).ConfigureAwait(false);
                }
                else if (path == "/api/v1/admin/projections/delivery-writer-protocol/activate" && context.Request.HttpMethod == "POST")
                {
                    Authorizations.Enqueue(context.Request.Headers["Authorization"]);
                    using StreamReader reader = new(context.Request.InputStream, Encoding.UTF8);
                    Bodies.Enqueue(await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false));
                    _activated = true;
                    await RespondAsync(context, 200, "{\"status\":\"Activated\"}").ConfigureAwait(false);
                }
                else
                {
                    await RespondAsync(context, 404, "{}").ConfigureAwait(false);
                }
            }
        }
    }

    /// <summary>A model token source double.</summary>
    /// <param name="operatorName">The reported operator name.</param>
    /// <param name="create">The token behavior.</param>
    private sealed class StubTokenSource(string operatorName, Func<CancellationToken, Task<string>> create) : ICompositionOperatorTokenSource
    {
        private int _calls;

        public string OperatorName => operatorName;

        public int Calls => _calls;

        public Task<string> CreateGlobalAdministratorTokenAsync(CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _calls);
            return create(cancellationToken);
        }
    }
}
