// <copyright file="ModuleInvocationStateStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Stores invocation state outside consumer repositories.
/// </summary>
public static class ModuleInvocationStateStore
{
    private static readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    /// <summary>
    /// Creates runner-owned metadata for an invocation.
    /// </summary>
    /// <param name="command">The runner command.</param>
    /// <param name="manifestPath">The manifest path.</param>
    /// <param name="profile">The optional profile.</param>
    /// <param name="filterHash">The optional filter fingerprint.</param>
    /// <param name="runtimePlan">The run-scoped topology identity.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The created invocation state.</returns>
    public static async Task<ModuleInvocationState> CreateAsync(
        ModuleInvocationCommand command,
        string manifestPath,
        string? profile,
        string? filterHash,
        ModuleRuntimePlan runtimePlan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentNullException.ThrowIfNull(runtimePlan);

        string manifestHash = await ComputeManifestHashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        ModuleInvocationState state = new(
            runtimePlan.RunId,
            command,
            manifestHash,
            profile,
            filterHash,
            runtimePlan.TenantNamespace,
            runtimePlan.ResourceNamespace,
            DateTimeOffset.UtcNow);
        string directory = GetStateDirectory();
        _ = Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"{state.RunId}.json");
        string json = JsonSerializer.Serialize(state, _serializerOptions);
        await File.WriteAllTextAsync(path, json, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return state;
    }

    /// <summary>
    /// Removes retained runner state matching a manifest hash.
    /// </summary>
    /// <param name="manifestPath">The manifest path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after idempotent cleanup.</returns>
    public static async Task DownAsync(string manifestPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string directory = GetStateDirectory();
        if (!Directory.Exists(directory))
        {
            return;
        }

        string manifestHash = await ComputeManifestHashAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        foreach (string statePath in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleInvocationState? state = await ReadStateAsync(statePath, cancellationToken).ConfigureAwait(false);
            if (state is not null && string.Equals(state.ManifestHash, manifestHash, StringComparison.Ordinal))
            {
                File.Delete(statePath);
            }
        }
    }

    private static async Task<string> ComputeManifestHashAsync(string manifestPath, CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(SHA256.HashData(content));
    }

    private static string GetStateDirectory() => Path.Combine(Path.GetTempPath(), "hexalith-builds", "runs");

    private static async Task<ModuleInvocationState?> ReadStateAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            FileStream stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<ModuleInvocationState>(stream, _serializerOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}