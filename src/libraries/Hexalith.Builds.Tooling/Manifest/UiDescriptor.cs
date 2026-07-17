// <copyright file="UiDescriptor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json.Serialization;

/// <summary>
/// Describes an optional FrontComposer UI descriptor.
/// </summary>
/// <param name="DescriptorAssembly">The repository-relative UI descriptor assembly path.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UiDescriptor(
    [property: JsonPropertyName("descriptorAssembly")] string DescriptorAssembly);