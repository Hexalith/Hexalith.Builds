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
    /// <summary>Gets the Dapr CLI version selected by the accepted G-6 tuple.</summary>
    public static string DaprCliVersion => PlatformVersionCatalog.Current.DaprCliVersion;

    /// <summary>Gets the pinned run-scoped Redis image repository.</summary>
    public static string RedisImage => PlatformVersionCatalog.Current.RedisImage;

    /// <summary>Gets the run-scoped Redis image tag, kept for readability; the digest below is authoritative.</summary>
    public static string RedisImageTag => PlatformVersionCatalog.Current.RedisImageTag;

    /// <summary>Gets the immutable digest of the catalog-selected Redis image index.</summary>
    public static string RedisImageDigest => PlatformVersionCatalog.Current.RedisImageDigest;

    /// <summary>Gets the digest-pinned Redis image reference (<c>repository:tag@sha256:digest</c>).</summary>
    public static string RedisImageReference => $"{RedisImage}:{RedisImageTag}@{RedisImageDigest}";

    /// <summary>Gets the selected Aspire AppHost SDK and required CLI version.</summary>
    public static string AspireAppHostSdkVersion => PlatformVersionCatalog.Current.AspireAppHostSdkVersion;

    /// <summary>Gets the Dapr runtime version selected by the accepted G-6 tuple.</summary>
    public static string DaprRuntimeVersion => SupportedPlatformPins.DaprRuntimeVersion;
}