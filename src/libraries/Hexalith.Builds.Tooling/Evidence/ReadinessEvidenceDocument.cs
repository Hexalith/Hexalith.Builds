// <copyright file="ReadinessEvidenceDocument.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Evidence;

using YamlDotNet.RepresentationModel;

/// <summary>
/// Holds a parsed readiness-evidence document and its repository context.
/// </summary>
/// <param name="FullPath">The absolute path used only for local file resolution.</param>
/// <param name="RepositoryRoot">The resolved repository root.</param>
/// <param name="Source">The metadata-only repository-relative source identity.</param>
/// <param name="Root">The parsed YAML mapping root.</param>
internal sealed record ReadinessEvidenceDocument(
    string FullPath,
    string RepositoryRoot,
    string Source,
    YamlMappingNode Root);