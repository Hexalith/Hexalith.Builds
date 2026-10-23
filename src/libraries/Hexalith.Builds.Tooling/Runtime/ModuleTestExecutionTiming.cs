// <copyright file="ModuleTestExecutionTiming.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The actual execution clock, independent of immutable source selection and startup.</summary>
/// <param name="StartedAtUtc">The execution activation time, persisted before launch.</param>
/// <param name="DeadlineUtc">The absolute execution expiry, never renewed.</param>
/// <param name="EffectiveSeconds">The declared execution bound.</param>
/// <param name="ConfigurationContentIdentity">The selected configuration content identity.</param>
public sealed record ModuleTestExecutionTiming(DateTimeOffset StartedAtUtc, DateTimeOffset DeadlineUtc, int EffectiveSeconds, string ConfigurationContentIdentity)
{
    /// <summary>Gets the measured execution duration after the child stops.</summary>
    public double? ActualDurationSeconds { get; init; }

    /// <summary>Gets the credential expiry rounded up to whole seconds.</summary>
    public DateTimeOffset CredentialExpiryUtc => DateTimeOffset.FromUnixTimeSeconds(DeadlineUtc.ToUnixTimeSeconds() + (DeadlineUtc.Ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1));

    /// <summary>Activates a new entry once readiness has passed.</summary>
    /// <param name="selection">The immutable entry selection.</param>
    /// <param name="startedAt">The actual activation time.</param>
    /// <returns>The immutable active clock.</returns>
    internal static ModuleTestExecutionTiming Start(ModuleTestSelection selection, DateTimeOffset startedAt) =>
        new(startedAt, startedAt.AddSeconds(selection.ExecutionTimeoutSeconds), selection.ExecutionTimeoutSeconds, "sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { selection.ModuleId, selection.Name, selection.Executor, selection.Entry, selection.ManifestHash, selection.EntryHash, selection.ExecutionTimeoutSeconds, selection.StopTimeoutSeconds, selection.Mode, selection.DaprRuntimeVersion }))));
}
