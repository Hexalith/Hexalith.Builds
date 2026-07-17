// <copyright file="ManifestSecretDetector.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Detects prohibited secret-bearing manifest values without retaining those values.
/// </summary>
public static class ManifestSecretDetector
{
    /// <summary>
    /// Determines whether a manifest value appears to contain credential material.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>A value indicating whether credential material was detected.</returns>
    public static bool ContainsSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return value.Contains("bearer ", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("token=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("api-key", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("client-secret", StringComparison.OrdinalIgnoreCase);
    }
}