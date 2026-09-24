// <copyright file="CompositionRunUiMarker.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// One validated FrontComposer module UI marker binding in a run plan.
/// </summary>
/// <param name="ModuleId">The owning manifest module identity.</param>
/// <param name="AssemblyPath">The absolute, descriptor-validated marker assembly path.</param>
/// <param name="MarkerType">The full CLR marker type name.</param>
public sealed record CompositionRunUiMarker(string ModuleId, string AssemblyPath, string MarkerType);