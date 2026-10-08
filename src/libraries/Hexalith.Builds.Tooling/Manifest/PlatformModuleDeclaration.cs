// <copyright file="PlatformModuleDeclaration.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;

/// <summary>
/// Represents an immutable validated module declaration, with effective defaults materialized.
/// </summary>
/// <param name="Source">The source file.</param>
/// <param name="Declaration">The complete module declaration.</param>
public sealed record PlatformModuleDeclaration(string Source, JsonElement Declaration);