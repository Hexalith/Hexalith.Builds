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
    private const int _maximumBytes = 64 * 1024;

    private readonly string _directory = Path.Combine(Path.GetFullPath(stateDirectory), "claims");

    /// <summary>Claims the complete exclusive set, rolling back only this acquisition on conflict.</summary>
    /// <param name="runId">The owning run.</param>
    /// <param name="resourceIds">The actual exclusive identities.</param>
    /// <exception cref="CompositionResourceClaimConflictException">Another managed run owns a resource.</exception>
    /// <exception cref="ArgumentException">The requested observation exceeds the bounded claim or receipt format.</exception>
    public void Acquire(string runId, IEnumerable<string> resourceIds)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        ArgumentOutOfRangeException.ThrowIfEqual(CompositionRunPlanFactory.IsRunId(runId), false);
        string[] requested = [.. resourceIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        foreach (string resource in requested)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(resource);
            if (JsonSerializer.SerializeToUtf8Bytes(new CompositionResourceClaim(resource, runId)).Length > _maximumBytes)
            {
                throw new ArgumentException("The exclusive resource observation exceeds the readable claim bound.", nameof(resourceIds));
            }
        }

        CompositionWorkspace.CreatePrivateDirectory(_directory);
        using FileStream transaction = OpenTransaction();
        string ownerDirectory = Path.Combine(_directory, "owners");
        string receipt = Path.Combine(ownerDirectory, runId + ".json");
        string[] prior = ReadReceipt(runId);
        string[] identities = [.. prior.Concat(requested.Select(resource => Path.GetFileNameWithoutExtension(PathFor(resource)))).Distinct(StringComparer.Ordinal)];
        byte[] receiptBytes = JsonSerializer.SerializeToUtf8Bytes(identities);
        if (receiptBytes.Length > _maximumBytes)
        {
            throw new ArgumentException("The complete exclusive set exceeds the readable receipt bound.", nameof(resourceIds));
        }

        CompositionWorkspace.CreatePrivateDirectory(ownerDirectory);
        string receiptTemporary = Path.Combine(ownerDirectory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            FileStreamOptions receiptOptions = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
            {
                receiptOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            using (FileStream stream = new(receiptTemporary, receiptOptions))
            {
                stream.Write(receiptBytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(receiptTemporary, receipt, overwrite: true);
        }
        finally
        {
            File.Delete(receiptTemporary);
        }

        List<string> acquired = [];
        try
        {
            foreach (string resourceId in requested)
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

    /// <summary>Lists exact claim file identities whose durable owner is this run.</summary>
    /// <param name="runId">The owner identity.</param>
    /// <returns>The owned claim identities.</returns>
    /// <exception cref="IOException">A claim recorded for this run cannot be verified.</exception>
    public IReadOnlyList<string> FindOwned(string runId) => FindOwned(runId, () => throw new IOException("Managed claim ownership cannot be verified."));

    /// <summary>Returns receipt-bound identities while exposing unverifiable claim paths without discarding them.</summary>
    /// <param name="runId">The exact owning run.</param>
    /// <param name="onUnverified">Reports each unresolved recorded path.</param>
    /// <returns>Known owned claim identities and unresolved receipt-bound paths.</returns>
    internal IReadOnlyList<string> FindOwned(string runId, Action onUnverified)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(CompositionRunPlanFactory.IsRunId(runId), false);
        ArgumentNullException.ThrowIfNull(onUnverified);
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        HashSet<string> owned = new(StringComparer.Ordinal);
        string[] recorded = ReadReceipt(runId);
        HashSet<string> expected = new(recorded, StringComparer.Ordinal);
        IEnumerable<string> paths = Directory.EnumerateFileSystemEntries(_directory, "*.json")
            .Concat(recorded.Select(identity => Path.Combine(_directory, identity + ".json"))).Distinct(StringComparer.Ordinal);
        foreach (string path in paths)
        {
            string identity = Path.GetFileNameWithoutExtension(path);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(path);
            }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            {
                continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                if (expected.Contains(identity))
                {
                    _ = owned.Add("claim:" + identity);
                    onUnverified();
                }

                continue;
            }

            string? owner = (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 ? ReadOwner(path) : null;
            if (owner == runId || (owner is null && expected.Contains(identity)))
            {
                _ = owned.Add("claim:" + identity);
            }

            if (owner is null && expected.Contains(identity))
            {
                onUnverified();
            }
        }

        return [.. owned.Order(StringComparer.Ordinal)];
    }

    private static string? ReadOwner(string path)
    {
        try
        {
            using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path);
            if (!stream.CanSeek || stream.Length > _maximumBytes)
            {
                return null;
            }

            string? owner = JsonSerializer.Deserialize<CompositionResourceClaim>(stream)?.OwnerRunId;
            return CompositionRunPlanFactory.IsRunId(owner) ? owner : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private string[] ReadReceipt(string runId)
    {
        string receipt = Path.Combine(_directory, "owners", runId + ".json");
        try
        {
            _ = File.GetAttributes(receipt);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }

        try
        {
            using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(receipt, respectSharing: true);
            string[] identities = stream.CanSeek && stream.Length <= _maximumBytes
                ? JsonSerializer.Deserialize<string[]>(stream) ?? throw new IOException("Claim receipt cannot be verified.")
                : throw new IOException("Claim receipt cannot be verified.");
            return identities.All(identity => identity is { Length: 64 } && identity.All(char.IsAsciiHexDigit))
                ? identities
                : throw new IOException("Claim receipt cannot be verified.");
        }
        catch (JsonException exception)
        {
            throw new IOException("Claim receipt cannot be verified.", exception);
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
