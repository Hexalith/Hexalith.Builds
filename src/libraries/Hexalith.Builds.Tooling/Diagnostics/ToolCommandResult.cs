// <copyright file="ToolCommandResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

using System.Text.Json.Serialization;

/// <summary>
/// Represents an execution result safe to render to command output.
/// </summary>
/// <param name="Status">The non-passing or completed invocation status.</param>
/// <param name="Outcome">The first causal invocation outcome.</param>
/// <param name="Diagnostics">The metadata-only diagnostics.</param>
public sealed record ToolCommandResult(
    string Status,
    ToolOutcome Outcome,
    IReadOnlyList<ToolDiagnostic> Diagnostics)
{
    /// <summary>Gets the optional bounded static enrollment report.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformAdoptionRow>? Adoption { get; init; }

    /// <summary>Gets the static-only scope and pending human SM-2 verification.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AdoptionScope { get; init; }

    /// <summary>
    /// Gets the run identity returned by a public composition command, when allocated.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>Gets exact known leftovers and explicit discovery uncertainty.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Runtime.CompositionRunResources? RemainingResources { get; init; }

    /// <summary>Gets the tracked environments for a read-only list command.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<Runtime.CompositionRunListingEntry>? Environments { get; init; }

    /// <summary>Gets the durable v1 result location, when one was published.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TestResultPath { get; init; }
}
