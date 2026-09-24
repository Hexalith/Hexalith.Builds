// <copyright file="RedisPingHealthCheck.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Reports the run-scoped Redis endpoint healthy once it answers <c>PING</c>.
/// </summary>
/// <param name="port">The run-scoped Redis port.</param>
internal sealed class RedisPingHealthCheck(int port) : IHealthCheck
{
    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource probe = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        probe.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using TcpClient client = new();
            await client.ConnectAsync("127.0.0.1", port, probe.Token).ConfigureAwait(false);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes("PING\r\n"), probe.Token).ConfigureAwait(false);
            byte[] buffer = new byte[16];
            int read = await stream.ReadAsync(buffer, probe.Token).ConfigureAwait(false);
            return Encoding.ASCII.GetString(buffer, 0, read).StartsWith("+PONG", StringComparison.Ordinal)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Redis did not answer PING.");
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            return HealthCheckResult.Unhealthy("Redis is not reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Redis PING probe exceeded its 3-second bound.");
        }
    }
}
