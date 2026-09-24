// <copyright file="CompositionToolchainPins.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// The selected G-6 local toolchain the runner composition requires, verified by executing it.
/// </summary>
public static class CompositionToolchainPins
{
    /// <summary>The Dapr CLI version selected by the accepted G-6 tuple.</summary>
    public const string DaprCliVersion = "1.18.0";

    /// <summary>The pinned run-scoped Redis image repository.</summary>
    public const string RedisImage = "docker.io/library/redis";

    /// <summary>The run-scoped Redis image tag, kept for readability; the digest below is authoritative.</summary>
    public const string RedisImageTag = "7.4-alpine";

    /// <summary>The immutable digest of the qualified Redis <c>7.4-alpine</c> image index.</summary>
    public const string RedisImageDigest = "sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf";

    /// <summary>Gets the digest-pinned Redis image reference (<c>repository:tag@sha256:digest</c>).</summary>
    public static string RedisImageReference => $"{RedisImage}:{RedisImageTag}@{RedisImageDigest}";

    /// <summary>Gets the Dapr runtime version selected by the accepted G-6 tuple.</summary>
    public static string DaprRuntimeVersion => SupportedPlatformPins.DaprRuntimeVersion;
}