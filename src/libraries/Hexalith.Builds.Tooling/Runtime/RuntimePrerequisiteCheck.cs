// <copyright file="RuntimePrerequisiteCheck.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Represents the runner's fail-closed decision about external platform availability.
/// </summary>
/// <param name="IsAvailable">A value indicating whether composition may proceed.</param>
/// <param name="Diagnostic">The stable diagnostic when composition must not proceed.</param>
public sealed record RuntimePrerequisiteCheck(bool IsAvailable, ToolDiagnostic? Diagnostic);