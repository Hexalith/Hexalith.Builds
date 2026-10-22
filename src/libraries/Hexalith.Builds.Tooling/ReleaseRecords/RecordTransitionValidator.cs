// <copyright file="RecordTransitionValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Checks transitions against immutable verified context; callers must run these checks inside store CAS.
/// </summary>
public static class RecordTransitionValidator
{
    /// <summary>Checks an attempt revision, terminal immutability and linked successor authority.</summary>
    /// <param name="proposed">The independently verified proposed revision.</param>
    /// <param name="release">The independently verified release.</param>
    /// <param name="currentEpoch">The epoch read inside the environment store CAS.</param>
    /// <param name="previous">The prior verified attempt, if one exists.</param>
    /// <param name="takeover">The verified higher-epoch successor, required for supersession.</param>
    /// <exception cref="RecordValidationException">The proposed transition is unauthorized or inconsistent.</exception>
    public static void ValidateAttempt(
        VerifiedRecord<AttemptRecord> proposed,
        VerifiedRecord<ReleaseRecord> release,
        long currentEpoch,
        VerifiedRecord<AttemptRecord>? previous = null,
        VerifiedRecord<AttemptRecord>? takeover = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(release);
        AttemptRecord next = proposed.Record.Payload;
        Require(References(next.Release, release) && proposed.Record.Subject == release.Record.Payload.ReleaseId);
        if (previous is not null && ReleaseRecordCodec.SameContent(previous.Record, proposed.Record))
        {
            Require(next.Outcome != "in-progress" || next.LockEpoch == currentEpoch);
            return;
        }

        Require(next.LockEpoch == currentEpoch);
        if (previous is null)
        {
            Require(next.Outcome != "superseded" && takeover is null);
            return;
        }

        AttemptRecord prior = previous.Record.Payload;
        Require(prior.Outcome == "in-progress");
        Require(SameIdentity(proposed, previous) && proposed.Record.RecordedUtc >= previous.Record.RecordedUtc);
        if (next.Outcome == "superseded")
        {
            VerifiedRecord<AttemptRecord> successor = takeover ?? throw new RecordValidationException("invalid-transition");
            Require(successor.Record.RecordId != previous.Record.RecordId &&
                SameEnvironment(proposed, successor) &&
                successor.Record.Payload.LockEpoch == currentEpoch && currentEpoch > prior.LockEpoch &&
                References(successor.Record.Payload.Supersedes, previous) &&
                References(next.SupersededBy, successor) &&
                proposed.Record.WriterId == successor.Record.WriterId &&
                successor.Record.Payload.Outcome != "superseded");
            AttemptRecord restored = next with { Outcome = prior.Outcome, LockEpoch = prior.LockEpoch, SupersededBy = prior.SupersededBy };
            Require(ReleaseRecordCodec.SameContent(restored, prior));
            return;
        }

        Require(takeover is null && proposed.Record.WriterId == previous.Record.WriterId && next.LockEpoch >= prior.LockEpoch &&
            next.RecoveriesConsumed >= prior.RecoveriesConsumed);
        AttemptRecord fixedFields = next with
        {
            LockEpoch = prior.LockEpoch,
            FirstMutationUtc = prior.FirstMutationUtc,
            RolloutDeadlineUtc = prior.RolloutDeadlineUtc,
            VerificationDeadlineUtc = prior.VerificationDeadlineUtc,
            MaximumLifetimeUtc = prior.MaximumLifetimeUtc,
            RecoveriesConsumed = prior.RecoveriesConsumed,
            Outcome = prior.Outcome,
        };
        Require(ReleaseRecordCodec.SameContent(fixedFields, prior));
        Require(prior.FirstMutationUtc is null || next.FirstMutationUtc == prior.FirstMutationUtc);
        bool recoveryStarted = prior.RecoveriesConsumed == 0 && next.RecoveriesConsumed == 1;
        Require(recoveryStarted || prior.RolloutDeadlineUtc is null || next.RolloutDeadlineUtc == prior.RolloutDeadlineUtc);
        Require(recoveryStarted || prior.VerificationDeadlineUtc is null || next.VerificationDeadlineUtc == prior.VerificationDeadlineUtc);
        bool phaseStarted = recoveryStarted || (prior.RolloutDeadlineUtc is null && next.RolloutDeadlineUtc is not null) ||
            (prior.VerificationDeadlineUtc is null && next.VerificationDeadlineUtc is not null);
        if (prior.AttemptKind is "in-place-restore" or "disaster-recovery" || (next.LockEpoch > prior.LockEpoch && !recoveryStarted) || !phaseStarted)
        {
            Require(next.MaximumLifetimeUtc == prior.MaximumLifetimeUtc);
        }
        else if (next.MaximumLifetimeUtc != prior.MaximumLifetimeUtc)
        {
            Require(next.RolloutDeadlineUtc is not null || next.VerificationDeadlineUtc is not null);
            DateTimeOffset latestPhaseDeadline = new[] { next.RolloutDeadlineUtc, next.VerificationDeadlineUtc }.Max()!.Value;
            Require(next.MaximumLifetimeUtc - latestPhaseDeadline == TimeSpan.FromSeconds(next.GraceSeconds));
        }

        if (next.LockEpoch > prior.LockEpoch && !recoveryStarted)
        {
            Require(next.RolloutDeadlineUtc == prior.RolloutDeadlineUtc && next.VerificationDeadlineUtc == prior.VerificationDeadlineUtc);
        }
    }

