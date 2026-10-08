// <copyright file="PlatformVersionCatalog.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;
using System.Text.RegularExpressions;

using NuGet.Versioning;

/// <summary>
/// The evaluated catalog and dependency requirements embedded when the tools are built.
/// </summary>
/// <param name="EventStoreVersion">The evaluated eventStoreVersion selection.</param>
/// <param name="FrontComposerVersion">The evaluated frontComposerVersion selection.</param>
/// <param name="DaprSdkVersion">The evaluated daprSdkVersion selection.</param>
/// <param name="AspireHostingVersion">The evaluated aspireHostingVersion selection.</param>
/// <param name="AspireHostingDaprVersion">The evaluated aspireHostingDaprVersion selection.</param>
/// <param name="AspireAppHostSdkVersion">The evaluated aspireAppHostSdkVersion selection.</param>
/// <param name="DaprRuntimeVersion">The evaluated daprRuntimeVersion selection.</param>
/// <param name="DaprCliVersion">The evaluated daprCliVersion selection.</param>
/// <param name="RedisImage">The evaluated redisImage selection.</param>
/// <param name="RedisImageTag">The evaluated redisImageTag selection.</param>
/// <param name="RedisImageDigest">The evaluated redisImageDigest selection.</param>
/// <param name="EventStoreHostingDaprRange">The evaluated eventStoreHostingDaprRange selection.</param>
public sealed partial record PlatformVersionCatalog(
    string EventStoreVersion,
    string FrontComposerVersion,
    string DaprSdkVersion,
    string AspireHostingVersion,
    string AspireHostingDaprVersion,
    string AspireAppHostSdkVersion,
    string DaprRuntimeVersion,
    string DaprCliVersion,
    string RedisImage,
    string RedisImageTag,
    string RedisImageDigest,
    string EventStoreHostingDaprRange)
{
    private static readonly Lazy<PlatformVersionCatalog> _current = new(LoadEmbedded);

    /// <summary>Gets the strict catalog snapshot embedded in this tooling assembly.</summary>
    public static PlatformVersionCatalog Current => _current.Value;

    /// <summary>Loads a strict snapshot without reading a source checkout or resolving packages.</summary>
    /// <param name="json">The generated snapshot JSON.</param>
    /// <returns>The validated catalog record.</returns>
    /// <exception cref="InvalidDataException">A required catalog field is missing, duplicate or invalid.</exception>
    public static PlatformVersionCatalog Parse(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Platform version catalog root must be an object.");
        }

        Dictionary<string, string> selections = new(StringComparer.Ordinal);
        string[] required = ["schemaVersion", "eventStoreVersion", "frontComposerVersion", "daprSdkVersion", "aspireHostingVersion", "aspireHostingDaprVersion", "aspireAppHostSdkVersion", "daprRuntimeVersion", "daprCliVersion", "redisImage", "redisImageTag", "redisImageDigest", "eventStoreHostingDaprRange"];
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            if (!required.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new InvalidDataException($"Unknown platform version catalog field '{property.Name}'.");
            }

            if (!selections.TryAdd(property.Name, property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : string.Empty))
            {
                throw new InvalidDataException($"Duplicate platform version catalog field '{property.Name}'.");
            }
        }

        foreach (string field in required)
        {
            if (!selections.TryGetValue(field, out string? value) || string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Platform version catalog field '{field}' is missing or invalid.");
            }

            if (field.EndsWith("Version", StringComparison.Ordinal) && field != "schemaVersion" && !NuGetVersion.TryParse(value, out _))
            {
                throw new InvalidDataException($"Platform version catalog field '{field}' has malformed version '{value}'.");
            }
        }

        if (selections["schemaVersion"] != "1")
        {
            throw new InvalidDataException("Unsupported platform version catalog field 'schemaVersion'.");
        }

        if (!ImageRegex().IsMatch(selections["redisImage"]) || !TagRegex().IsMatch(selections["redisImageTag"]) || !DigestRegex().IsMatch(selections["redisImageDigest"]))
        {
            throw new InvalidDataException("Malformed platform version catalog redisImage, redisImageTag or redisImageDigest field.");
        }

        VersionRange range = VersionRange.TryParse(selections["eventStoreHostingDaprRange"], out VersionRange? parsedRange)
            ? parsedRange
            : throw new InvalidDataException("Malformed platform version catalog field 'eventStoreHostingDaprRange'.");

        return !range.Satisfies(NuGetVersion.Parse(selections["aspireHostingDaprVersion"]))
            ? throw new InvalidDataException($"Hexalith.EventStore.Aspire/{selections["eventStoreVersion"]} requires CommunityToolkit.Aspire.Hosting.Dapr range '{selections["eventStoreHostingDaprRange"]}'; selected CommunityToolkit.Aspire.Hosting.Dapr/{selections["aspireHostingDaprVersion"]} is incompatible.")
            : new PlatformVersionCatalog(
            selections["eventStoreVersion"],
            selections["frontComposerVersion"],
            selections["daprSdkVersion"],
            selections["aspireHostingVersion"],
            selections["aspireHostingDaprVersion"],
            selections["aspireAppHostSdkVersion"],
            selections["daprRuntimeVersion"],
            selections["daprCliVersion"],
            selections["redisImage"],
            selections["redisImageTag"],
            selections["redisImageDigest"],
            selections["eventStoreHostingDaprRange"]);
    }

    /// <summary>Compares release and prerelease identities, ignoring build metadata.</summary>
    /// <param name="observed">The observed tool version.</param>
    /// <param name="expected">The selected SDK version.</param>
    /// <returns>Whether both versions have the same release and prerelease identity.</returns>
    public static bool VersionsEqual(string observed, string expected)
    {
        ArgumentNullException.ThrowIfNull(observed);
        ArgumentNullException.ThrowIfNull(expected);
        return NuGetVersion.TryParse(observed, out NuGetVersion? observedVersion)
            && NuGetVersion.TryParse(expected, out NuGetVersion? expectedVersion)
            && VersionComparer.VersionRelease.Equals(observedVersion, expectedVersion);
    }

    private static PlatformVersionCatalog LoadEmbedded()
    {
        using Stream stream = typeof(PlatformVersionCatalog).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json")
            ?? throw new InvalidDataException("The embedded platform version catalog is missing. Rebuild the tooling from the Builds catalog.");
        using StreamReader reader = new(stream);
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex(@"^[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ImageRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"^sha256:[0-9a-f]{64}$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DigestRegex();
}