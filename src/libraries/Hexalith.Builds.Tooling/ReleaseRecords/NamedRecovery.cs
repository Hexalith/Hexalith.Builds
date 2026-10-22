// <copyright file="NamedRecovery.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Binds a planned recovery to its acceptance checks and finite duration.
/// </summary>
/// <param name="RecoveryId">The named separately planned recovery.</param>
/// <param name="AcceptanceChecks">The required acceptance checks.</param>
/// <param name="MaximumDurationSeconds">The declared recovery duration bound.</param>
public sealed record NamedRecovery(
    string RecoveryId,
    ImmutableArray<string> AcceptanceChecks,
    int MaximumDurationSeconds);