// <copyright file="StagingEvidenceBinding.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>Binds an operational record to one staging-evidence policy and promotion decision.</summary>
/// <param name="PolicyId">The exact immutable policy identifier.</param>
/// <param name="DecisionId">The promotion decision identifier.</param>
public sealed record StagingEvidenceBinding(string PolicyId, string DecisionId)
{
    /// <summary>The Platform staging-evidence policy identifier accepted by the v2 schemas.</summary>
    public const string PolicyV2 = "hexalith.platform.staging-evidence-policy.v2";
}