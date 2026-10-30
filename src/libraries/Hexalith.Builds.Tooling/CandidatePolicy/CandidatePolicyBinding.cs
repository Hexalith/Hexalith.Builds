// <copyright file="CandidatePolicyBinding.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CandidatePolicy;

using System.Text.Json;

/// <summary>Immutable authenticated decision and producing attempt carried by the runner.</summary>
/// <param name="RunId">The signed producing run.</param>
/// <param name="AttemptId">The distinct signed attempt.</param>
/// <param name="AttemptSequence">The GitHub-issued retry sequence.</param>
/// <param name="Candidate">The existing v1 candidate representation.</param>
/// <param name="Decision">The existing v1 decision representation.</param>
/// <param name="ReuseLineage">Explicit own production and empty reused sources.</param>
public sealed record CandidatePolicyBinding(string RunId, string AttemptId, int AttemptSequence, JsonElement Candidate, JsonElement Decision, JsonElement ReuseLineage);
