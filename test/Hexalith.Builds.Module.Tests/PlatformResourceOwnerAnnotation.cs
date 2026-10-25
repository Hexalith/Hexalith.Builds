// <copyright file="PlatformResourceOwnerAnnotation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Aspire.Hosting.ApplicationModel;

/// <summary>Mirrors the explicit Platform resource attribution across the model assembly boundary.</summary>
/// <param name="ModuleId">The actual declaring module.</param>
internal sealed record PlatformResourceOwnerAnnotation(string? ModuleId) : IResourceAnnotation;
