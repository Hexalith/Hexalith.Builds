// <copyright file="ModuleProfile.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json.Serialization;

/// <summary>
/// Describes an orchestratable module qualification profile.
/// </summary>
/// <param name="Fixture">The repository-relative fixture definition path.</param>
/// <param name="Classes">The supported profile classes to execute.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModuleProfile(
    [property: JsonPropertyName("fixture")] string Fixture,
    [property: JsonPropertyName("classes")] IReadOnlyList<string> Classes);