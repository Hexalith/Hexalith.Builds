// <copyright file="SupportedPlatformPins.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines the owner-approved platform pins used by the module tooling contract.
/// </summary>
public static class SupportedPlatformPins
{
    /// <summary>Gets the candidate EventStore package pin.</summary>
    public static string EventStoreVersion => PlatformVersionCatalog.Current.EventStoreVersion;

    /// <summary>Gets the approved Dapr runtime exception pin.</summary>
    public static string DaprRuntimeVersion => PlatformVersionCatalog.Current.DaprRuntimeVersion;

    /// <summary>Gets the authorized Dapr SDK package pin.</summary>
    public static string DaprSdkVersion => PlatformVersionCatalog.Current.DaprSdkVersion;

    /// <summary>Gets the authorized FrontComposer package pin.</summary>
    public static string FrontComposerVersion => PlatformVersionCatalog.Current.FrontComposerVersion;
}
