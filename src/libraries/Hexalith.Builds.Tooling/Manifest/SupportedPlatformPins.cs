// <copyright file="SupportedPlatformPins.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Defines the P0 platform pins after applying the accepted P1 EventStore normalization.
/// </summary>
public static class SupportedPlatformPins
{
    /// <summary>Gets the authorized EventStore package pin.</summary>
    public const string EventStoreVersion = "3.70.1";

    /// <summary>Gets the observed Dapr runtime pin whose support disposition remains G-6 dependent.</summary>
    public const string DaprRuntimeVersion = "1.18.0";

    /// <summary>Gets the authorized Dapr SDK package pin.</summary>
    public const string DaprSdkVersion = "1.18.4";

    /// <summary>Gets the authorized FrontComposer package pin.</summary>
    public const string FrontComposerVersion = "4.0.1";
}