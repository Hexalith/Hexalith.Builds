// <copyright file="AttemptRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Binds one attempt to its release, environment-current values, timing and epoch.
/// </summary>
/// <param name="Release">The release record used by the attempt.</param>
/// <param name="AttemptKind">The declared operation kind.</param>
/// <param name="EnvironmentCurrent">The bindings actually used.</param>
/// <param name="RenderMode">The normal, recovery or remove-workloads render mode.</param>
/// <param name="LockEpoch">The environment lock epoch.</param>
/// <param name="EntryUtc">The original lock acquisition or entry time.</param>
/// <param name="FirstMutationUtc">The original first mutation time, when known.</param>
/// <param name="OutageUtc">The original outage timestamp, if any.</param>
/// <param name="PreMutationDeadlineUtc">The initial finite pre-mutation bound.</param>
/// <param name="RolloutDeadlineUtc">The applicable readiness deadline.</param>
/// <param name="VerificationDeadlineUtc">The applicable verification deadline.</param>
/// <param name="MaximumLifetimeUtc">The recorded absolute maximum-lifetime deadline.</param>
/// <param name="CheckTimeoutSeconds">The declared check timeout.</param>
/// <param name="GraceSeconds">The single attempt grace, at most the check timeout plus 30 seconds.</param>
/// <param name="RecoveryAllowance">The maximum automatic recoveries, zero or one.</param>
/// <param name="RecoveriesConsumed">The consumed automatic recovery allowance.</param>
/// <param name="ObservedStop">The exact observed stop record, if any.</param>
/// <param name="PreIncidentIngress">The pre-incident open or restricted ingress state.</param>
/// <param name="Outcome">The in-progress, working, non-working or superseded outcome.</param>
/// <param name="Supersedes">The prior nonterminal attempt content taken over, if any.</param>
/// <param name="SupersededBy">The verified successor attempt content that terminalizes this attempt.</param>
public sealed record AttemptRecord(
    RecordReference Release,
    string AttemptKind,
    EnvironmentCurrent EnvironmentCurrent,
    string RenderMode,
    long LockEpoch,
    DateTimeOffset EntryUtc,
    DateTimeOffset? FirstMutationUtc,
    DateTimeOffset? OutageUtc,
    DateTimeOffset PreMutationDeadlineUtc,
    DateTimeOffset? RolloutDeadlineUtc,
    DateTimeOffset? VerificationDeadlineUtc,
    DateTimeOffset MaximumLifetimeUtc,
    int CheckTimeoutSeconds,
    int GraceSeconds,
    int RecoveryAllowance,
    int RecoveriesConsumed,
    RecordReference? ObservedStop,
    string PreIncidentIngress,
    string Outcome,
    RecordReference? Supersedes,
    RecordReference? SupersededBy);