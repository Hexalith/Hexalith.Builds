// <copyright file="ManifestSecretDetector.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.RegularExpressions;

/// <summary>
/// Detects prohibited secret-bearing manifest values without retaining those values.
/// </summary>
public static partial class ManifestSecretDetector
{
    /// <summary>
    /// Determines whether a manifest value appears to contain credential material.
    /// </summary>
    /// <param name="value">The value to inspect.</param>
    /// <returns>A value indicating whether credential material was detected.</returns>
    public static bool ContainsSecret(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return CredentialAssignmentRegex().IsMatch(value)
            || AuthorizationRegex().IsMatch(value)
            || KnownTokenPrefixRegex().IsMatch(value)
            || JsonWebTokenRegex().IsMatch(value)
            || PemPrivateKeyRegex().IsMatch(value)
            || AwsAccessKeyRegex().IsMatch(value);
    }

    [GeneratedRegex(
        @"(?:^|[/\s?&;])(?:token|secret|password|passwd|pwd|pass|api[-_]?key|client[-_]?secret|accountkey|sharedaccesskey|sharedaccesssignature|sig|aws_access_key_id|aws_secret_access_key)\s*=\s*[^\s;&]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CredentialAssignmentRegex();

    [GeneratedRegex(
        @"(?:^|[/\s?&;])(?:authorization\s*=\s*)?(?:bearer|basic)\s+[A-Za-z0-9+/_=.-]{8,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"(?:gh[pousr]_[A-Za-z0-9]{8,}|github_pat_[A-Za-z0-9_]{8,}|xox[bp]-[A-Za-z0-9-]{8,})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KnownTokenPrefixRegex();

    [GeneratedRegex(
        @"(?:^|[^A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(?:\.[A-Za-z0-9_-]{5,})?",
        RegexOptions.CultureInvariant)]
    private static partial Regex JsonWebTokenRegex();

    [GeneratedRegex(
        @"-----BEGIN(?: [A-Z0-9]+)? PRIVATE KEY-----",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PemPrivateKeyRegex();

    [GeneratedRegex(@"(?:AKIA|ASIA)[A-Z0-9]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex AwsAccessKeyRegex();
}