    /// <summary>Checks that qualification is issued by its referenced attempt's writer and profile.</summary>
    /// <param name="qualification">The verified qualification.</param>
    /// <param name="release">The verified release.</param>
    /// <param name="attempt">The exact issuing verified attempt.</param>
    public static void ValidateQualification(VerifiedRecord<QualificationRecord> qualification, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> attempt)
    {
        ArgumentNullException.ThrowIfNull(qualification);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(attempt);
        QualificationRecord payload = qualification.Record.Payload;
        ValidateIssuer(qualification, payload.Release, payload.IssuingAttempt, release, attempt);
        Require(payload.ProfileDigest == attempt.Record.Payload.EnvironmentCurrent.ActiveProfileDigest);
    }

    /// <summary>Checks production promotion against its issuing attempt and qualification.</summary>
    /// <param name="promotion">The verified production promotion.</param>
    /// <param name="release">The verified release.</param>
    /// <param name="attempt">The exact issuing verified attempt.</param>
    /// <param name="qualification">The exact production qualification.</param>
    public static void ValidateProductionPromoted(VerifiedRecord<ProductionPromotedRecord> promotion, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> attempt, VerifiedRecord<QualificationRecord> qualification)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        ProductionPromotedRecord payload = promotion.Record.Payload;
        ValidateIssuer(promotion, payload.Release, payload.IssuingAttempt, release, attempt);
        ValidateQualification(qualification, release, attempt);
        Require(promotion.Record.Environment == "production" && References(payload.Qualification, qualification) &&
            payload.ProfileDigest == qualification.Record.Payload.ProfileDigest);
    }

    /// <summary>Checks publication availability against the exact working staging attempt.</summary>
    /// <param name="available">The verified publication record.</param>
    /// <param name="release">The verified release.</param>
    /// <param name="stagingAttempt">The exact working staging attempt.</param>
    public static void ValidateReleaseAvailable(VerifiedRecord<ReleaseAvailableRecord> available, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> stagingAttempt)
    {
        ArgumentNullException.ThrowIfNull(available);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(stagingAttempt);
        ReleaseAvailableRecord payload = available.Record.Payload;
        Require(References(payload.Release, release) && References(payload.StagingAttempt, stagingAttempt) &&
            available.Record.Subject == release.Record.Payload.ReleaseId && stagingAttempt.Record.Environment == "staging" &&
            References(stagingAttempt.Record.Payload.Release, release) && stagingAttempt.Record.Subject == release.Record.Payload.ReleaseId &&
            stagingAttempt.Record.Payload.Outcome == "working" && payload.ComposedImageDigest == release.Record.Payload.ComposedHostDigest);
    }

    /// <summary>Checks a latest-working move under the current CAS epoch.</summary>
    /// <param name="proposed">The verified pointer revision.</param>
    /// <param name="release">The verified release.</param>
    /// <param name="terminalAttempt">The exact working terminal attempt.</param>
    /// <param name="currentEpoch">The current environment CAS epoch.</param>
    /// <param name="previous">The prior pointer, if one exists.</param>
    public static void ValidateLatestWorking(VerifiedRecord<LatestWorkingPointerRecord> proposed, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> terminalAttempt, long currentEpoch, VerifiedRecord<LatestWorkingPointerRecord>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(terminalAttempt);
        LatestWorkingPointerRecord payload = proposed.Record.Payload;
        Require(References(payload.Release, release) && References(payload.TerminalAttempt, terminalAttempt) &&
            References(terminalAttempt.Record.Payload.Release, release) && terminalAttempt.Record.Subject == release.Record.Payload.ReleaseId &&
            terminalAttempt.Record.Payload.Outcome == "working" && SameEnvironment(proposed, terminalAttempt) &&
            proposed.Record.WriterId == terminalAttempt.Record.WriterId && payload.Epoch == terminalAttempt.Record.Payload.LockEpoch &&
            payload.Epoch == currentEpoch);
        if (previous is not null && previous.Digest != proposed.Digest)
        {
            Require(SameIdentity(proposed, previous) && payload.Epoch > previous.Record.Payload.Epoch &&
                proposed.Record.RecordedUtc >= previous.Record.RecordedUtc);
        }
    }

    /// <summary>Checks a stop-set revision against the state observed inside CAS.</summary>
    /// <param name="proposed">The verified stop-set record.</param>
    /// <param name="currentRevision">The current store stop revision, zero before the first set.</param>
    /// <param name="previous">The current verified stop, if one exists.</param>
    public static void ValidatePromotionStop(VerifiedRecord<PromotionStopRecord> proposed, long currentRevision, VerifiedRecord<PromotionStopRecord>? previous = null)
    {
        ArgumentNullException.ThrowIfNull(proposed);
        PromotionStopRecord payload = proposed.Record.Payload;
        Require(currentRevision >= 0 && currentRevision < long.MaxValue && payload.Revision == currentRevision + 1);
        Require(previous is null
            ? currentRevision == 0 && payload.PreviousStop is null
            : SameEnvironment(proposed, previous) && previous.Record.Payload.Revision == currentRevision && References(payload.PreviousStop, previous));
    }

    /// <summary>Checks revision-conditioned stop clearance and any referenced action/authority lineage.</summary>
    /// <param name="action">The verified Administrator action.</param>
    /// <param name="currentStop">The currently observed verified stop for clearance.</param>
    /// <param name="attempt">The exact attempt named by the action, if any.</param>
    /// <param name="predecessor">The exact prior Administrator authority record, if any.</param>
    public static void ValidateAdministrator(VerifiedRecord<AdministratorRecord> action, VerifiedRecord<PromotionStopRecord>? currentStop = null, VerifiedRecord<AttemptRecord>? attempt = null, VerifiedRecord<AdministratorRecord>? predecessor = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        AdministratorRecord payload = action.Record.Payload;
        if (payload.Attempt is null)
        {
            Require(attempt is null);
        }
        else
        {
            Require(attempt is not null && References(payload.Attempt, attempt) && SameEnvironment(action, attempt));
        }

        if (payload.Predecessor is null)
        {
            Require(predecessor is null);
        }
        else
        {
            Require(predecessor is not null && References(payload.Predecessor, predecessor) &&
                SameEnvironment(action, predecessor) && payload.Target == predecessor.Record.Payload.Target &&
                AuthorityFamily(payload.Action) == AuthorityFamily(predecessor.Record.Payload.Action));
        }

        if (payload.Action == "clear-promotion-stop")
        {
            Require(currentStop is not null && SameEnvironment(action, currentStop) && payload.Target == action.Record.Environment &&
                References(payload.ObservedStop, currentStop) && payload.ExpectedStopRevision == currentStop.Record.Payload.Revision &&
                payload.CauseDispositions.TryGetValue(currentStop.Record.Payload.Cause, out string? disposition) && disposition is "resolved" or "accepted");
        }
        else if (payload.ObservedStop is null)
        {
            Require(currentStop is null);
        }
        else
        {
            Require(currentStop is not null && SameEnvironment(action, currentStop) &&
                References(payload.ObservedStop, currentStop) && payload.ExpectedStopRevision == currentStop.Record.Payload.Revision);
        }
    }

    private static string AuthorityFamily(string action) => action switch
    {
        "grant-admission" or "revoke-admission" => "admission",
        "grant-management" or "revoke-management" => "management",
        _ => action,
    };

    private static bool References<T>(RecordReference? reference, VerifiedRecord<T> record)
        where T : class => reference == record.Reference;

    private static void Require(bool valid)
    {
        if (!valid)
        {
            throw new RecordValidationException("invalid-transition");
        }
    }

    private static bool SameEnvironment<T, TOther>(VerifiedRecord<T> left, VerifiedRecord<TOther> right)
        where T : class
        where TOther : class => left.Record.Environment == right.Record.Environment;

    private static bool SameIdentity<T>(VerifiedRecord<T> left, VerifiedRecord<T> right)
        where T : class => left.Record.RecordId == right.Record.RecordId && left.Record.Subject == right.Record.Subject &&
        left.Record.Environment == right.Record.Environment && left.Record.CreatedUtc == right.Record.CreatedUtc;

    private static void ValidateIssuer<T>(VerifiedRecord<T> issued, RecordReference releaseReference, RecordReference attemptReference, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> attempt)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(attempt);
        Require(References(releaseReference, release) && References(attemptReference, attempt) && References(attempt.Record.Payload.Release, release) &&
            issued.Record.Subject == release.Record.Payload.ReleaseId && attempt.Record.Subject == release.Record.Payload.ReleaseId &&
            SameEnvironment(issued, attempt) && issued.Record.WriterId == attempt.Record.WriterId &&
            attempt.Record.Payload.Outcome is "in-progress" or "working");
    }
}