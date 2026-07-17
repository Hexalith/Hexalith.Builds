// <copyright file="ModuleRunTimestamps.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.RunEvidence;

/// <summary>
/// Identifies volatile timestamps retained only for audit chronology.
/// </summary>
/// <param name="StartedUtc">The UTC invocation start timestamp.</param>
/// <param name="CompletedUtc">The UTC invocation completion timestamp.</param>
public sealed record ModuleRunTimestamps(DateTimeOffset StartedUtc, DateTimeOffset CompletedUtc);