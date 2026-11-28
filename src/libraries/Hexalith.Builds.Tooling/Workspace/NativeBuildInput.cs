// <copyright file="NativeBuildInput.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Names already-built output and its audited restore assets for sealing.</summary>
/// <param name="Role">The component purpose.</param>
/// <param name="Entry">The absolute compiled entry assembly.</param>
/// <param name="Assets">The absolute MSBuild restore assets path for that output.</param>
/// <param name="Project">The producing project path.</param>
internal sealed record NativeBuildInput(string Role, string Entry, string Assets, string Project);
