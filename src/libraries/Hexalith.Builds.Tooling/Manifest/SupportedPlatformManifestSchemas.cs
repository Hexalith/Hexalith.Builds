// <copyright file="SupportedPlatformManifestSchemas.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines the bounded current/previous-major enrollment window, excluding legacy v1.
/// </summary>
public static class SupportedPlatformManifestSchemas
{
    /// <summary>The first major containing the complete Platform declaration.</summary>
    public const int FirstPlatformMajor = 2;

    /// <summary>The current Platform declaration major.</summary>
    public const int CurrentMajor = 2;

    /// <summary>The current schema identity.</summary>
    public const string Current = "hexalith.module-manifest.v2";

    /// <summary>The default startup budget in seconds.</summary>
    public const int DefaultStartupTimeoutSeconds = 600;

    /// <summary>Gets the current and, when eligible, previous Platform schema identities.</summary>
    public static IReadOnlyList<string> Eligible { get; } = Array.AsReadOnly(
        Enumerable.Range(Math.Max(FirstPlatformMajor, CurrentMajor - 1), Math.Min(2, CurrentMajor - FirstPlatformMajor + 1))
            .Select(major => $"hexalith.module-manifest.v{major}").ToArray());
}