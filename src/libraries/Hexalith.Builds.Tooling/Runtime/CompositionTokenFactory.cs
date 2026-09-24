// <copyright file="CompositionTokenFactory.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>
/// Mints short-lived HS256 development tokens from the per-run signing key, in memory only.
/// </summary>
public static class CompositionTokenFactory
{
    /// <summary>
    /// Creates a signed token accepted by the run's EventStore host.
    /// </summary>
    /// <param name="signingKey">The per-run signing key.</param>
    /// <param name="request">The identity.</param>
    /// <param name="now">The issue time.</param>
    /// <returns>The compact JWT.</returns>
    public static string Create(string signingKey, CompositionTokenRequest request, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(signingKey);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);

        Dictionary<string, object> claims = new(StringComparer.Ordinal)
        {
            ["iss"] = CompositionEnvironment.TokenIssuer,
            ["aud"] = CompositionEnvironment.TokenAudience,
            ["sub"] = request.Subject,
            ["iat"] = now.ToUnixTimeSeconds(),
            ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(),
            ["exp"] = now.Add(request.Lifetime).ToUnixTimeSeconds(),
        };
        if (request.Tenants.Count > 0)
        {
            claims["tenants"] = string.Join(' ', request.Tenants);
        }

        if (request.Domains.Count > 0)
        {
            claims["domains"] = string.Join(' ', request.Domains);
        }

        if (request.Permissions.Count > 0)
        {
            claims["permissions"] = string.Join(' ', request.Permissions);
        }

        if (request.GlobalAdministrator)
        {
            claims["role"] = "GlobalAdministrator";
        }

        string header = Encode(JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(StringComparer.Ordinal) { ["alg"] = "HS256", ["typ"] = "JWT" }));
        string payload = Encode(JsonSerializer.SerializeToUtf8Bytes(claims));
        string signed = header + "." + payload;
        byte[] signature = HMACSHA256.HashData(Encoding.UTF8.GetBytes(signingKey), Encoding.ASCII.GetBytes(signed));
        return signed + "." + Encode(signature);
    }

    private static string Encode(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}