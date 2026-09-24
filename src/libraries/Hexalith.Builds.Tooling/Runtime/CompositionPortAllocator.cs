// <copyright file="CompositionPortAllocator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

/// <summary>
/// Allocates distinct, currently free loopback ports outside the host's outbound ephemeral range.
/// </summary>
/// <remarks>
/// Probe sockets are released before the run binds its ports. Outbound connections can reclaim an ephemeral
/// port in that gap, so Linux allocations use a range outside ip_local_port_range. Allocations are serialized
/// and every port handed out by this process is remembered.
/// </remarks>
public static class CompositionPortAllocator
{
    private const int _basePortsPerRun = 16;

    private const int _rememberedPorts = 4096;

    private const int _minimumCandidatePort = 10000;

    private const int _preferredMaximumCandidatePort = 30000;

    private static readonly Lock _gate = new();

    private static readonly HashSet<int> _handedOut = [];

    private static readonly Queue<int> _handedOutOrder = new();

    /// <summary>
    /// Allocates every run-scoped port.
    /// </summary>
    /// <param name="moduleCount">The number of module HTTP endpoints to allocate.</param>
    /// <param name="includeSecondEventStore">Whether to allocate a second EventStore HTTP endpoint and sidecar.</param>
    /// <returns>The allocated ports.</returns>
    /// <exception cref="InvalidOperationException">Not enough free loopback ports are available.</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Every retained probe listener is disposed in the enclosing finally; failed binds are disposed in the catch.")]
    public static CompositionRunPorts Allocate(int moduleCount, bool includeSecondEventStore = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(moduleCount);
        lock (_gate)
        {
            (int minimum, int maximumExclusive) = CandidateRange();
            List<TcpListener> listeners = [];
            List<int> ports = [];
            int secondEventStoreStart = _basePortsPerRun + (moduleCount * 5);
            int portCount = secondEventStoreStart + (includeSecondEventStore ? 5 : 0);
            try
            {
                for (int attempt = 0; ports.Count < portCount && attempt < portCount * 64; attempt++)
                {
                    TcpListener listener = new(IPAddress.Loopback, RandomNumberGenerator.GetInt32(minimum, maximumExclusive));
                    try
                    {
                        listener.Start();
                    }
                    catch (SocketException)
                    {
                        listener.Stop();
                        listener.Dispose();
                        continue;
                    }

                    listeners.Add(listener);
                    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    if (!_handedOut.Contains(port))
                    {
                        ports.Add(port);
                    }
                }

                if (ports.Count < portCount)
                {
                    throw new InvalidOperationException("Not enough free loopback ports are available for a run.");
                }

                foreach (int port in ports)
                {
                    Remember(port);
                }

                CompositionDaprSidecarPorts? secondSidecar = includeSecondEventStore
                    ? new CompositionDaprSidecarPorts(
                        ports[secondEventStoreStart + 1],
                        ports[secondEventStoreStart + 2],
                        ports[secondEventStoreStart + 3],
                        ports[secondEventStoreStart + 4])
                    : null;
                return new CompositionRunPorts(
                    ports[0],
                    ports[1],
                    ports[2],
                    ports[3],
                    ports[4],
                    ports[5],
                    ports[6],
                    ports[7],
                    ports[8],
                    ports[9],
                    ports[10],
                    ports[11],
                    ports[12],
                    [.. Enumerable.Range(0, moduleCount).Select(index => ports[_basePortsPerRun + (index * 5)])],
                    ports[13],
                    ports[14],
                    ports[15],
                    [.. Enumerable.Range(0, moduleCount).Select(index =>
                    {
                        int start = _basePortsPerRun + (index * 5) + 1;
                        return new CompositionDaprSidecarPorts(ports[start], ports[start + 1], ports[start + 2], ports[start + 3]);
                    })],
                    includeSecondEventStore ? ports[secondEventStoreStart] : null,
                    secondSidecar);
            }
            finally
            {
                foreach (TcpListener listener in listeners)
                {
                    listener.Stop();
                    listener.Dispose();
                }
            }
        }
    }

    private static (int Minimum, int MaximumExclusive) CandidateRange()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (_minimumCandidatePort, _preferredMaximumCandidatePort);
        }

        const string rangePath = "/proc/sys/net/ipv4/ip_local_port_range";
        string[] values = File.ReadAllText(rangePath).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (values.Length != 2
            || !int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out int ephemeralStart)
            || !int.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out int ephemeralEnd))
        {
            throw new InvalidOperationException("The Linux ephemeral port range could not be read.");
        }

        int belowEnd = Math.Min(_preferredMaximumCandidatePort, ephemeralStart);
        if (belowEnd - _minimumCandidatePort > _rememberedPorts)
        {
            return (_minimumCandidatePort, belowEnd);
        }

        int aboveStart = Math.Max(ephemeralEnd + 1, _preferredMaximumCandidatePort);
        return 65536 - aboveStart > _rememberedPorts
            ? (aboveStart, 65536)
            : throw new InvalidOperationException("No sufficiently large non-ephemeral loopback port range is available.");
    }

    private static void Remember(int port)
    {
        _ = _handedOut.Add(port);
        _handedOutOrder.Enqueue(port);
        while (_handedOutOrder.Count > _rememberedPorts)
        {
            _ = _handedOut.Remove(_handedOutOrder.Dequeue());
        }
    }
}
