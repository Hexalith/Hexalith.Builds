// <copyright file="SourceMappingEntry.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>One module identity and its selected origin.</summary>
/// <param name="Identity">The module identity.</param>
/// <param name="Origin">The source or package origin.</param>
/// <param name="Path">The absolute source path, or null for a package.</param>
public sealed record SourceMappingEntry(string Identity, string Origin, string? Path);
