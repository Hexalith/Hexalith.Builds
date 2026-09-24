// <copyright file="ExecutableModuleDescriptor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Contains validated paths and identity from one executable module descriptor.
/// </summary>
/// <param name="ModuleId">The declared module identity.</param>
/// <param name="DomainServiceProject">The physically contained domain-service project.</param>
/// <param name="ApiProject">The optional physically contained API project.</param>
/// <param name="UiAssembly">The optional physically contained UI assembly.</param>
/// <param name="UiMarkerType">The optional fully qualified UI marker type.</param>
public sealed record ExecutableModuleDescriptor(
    string ModuleId,
    string DomainServiceProject,
    string? ApiProject,
    string? UiAssembly,
    string? UiMarkerType);
