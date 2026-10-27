// <copyright file="BuildPreparation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Describes verified imports for a build without allocating a runtime identity.</summary>
/// <param name="Workspace">The fresh private output directory.</param>
/// <param name="Mode">The selected source or package mode.</param>
/// <param name="MappingHash">The immutable mapping hash.</param>
/// <param name="Environment">The explicit MSBuild import environment.</param>
public sealed record BuildPreparation(
    string Workspace,
    string Mode,
    string MappingHash,
    IReadOnlyDictionary<string, string> Environment);
