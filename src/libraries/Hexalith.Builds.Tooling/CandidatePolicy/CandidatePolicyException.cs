// <copyright file="CandidatePolicyException.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CandidatePolicy;

/// <summary>A stable, metadata-only candidate policy refusal.</summary>
/// <param name="condition">The violated policy condition.</param>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1032:Implement standard exception constructors", Justification = "Policy refusals expose only stable named conditions and must not accept arbitrary exception text.")]
public sealed class CandidatePolicyException(string condition) : Exception("Candidate policy refused: " + condition)
{
    /// <summary>Gets the stable policy condition.</summary>
    public string Condition { get; } = condition;
}
