// <copyright file="PlatformAdoptionRow.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

/// <summary>A bounded static enrollment requirement, never runtime or SM-2 proof.</summary>
/// <param name="Requirement">The stable requirement identifier.</param>
/// <param name="Passed">Whether the inspected requirement passed.</param>
/// <param name="File">The inspected workspace-relative file.</param>
/// <param name="Reason">The metadata-only result and remediation.</param>
public sealed record PlatformAdoptionRow(string Requirement, bool Passed, string File, string Reason);
