// <copyright file="ExecutableUiMarker.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Identifies a validated module UI marker for FrontComposer registration.
/// </summary>
/// <param name="ModuleId">The declared module identity.</param>
/// <param name="UiAssembly">The physically contained UI assembly.</param>
/// <param name="UiMarkerType">The loadable, fully qualified marker type.</param>
public sealed record ExecutableUiMarker(string ModuleId, string UiAssembly, string UiMarkerType);
