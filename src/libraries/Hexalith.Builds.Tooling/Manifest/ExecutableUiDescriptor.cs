// <copyright file="ExecutableUiDescriptor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Contains the validated FrontComposer project and module marker bindings.
/// </summary>
/// <param name="UiProject">The physically contained UI host project.</param>
/// <param name="ModuleUiMarkers">The declared module UI marker bindings.</param>
public sealed record ExecutableUiDescriptor(string UiProject, IReadOnlyList<ExecutableUiMarker> ModuleUiMarkers);
