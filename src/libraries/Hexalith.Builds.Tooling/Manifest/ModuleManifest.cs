// <copyright file="ModuleManifest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json.Serialization;

/// <summary>
/// Represents the strict <c>hexalith.module-manifest.v1</c> contract.
/// </summary>
/// <param name="Schema">The schema identity.</param>
/// <param name="Id">The deterministic manifest identity.</param>
/// <param name="Modules">The module descriptors.</param>
/// <param name="Platform">The qualified platform pins.</param>
/// <param name="Ui">The optional UI descriptor.</param>
/// <param name="Profiles">The named qualification profiles.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModuleManifest(
    [property: JsonPropertyName("schema")] string Schema,
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("modules")] IReadOnlyList<ModuleDescriptor> Modules,
    [property: JsonPropertyName("platform")] PlatformPins Platform,
    [property: JsonPropertyName("ui")] UiDescriptor? Ui,
    [property: JsonPropertyName("profiles")] IReadOnlyDictionary<string, ModuleProfile> Profiles);