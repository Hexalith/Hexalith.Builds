// <copyright file="ModuleTestSelection.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>The immutable metadata binding of one declared module test to its initial request.</summary>
/// <param name="ModuleId">The declaration owner.</param>
/// <param name="Name">The selected profile name.</param>
/// <param name="Executor">The fixed executor identity.</param>
/// <param name="Entry">The module-relative entry file.</param>
/// <param name="ManifestHash">The selected declaration bytes.</param>
/// <param name="EntryHash">The selected entry bytes.</param>
/// <param name="ExecutionTimeoutSeconds">The lifetime from the initial request.</param>
/// <param name="StopTimeoutSeconds">The separate stopping bound.</param>
/// <param name="RequestedAtUtc">The initial request time.</param>
/// <param name="ExecutionDeadlineUtc">The absolute expiry, never renewed.</param>
/// <param name="CredentialExpiryUtc">The whole-second JWT expiry rounded up by less than one second from the execution expiry.</param>
/// <param name="Mode">The declared execution mode.</param>
public sealed record ModuleTestSelection(
    string ModuleId,
    string Name,
    string Executor,
    string Entry,
    string ManifestHash,
    string EntryHash,
    int ExecutionTimeoutSeconds,
    int StopTimeoutSeconds,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExecutionDeadlineUtc,
    DateTimeOffset CredentialExpiryUtc,
    string Mode)
{
    /// <summary>Gets the optional exact runtime selected by an owner-declared local experiment; defaults remain catalog-owned.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? DaprRuntimeVersion { get; init; }

    /// <summary>Selects exactly one test from a validated declaration, before startup.</summary>
    /// <param name="declarations">The validated declarations.</param>
    /// <param name="name">The explicit profile.</param>
    /// <param name="manifestPath">The declaration path.</param>
    /// <param name="requestedAt">The initial command time.</param>
    /// <returns>The unique binding, or null for absent or ambiguous selection.</returns>
    internal static ModuleTestSelection? Select(IReadOnlyList<PlatformModuleDeclaration> declarations, string? name, string manifestPath, DateTimeOffset requestedAt)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        List<ModuleTestSelection> matches = [];
        foreach (PlatformModuleDeclaration declaration in declarations)
        {
            if (!declaration.Declaration.TryGetProperty("tests", out JsonElement tests))
            {
                continue;
            }

            foreach (JsonElement test in tests.EnumerateArray().Where(test => test.GetProperty("name").GetString() == name))
            {
                if (declaration.SourceHash is null || declaration.SourceHash != Hash(manifestPath))
                {
                    return null;
                }

                int seconds = test.GetProperty("executionTimeoutSeconds").GetInt32();
                DateTimeOffset expiry = requestedAt + TimeSpan.FromSeconds(seconds);
                DateTimeOffset credentialExpiry = DateTimeOffset.FromUnixTimeSeconds(expiry.ToUnixTimeSeconds() + (expiry.Ticks % TimeSpan.TicksPerSecond == 0 ? 0 : 1));
                string entry = test.GetProperty("entry").GetString()!;
                string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
                matches.Add(new ModuleTestSelection(
                    declaration.Declaration.GetProperty("identity").GetProperty("moduleId").GetString()!,
                    name,
                    test.GetProperty("executor").GetString()!,
                    entry,
                    declaration.SourceHash,
                    Hash(Path.Combine(root, entry)),
                    seconds,
                    test.GetProperty("stopTimeoutSeconds").GetInt32(),
                    requestedAt,
                    expiry,
                    credentialExpiry,
                    test.GetProperty("mode").GetString()!)
                {
                    DaprRuntimeVersion = test.TryGetProperty("daprRuntimeVersion", out JsonElement runtime) ? runtime.GetString() : null,
                });
            }
        }

        return matches.Count == 1 ? matches[0] : null;
    }

    /// <summary>Rejects symlinks, nested repositories and dependency-owned files.</summary>
    /// <param name="root">The module repository root.</param>
    /// <param name="entry">The repository-relative entry.</param>
    /// <returns>Whether the existing file belongs directly to the module.</returns>
    internal static bool IsOwnedEntry(string root, string entry)
    {
        List<Diagnostics.ToolDiagnostic> diagnostics = [];
        if (ManifestPathValidator.ValidateExistingFile(entry, root, "tests.entry", diagnostics) is null
            || string.Equals(entry.Split('/')[0], "references", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        FileSystemInfo? current = new FileInfo(Path.GetFullPath(Path.Combine(root, entry)));
        while (current is not null && current.FullName != Path.GetFullPath(root))
        {
            if (current.LinkTarget is not null || (current is DirectoryInfo directory && (File.Exists(Path.Combine(directory.FullName, ".git")) || Directory.Exists(Path.Combine(directory.FullName, ".git")))))
            {
                return false;
            }

            current = current is FileInfo file ? file.Directory : ((DirectoryInfo)current).Parent;
        }

        return current is not null;
    }

    /// <summary>Checks ownership and bytes again at the execution boundary.</summary>
    /// <param name="manifestPath">The original declaration.</param>
    /// <returns>Whether the selected inputs remain unchanged and owned.</returns>
    internal bool MatchesSource(string manifestPath)
    {
        try
        {
            string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
            PlatformManifestValidationResult current = PlatformManifestValidator.Validate([manifestPath]);
            return current.IsValid && Select(current.Declarations!, Name, manifestPath, RequestedAtUtc) == this
                && IsOwnedEntry(root, Entry) && Hash(manifestPath) == ManifestHash && Hash(Path.Combine(root, Entry)) == EntryHash;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
