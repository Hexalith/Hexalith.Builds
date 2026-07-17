// <copyright file="ToolOutputFormat.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Defines supported diagnostic output formats.
/// </summary>
public enum ToolOutputFormat
{
    /// <summary>Writes a human-readable diagnostic.</summary>
    Human = 0,

    /// <summary>Writes a machine-readable JSON diagnostic.</summary>
    Json = 1,
}