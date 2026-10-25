// <copyright file="CompositionResourceClaimStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>Atomically claims actual exclusive resources across runner processes; composition identity is never a lock.</summary>
/// <param name="stateDirectory">The runner's state directory.</param>
public sealed class CompositionResourceClaimStore(string stateDirectory)
{
    private readonly string _directory = Path.Combine(Path.GetFullPath(stateDirectory), "claims");

    /// <summary>Claims the complete exclusive set, rolling back only this acquisition on conflict.</summary>
    /// <param name="runId">The owning run.</param>
    /// <param name="resourceIds">The actual exclusive identities.</param>
    /// <exception cref="CompositionResourceClaimConflictException">Another managed run owns a resource.</exception>
    public void Acquire(string runId, IEnumerable<string> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        ArgumentOutOfRangeException.ThrowIfEqual(CompositionRunPlanFactory.IsRunId(runId), false);
        CompositionWorkspace.CreatePrivateDirectory(_directory);
        using FileStream transaction = OpenTransaction();
        List<string> acquired = [];
        try
        {
            foreach (string resourceId in resourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
                string path = PathFor(resourceId);
                string temporary = Path.Combine(_directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CompositionResourceClaim(resourceId, runId));
                    FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                    if (!OperatingSystem.IsWindows())
                    {
                        options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    }

                    using (FileStream stream = new(temporary, options))
                    {
                        stream.Write(bytes);
                        stream.Flush(flushToDisk: true);
                    }

                    // Readers can see only complete claims, including if this process terminates mid-write.
                    File.Move(temporary, path);
                    acquired.Add(path);
                }
                catch (IOException) when (File.Exists(path) && !acquired.Contains(path, StringComparer.Ordinal))
                {
                    throw new CompositionResourceClaimConflictException(resourceId, ReadOwner(path) ?? "unknown");
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }
            }
        }
        catch
        {
            foreach (string path in acquired)
            {
                File.Delete(path);
            }

            throw;
        }
    }

    /// <summary>Releases only claims whose durable owner matches a verified cleaned run.</summary>
    /// <param name="runId">The cleaned owning run.</param>
    public void ReleaseOwned(string runId)
    {
        if (!Directory.Exists(_directory))
        {
            return;
        }

        using FileStream transaction = OpenTransaction();
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json").Where(path => ReadOwner(path) == runId))
        {
            File.Delete(path);
        }
    }

    /// <summary>Returns the currently claimed TCP ports, including allocations not yet bound by their owners.</summary>
    /// <returns>The managed loopback port exclusions.</returns>
    public IReadOnlySet<int> ClaimedPorts()
    {
        if (!Directory.Exists(_directory))
        {
            return new HashSet<int>();
        }

        using FileStream transaction = OpenTransaction();
        HashSet<int> ports = [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            using FileStream stream = File.OpenRead(path);
            string? resource = JsonSerializer.Deserialize<CompositionResourceClaim>(stream)?.ResourceId;
            const string prefix = "tcp:127.0.0.1:";
            if (resource?.StartsWith(prefix, StringComparison.Ordinal) == true
                && int.TryParse(resource.AsSpan(prefix.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port))
            {
                _ = ports.Add(port);
            }
        }

        return ports;
    }

    private static string? ReadOwner(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            string? owner = JsonSerializer.Deserialize<CompositionResourceClaim>(stream)?.OwnerRunId;
            return CompositionRunPlanFactory.IsRunId(owner) ? owner : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private FileStream OpenTransaction()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                return new FileStream(Path.Combine(_directory, "transaction.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (System.Diagnostics.Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(5);
            }
        }
    }

    private string PathFor(string resourceId) => Path.Combine(_directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(resourceId))) + ".json");
}