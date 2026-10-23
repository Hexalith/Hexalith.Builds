// <copyright file="TestEnvironmentContractReadResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestEnvironment;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>Contains one detached, validated contract or metadata-only refusal diagnostics.</summary>
/// <param name="Document">The complete detached JSON value, or null on any refusal.</param>
/// <param name="Diagnostics">Stable diagnostics without input values, keys or paths.</param>
public sealed record TestEnvironmentContractReadResult(
    JsonElement? Document,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>Gets a value indicating whether the complete document can be consumed.</summary>
    public bool IsValid => Document.HasValue && Diagnostics.Count == 0;
}