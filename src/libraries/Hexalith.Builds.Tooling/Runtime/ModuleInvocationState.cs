// <copyright file="ModuleInvocationState.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Represents metadata-only, runner-owned invocation state.
/// </summary>
/// <param name="RunId">The invocation identity.</param>
/// <param name="Command">The command identity.</param>
/// <param name="ManifestHash">The SHA-256 manifest fingerprint.</param>
/// <param name="Profile">The optional profile identity.</param>
/// <param name="FilterHash">The optional SHA-256 filter fingerprint.</param>
/// <param name="TenantNamespace">The run-unique tenant namespace.</param>
/// <param name="ResourceNamespace">The run-unique resource namespace.</param>
/// <param name="StartedAt">The invocation start timestamp.</param>
public sealed record ModuleInvocationState(
    string RunId,
    ModuleInvocationCommand Command,
    string ManifestHash,
    string? Profile,
    string? FilterHash,
    string TenantNamespace,
    string ResourceNamespace,
    DateTimeOffset StartedAt);