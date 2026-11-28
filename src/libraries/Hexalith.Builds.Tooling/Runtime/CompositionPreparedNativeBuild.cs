// <copyright file="CompositionPreparedNativeBuild.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Authenticates the verified inventory from the runner to the private AppHost.</summary>
public static class CompositionPreparedNativeBuild
{
    private const string _refusal = "Prepared native build plan does not match the verified owner handoff.";

    /// <summary>Checks the original inventory and refuses if the parent required one and it was removed.</summary>
    /// <param name="plan">The received plan.</param>
    /// <param name="signingKey">The per-run key privately supplied by the parent.</param>
    /// <param name="required">Whether the parent requires prepared execution.</param>
    /// <exception cref="InvalidDataException">The plan is missing, modified or replayed.</exception>
    public static void Verify(CompositionRunPlan plan, string? signingKey, bool required)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PreparedNativeBuild is null && plan.PreparedNativeBuildBinding is null && !required)
        {
            return;
        }

        string? binding = plan.PreparedNativeBuildBinding;
        if (!required || plan.PreparedNativeBuild is null || string.IsNullOrEmpty(signingKey)
            || binding is not { Length: 64 } || binding.Any(character => !char.IsAsciiHexDigitLower(character))
            || !CryptographicOperations.FixedTimeEquals(Tag(plan, signingKey), Convert.FromHexString(binding)))
        {
            throw new InvalidDataException(_refusal);
        }
    }

    /// <summary>Binds the inventory to its issued run.</summary>
    /// <param name="plan">The complete prepared plan.</param>
    /// <param name="signingKey">The per-run key.</param>
    /// <returns>The authenticated plan.</returns>
    internal static CompositionRunPlan Bind(CompositionRunPlan plan, string signingKey) => plan.PreparedNativeBuild is null
        ? plan
        : plan with { PreparedNativeBuildBinding = Convert.ToHexStringLower(Tag(plan, signingKey)) };

    private static byte[] Tag(CompositionRunPlan plan, string signingKey)
    {
        Span<byte> key = stackalloc byte[48];
        try
        {
            return Convert.TryFromBase64String(signingKey, key, out int length) && length == key.Length
                ? HMACSHA256.HashData(key, JsonSerializer.SerializeToUtf8Bytes(plan with { PreparedNativeBuildBinding = null }))
                : throw new InvalidDataException(_refusal);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }
}
