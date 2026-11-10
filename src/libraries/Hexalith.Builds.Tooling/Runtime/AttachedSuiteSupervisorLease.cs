// <copyright file="AttachedSuiteSupervisorLease.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Text.Json;

/// <summary>The liveness lease of the owner command that hosts runner-owned attached suites.</summary>
/// <param name="Schema">The internal lease schema.</param>
/// <param name="OwnerRunId">The owner run hosting the supervisor.</param>
/// <param name="ProcessId">The owner command process.</param>
/// <param name="ProcessStartedAtUtc">The owner command process start, which distinguishes a reused process identifier.</param>
/// <param name="RenewedAtUtc">The last renewal.</param>
/// <param name="ExpiresAtUtc">The lease expiry; an unrenewed lease is not live.</param>
/// <param name="State"><c>open</c> while the owner accepts attached suites, otherwise <c>closed</c>.</param>
public sealed record AttachedSuiteSupervisorLease(string Schema, string OwnerRunId, int ProcessId, DateTimeOffset? ProcessStartedAtUtc, DateTimeOffset RenewedAtUtc, DateTimeOffset ExpiresAtUtc, string State)
{
    /// <summary>The supported internal lease schema.</summary>
    public const string SupportedSchema = "hexalith.g4-attached-suite-supervisor.v1";

    /// <summary>Gets the lease path beside the owner's attachment records.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <returns>The lease path.</returns>
    public static string PathFor(string diagnosticsDirectory) => Path.Combine(diagnosticsDirectory, "attached-suite-supervisor.json");

    /// <summary>Reads the lease, treating absence and unverifiable bytes as no lease.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <returns>The recorded lease, or null.</returns>
    public static AttachedSuiteSupervisorLease? Read(string diagnosticsDirectory)
    {
        try
        {
            string path = PathFor(diagnosticsDirectory);
            return File.Exists(path) && new FileInfo(path) is { Length: > 0 and <= 4096, LinkTarget: null }
                ? JsonSerializer.Deserialize<AttachedSuiteSupervisorLease>(File.ReadAllBytes(path), CompositionDocumentStore.SerializerOptions)
                : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Gets whether a live owner command currently accepts attached suites for an owner.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <returns>Whether an open, renewed lease belongs to a running process.</returns>
    public static bool IsLive(string diagnosticsDirectory, string ownerRunId, DateTimeOffset now) => Read(diagnosticsDirectory) is { } lease && lease.IsLiveAt(ownerRunId, now);

    /// <summary>Gets whether this lease is open, unexpired and held by its recorded process.</summary>
    /// <param name="ownerRunId">The expected owner.</param>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <returns>Whether the owner command is live.</returns>
    public bool IsLiveAt(string ownerRunId, DateTimeOffset now) => Schema == SupportedSchema && OwnerRunId == ownerRunId && State == "open"
        && now < ExpiresAtUtc && HolderRunning();

    /// <summary>Gets whether the recorded owner process still runs, regardless of the lease state.</summary>
    /// <returns>Whether the recorded process exists with the recorded start time.</returns>
    public bool HolderRunning()
    {
        if (ProcessId == Environment.ProcessId)
        {
            return true;
        }

        try
        {
            using Process process = Process.GetProcessById(ProcessId);
            return !process.HasExited
                && (ProcessStartedAtUtc is not { } started || Math.Abs((process.StartTime.ToUniversalTime() - started.UtcDateTime).TotalSeconds) < 2);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }
}
