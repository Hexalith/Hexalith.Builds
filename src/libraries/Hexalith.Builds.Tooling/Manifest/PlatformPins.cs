// <copyright file="PlatformPins.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json.Serialization;

/// <summary>
/// Identifies the platform pins qualified by a module manifest.
/// </summary>
/// <param name="EventStoreVersion">The EventStore package version.</param>
/// <param name="DaprRuntimeVersion">The Dapr runtime version.</param>
/// <param name="DaprSdkVersion">The Dapr SDK package version.</param>
/// <param name="FrontComposerVersion">The FrontComposer package version.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PlatformPins(
    [property: JsonPropertyName("eventStoreVersion")] string EventStoreVersion,
    [property: JsonPropertyName("daprRuntimeVersion")] string DaprRuntimeVersion,
    [property: JsonPropertyName("daprSdkVersion")] string DaprSdkVersion,
    [property: JsonPropertyName("frontComposerVersion")] string FrontComposerVersion);