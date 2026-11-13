// <copyright file="SupportedPlatformPins.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines the frozen v1 manifest and acceptance pins; current v2 catalogs have separate authority.
/// </summary>
public static class SupportedPlatformPins
{
    /// <summary>Gets the frozen v1 EventStore package pin.</summary>
    public static string EventStoreVersion => "4.2.0";

    /// <summary>Gets the frozen v1 Dapr runtime pin.</summary>
    public static string DaprRuntimeVersion => "1.19.1";

    /// <summary>Gets the frozen v1 Dapr SDK package pin.</summary>
    public static string DaprSdkVersion => "1.19.1";

    /// <summary>Gets the frozen v1 FrontComposer package pin.</summary>
    public static string FrontComposerVersion => "5.0.0";
}
