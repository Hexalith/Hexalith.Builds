// <copyright file="ModuleDescriptor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json.Serialization;

/// <summary>
/// Describes one module resource in a supported runtime composition.
/// </summary>
/// <param name="Id">The deterministic module identity.</param>
/// <param name="DescriptorAssembly">The repository-relative descriptor assembly path.</param>
/// <param name="Dependencies">The module identities required by this module.</param>
/// <param name="Domain">The deterministic domain identity.</param>
/// <param name="ApplicationId">The deterministic application identity.</param>
/// <param name="ResourceId">The deterministic resource identity.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ModuleDescriptor(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("descriptorAssembly")] string DescriptorAssembly,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string> Dependencies,
    [property: JsonPropertyName("domain")] string Domain,
    [property: JsonPropertyName("applicationId")] string ApplicationId,
    [property: JsonPropertyName("resourceId")] string ResourceId);