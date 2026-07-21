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
    /// High-precision credential markers. Each carries an explicit delimiter, a well-known
    /// credential prefix, or an unambiguous key body so ordinary metadata (module ids,
    /// repository-relative paths, versions) is not misclassified as secret-bearing.
    /// </summary>
    private static readonly string[] _secretMarkers =
    [
        "bearer ",
        "token=",
        "secret=",
        "password=",
        "passwd=",
        "pwd=",
        "pass=",
        "api-key",
        "api_key",
        "apikey=",
        "client-secret",
        "client_secret",
        "private key",       // PEM private-key blocks (-----BEGIN ... PRIVATE KEY-----)
        "ghp_",              // GitHub personal access token
        "gho_",              // GitHub OAuth token
        "ghu_",              // GitHub user-to-server token
        "ghs_",              // GitHub server-to-server token
        "ghr_",              // GitHub refresh token
        "github_pat_",       // GitHub fine-grained token
        "xoxb-",             // Slack bot token
        "xoxp-",             // Slack user token
        "aws_secret_access_key",
        "eyj",               // JWT header segment (base64url of {")
    ];

    /// <summary>
    /// Determines whether a manifest value appears to contain credential material.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>A value indicating whether credential material was detected.</returns>
    public static bool ContainsSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return Array.Exists(_secretMarkers, marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}