// <copyright file="RecordTransitionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.ReleaseRecords;

using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;

using Hexalith.Builds.Tooling.ReleaseRecords;

using Shouldly;

using Xunit;

/// <summary>Exercises verified-only state transitions and the Platform writer matrix.</summary>
public sealed class RecordTransitionTests
{
    /// <summary>Every mutation of a terminal attempt fails, including replacement of its writer.</summary>
    /// <param name="change">The proposed terminal mutation.</param>
    [Theory]
    [InlineData("writer")]
    [InlineData("outcome")]
    [InlineData("epoch")]
    [InlineData("timing")]
    [InlineData("configuration")]
    [InlineData("recorded")]
    public void TerminalAttemptIsImmutable(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> terminal = Attempt(context, release, outcome: "working");
        SignedRecord<AttemptRecord> candidate = terminal.Record;
        candidate = change switch
        {
            "writer" => candidate with { WriterId = "other", KeyId = "other-key" },
            "outcome" => candidate with { Payload = candidate.Payload with { Outcome = "non-working" } },
            "epoch" => candidate with { Payload = candidate.Payload with { LockEpoch = 2 } },
            "timing" => candidate with { Payload = candidate.Payload with { MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(10) } },
            "configuration" => candidate with { Payload = candidate.Payload with { EnvironmentCurrent = candidate.Payload.EnvironmentCurrent with { CatalogGeneration = 2 } } },
            _ => candidate with { RecordedUtc = RecordTestContext.Now.AddSeconds(1) },
        };
        VerifiedRecord<AttemptRecord> changed = context.Verify(candidate);
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(changed, release, changed.Record.Payload.LockEpoch, terminal)).Code.ShouldBe("invalid-transition");
        RecordTransitionValidator.ValidateAttempt(terminal, release, 99, terminal);
    }

    /// <summary>A different valid ECDSA proof does not qualify as an exact terminal replay.</summary>
    [Fact]
    public void TerminalReplayRequiresTheExactCanonicalEnvelope()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> terminal = Attempt(context, release, outcome: "working");
        byte[] signature = Convert.FromBase64String(terminal.Record.Signature);
        BigInteger order = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        BigInteger scalar = new(signature.AsSpan(32), isUnsigned: true, isBigEndian: true);
        byte[] alternateScalar = (order - scalar).ToByteArray(isUnsigned: true, isBigEndian: true);
        signature.AsSpan(32).Clear();
        alternateScalar.CopyTo(signature.AsSpan(64 - alternateScalar.Length));
        SignedRecord<AttemptRecord> alternate = terminal.Record with { Signature = Convert.ToBase64String(signature) };
        VerifiedRecord<AttemptRecord> verified = ReleaseRecordCodec.Verify<AttemptRecord>(ReleaseRecordCodec.Encode(alternate), context.Policy).Verified.ShouldNotBeNull();
        verified.Digest.ShouldBe(terminal.Digest);
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(verified, release, 1, terminal));
    }

    /// <summary>Active exact replay still requires the current CAS epoch.</summary>
    [Fact]
    public void ActiveReplayCannotUseAnObsoleteEpoch()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> active = Attempt(context, release);
        RecordTransitionValidator.ValidateAttempt(active, release, 1, active);
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(active, release, 2, active)).Code.ShouldBe("invalid-transition");
        VerifiedRecord<AttemptRecord> terminal = Attempt(context, release, "working");
        RecordTransitionValidator.ValidateAttempt(terminal, release, 2, terminal);
    }

    /// <summary>Active revisions preserve writer, identity, timing and environment bindings.</summary>
    /// <param name="change">The proposed invalid revision.</param>
    [Theory]
    [InlineData("writer")]
    [InlineData("first-mutation")]
    [InlineData("record-id")]
    [InlineData("created-time")]
    [InlineData("recorded-time")]
    [InlineData("operations-definitions")]
    [InlineData("subject")]
    [InlineData("rollout-at-new-epoch")]
    [InlineData("verification-at-new-epoch")]
    public void ActiveRevisionsPreserveExistingAuthorityAndHistory(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        DateTimeOffset now = RecordTestContext.Now;
        AttemptRecord initial = RecordTestContext.Attempt(release.Reference) with { FirstMutationUtc = now };
        SignedRecord<AttemptRecord> envelope = RecordTestContext.Envelope(initial, "production", "production", "attempt") with { RecordedUtc = now.AddSeconds(10) };
        VerifiedRecord<AttemptRecord> previous = context.Verify(envelope);
        SignedRecord<AttemptRecord> next = change switch
        {
            "writer" => envelope with { WriterId = "other", KeyId = "other-key" },
            "first-mutation" => envelope with { Payload = initial with { FirstMutationUtc = now.AddSeconds(1) } },
            "record-id" => envelope with { RecordId = "different-attempt" },
            "created-time" => envelope with { CreatedUtc = now.AddSeconds(-1) },
            "recorded-time" => envelope with { RecordedUtc = now.AddSeconds(9) },
            "operations-definitions" => envelope with
            {
                Payload = initial with { EnvironmentCurrent = initial.EnvironmentCurrent with { OperationsDefinitionsDigest = "sha256:" + new string('b', 64) } },
            },
            "subject" => envelope with { Subject = "different-release" },
            "rollout-at-new-epoch" => envelope with { Payload = initial with { LockEpoch = 2, RolloutDeadlineUtc = now.AddMinutes(4) } },
            _ => envelope with { Payload = initial with { LockEpoch = 2, VerificationDeadlineUtc = now.AddMinutes(4) } },
        };
        VerifiedRecord<AttemptRecord> proposed = context.Verify(next);
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(proposed, release, next.Payload.LockEpoch, previous)).Code.ShouldBe("invalid-transition");
        if (change == "subject")
        {
            Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(proposed, release, next.Payload.LockEpoch)).Code.ShouldBe("invalid-transition");
        }
    }

    /// <summary>Established ordinary phase deadlines and lifetime cannot be renewed or removed.</summary>
    /// <param name="change">The unsupported renewal.</param>
    [Theory]
    [InlineData("rollout")]
    [InlineData("verification")]
    [InlineData("lifetime")]
    [InlineData("remove-rollout")]
    [InlineData("remove-verification")]
    public void OrdinaryAttemptsPreserveEstablishedDeadlines(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        AttemptRecord initial = RecordTestContext.Attempt(release.Reference) with
        {
            RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(10),
            VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(15),
            MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(16),
        };
        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(initial, "production", "production", "attempt"));
        AttemptRecord changed = change switch
        {
            "rollout" => initial with { RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(11) },
            "verification" => initial with { VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(15).AddSeconds(1) },
            "lifetime" => initial with { MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(17) },
            "remove-rollout" => initial with { RolloutDeadlineUtc = null },
            _ => initial with { VerificationDeadlineUtc = null },
        };
        VerifiedRecord<AttemptRecord> proposed = context.Verify(prior.Record with { Payload = changed });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(proposed, release, 1, prior)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>First phase deadlines can populate with their corresponding lifetime plus grace.</summary>
    /// <param name="phase">The phase that starts.</param>
    [Theory]
    [InlineData("rollout")]
    [InlineData("verification")]
    public void OrdinaryAttemptsAllowInitialPhaseProgress(string phase)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        AttemptRecord initial = RecordTestContext.Attempt(release.Reference);
        if (phase == "verification")
        {
            initial = initial with { RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(10), MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(10).AddSeconds(30) };
        }

        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(initial, "production", "production", "attempt"));
        AttemptRecord progress = phase == "rollout"
            ? initial with { RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(10), MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(10).AddSeconds(30) }
            : initial with { VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(15), MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(15).AddSeconds(30) };
        VerifiedRecord<AttemptRecord> proposed = context.Verify(prior.Record with { Payload = progress });
        RecordTransitionValidator.ValidateAttempt(proposed, release, 1, prior);
        VerifiedRecord<AttemptRecord> unrelatedLifetime = context.Verify(prior.Record with { Payload = progress with { MaximumLifetimeUtc = progress.MaximumLifetimeUtc.AddMinutes(1) } });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(unrelatedLifetime, release, 1, prior)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>The single consumed recovery can replace candidate phases once, including after resumption.</summary>
    /// <param name="epoch">The current epoch at recovery entry.</param>
    [Theory]
    [InlineData(1L)]
    [InlineData(2L)]
    public void SingleAutomaticRecoveryCanStartItsOwnPhases(long epoch)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        AttemptRecord initial = RecordTestContext.Attempt(release.Reference) with
        {
            FirstMutationUtc = RecordTestContext.Now,
            RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(10),
            VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(15),
            MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(15).AddSeconds(30),
        };
        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(initial, "production", "production", "attempt"));
        AttemptRecord recovery = initial with
        {
            LockEpoch = epoch,
            RecoveriesConsumed = 1,
            RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(20),
            VerificationDeadlineUtc = null,
            MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(20).AddSeconds(30),
        };
        VerifiedRecord<AttemptRecord> started = context.Verify(prior.Record with { Payload = recovery });
        RecordTransitionValidator.ValidateAttempt(started, release, epoch, prior);
        VerifiedRecord<AttemptRecord> verification = context.Verify(started.Record with
        {
            Payload = recovery with { VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(25), MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(25).AddSeconds(30) },
        });
        RecordTransitionValidator.ValidateAttempt(verification, release, epoch, started);
        VerifiedRecord<AttemptRecord> renewed = context.Verify(verification.Record with
        {
            Payload = verification.Record.Payload with
            {
                RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(21),
                VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(26),
                MaximumLifetimeUtc = RecordTestContext.Now.AddMinutes(26).AddSeconds(30),
            },
        });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(renewed, release, epoch, verification)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Recovery phases may proceed after the fixed notification lifetime boundary.</summary>
    [Fact]
    public void RecoveryPhasesCanProgressBeyondTheFixedLifetime()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        AttemptRecord initial = RecordTestContext.Attempt(release.Reference) with { AttemptKind = "in-place-restore", RecoveryAllowance = 0 };
        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(initial, "staging", "staging", "recovery"));
        VerifiedRecord<AttemptRecord> progress = context.Verify(prior.Record with
        {
            Payload = initial with { RolloutDeadlineUtc = RecordTestContext.Now.AddMinutes(10), VerificationDeadlineUtc = RecordTestContext.Now.AddMinutes(15) },
        });
        RecordTransitionValidator.ValidateAttempt(progress, release, 1, prior);
        progress.Record.Payload.MaximumLifetimeUtc.ShouldBe(initial.MaximumLifetimeUtc);
    }

    /// <summary>Only the exact linked higher-epoch successor's writer can supersede a predecessor.</summary>
    /// <param name="change">The missing or conflicting takeover evidence.</param>
    [Theory]
    [InlineData("none")]
    [InlineData("missing")]
    [InlineData("wrong-writer")]
    [InlineData("wrong-link")]
    [InlineData("wrong-environment")]
    [InlineData("stale-epoch")]
    [InlineData("same-epoch")]
    [InlineData("wrong-successor-reference")]
    [InlineData("changed-predecessor-payload")]
    [InlineData("no-predecessor")]
    public void SupersessionRequiresExactSuccessorEvidence(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> prior = Attempt(context, release);
        long currentEpoch = change == "same-epoch" ? 1 : 2;
        long successorEpoch = change == "stale-epoch" ? 1 : currentEpoch;
        RecordReference supersedes = change == "wrong-link" ? prior.Reference with { RecordId = "unrelated" } : prior.Reference;
        string successorEnvironment = change == "wrong-environment" ? "staging" : "production";
        AttemptRecord successorPayload = RecordTestContext.Attempt(release.Reference, successorEpoch) with { Supersedes = supersedes };
        VerifiedRecord<AttemptRecord> successor = context.Verify(RecordTestContext.Envelope(successorPayload, "dual", successorEnvironment, "successor"));
        string terminalWriter = change == "wrong-writer" ? "production" : successor.Record.WriterId;
        RecordReference supersededBy = change == "wrong-successor-reference" ? successor.Reference with { RecordId = "unrelated" } : successor.Reference;
        AttemptRecord terminalPayload = prior.Record.Payload with { Outcome = "superseded", LockEpoch = currentEpoch, SupersededBy = supersededBy };
        if (change == "changed-predecessor-payload")
        {
            terminalPayload = terminalPayload with { PreIncidentIngress = "restricted" };
        }

        VerifiedRecord<AttemptRecord> superseded = context.Verify(prior.Record with
        {
            WriterId = terminalWriter,
            KeyId = terminalWriter + "-key",
            Payload = terminalPayload,
        });
        if (change == "none")
        {
            RecordTransitionValidator.ValidateAttempt(superseded, release, currentEpoch, prior, successor);
        }
        else
        {
            VerifiedRecord<AttemptRecord>? previous = change == "no-predecessor" ? null : prior;
            VerifiedRecord<AttemptRecord>? takeover = change is "missing" or "no-predecessor" ? null : successor;
            Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(superseded, release, currentEpoch, previous, takeover)).Code.ShouldBe("invalid-transition");
        }
    }

    /// <summary>A recovery-executor-only DR writer can supersede an ordinary release attempt.</summary>
    [Fact]
    public void DisasterRecoveryWriterCanTerminalizeTheReleaseItTakesOver()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> prior = Attempt(context, release);
        AttemptRecord recovery = RecordTestContext.Attempt(release.Reference, 2) with { AttemptKind = "disaster-recovery", RecoveryAllowance = 0, RenderMode = "recovery", Supersedes = prior.Reference };
        VerifiedRecord<AttemptRecord> successor = context.Verify(RecordTestContext.Envelope(recovery, "recovery", "production", "dr"));
        VerifiedRecord<AttemptRecord> superseded = context.Verify(prior.Record with
        {
            WriterId = "recovery",
            KeyId = "recovery-key",
            Payload = prior.Record.Payload with { LockEpoch = 2, Outcome = "superseded", SupersededBy = successor.Reference },
        });
        RecordTransitionValidator.ValidateAttempt(superseded, release, 2, prior, successor);
        RecordTransitionValidator.ValidateAttempt(successor, release, 2);
    }

    /// <summary>Same-attempt resumption cannot restart timers or replenish recovery.</summary>
    [Fact]
    public void ResumptionPreservesTimingAndConsumedRecovery()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(RecordTestContext.Attempt(release.Reference) with { RecoveriesConsumed = 1 }, "production", "production", "attempt"));
        VerifiedRecord<AttemptRecord> resumed = context.Verify(prior.Record with { Payload = prior.Record.Payload with { LockEpoch = 2 } });
        RecordTransitionValidator.ValidateAttempt(resumed, release, 2, prior);
        VerifiedRecord<AttemptRecord> replenished = context.Verify(resumed.Record with { Payload = resumed.Record.Payload with { RecoveriesConsumed = 0 } });
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(replenished, release, 2, prior));
        VerifiedRecord<AttemptRecord> restarted = context.Verify(resumed.Record with { Payload = resumed.Record.Payload with { MaximumLifetimeUtc = RecordTestContext.Now.AddHours(1) } });
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(restarted, release, 2, prior));
    }

    /// <summary>Recovery phase progress cannot extend the absolute recovery lifetime.</summary>
    /// <param name="kind">The recovery attempt kind.</param>
    /// <param name="writer">The enrolled writer for that kind.</param>
    [Theory]
    [InlineData("in-place-restore", "production")]
    [InlineData("disaster-recovery", "recovery")]
    public void RecoveryLifetimeCannotBeExtendedWithinTheSameEpoch(string kind, string writer)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        AttemptRecord recovery = RecordTestContext.Attempt(release.Reference) with { AttemptKind = kind, RecoveryAllowance = 0, RenderMode = "recovery" };
        VerifiedRecord<AttemptRecord> prior = context.Verify(RecordTestContext.Envelope(recovery, writer, "production", "recovery"));
        DateTimeOffset rollout = RecordTestContext.Now.AddMinutes(10);
        AttemptRecord progress = recovery with { RolloutDeadlineUtc = rollout };
        RecordTransitionValidator.ValidateAttempt(context.Verify(prior.Record with { Payload = progress }), release, 1, prior);
        VerifiedRecord<AttemptRecord> extended = context.Verify(prior.Record with { Payload = progress with { MaximumLifetimeUtc = rollout.AddSeconds(recovery.GraceSeconds) } });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(extended, release, 1, prior)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Qualification and production-promoted belong to the exact issuing attempt's writer.</summary>
    /// <param name="wrongWriter">Whether to substitute another otherwise entitled writer.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void IssuingAttemptOwnsQualificationAndPromotion(bool wrongWriter)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> attempt = Attempt(context, release);
        string writer = wrongWriter ? "other" : "production";
        VerifiedRecord<QualificationRecord> qualification = context.Verify(RecordTestContext.Envelope(new QualificationRecord(release.Reference, attempt.Reference, RecordTestContext.Digest, [], [], [RecordTestContext.Digest]), writer, "production", "qualification"));
        VerifiedRecord<ProductionPromotedRecord> promotion = context.Verify(RecordTestContext.Envelope(new ProductionPromotedRecord(release.Reference, attempt.Reference, qualification.Reference, RecordTestContext.Digest), writer, "production", "promotion"));
        if (wrongWriter)
        {
            _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateQualification(qualification, release, attempt));
            _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateProductionPromoted(promotion, release, attempt, qualification));
        }
        else
        {
            RecordTransitionValidator.ValidateQualification(qualification, release, attempt);
            RecordTransitionValidator.ValidateProductionPromoted(promotion, release, attempt, qualification);
        }
    }

    /// <summary>Qualification and production promotion require the exact valid attempt and release.</summary>
    /// <param name="change">The inconsistent subject, outcome, environment or reference.</param>
    [Theory]
    [InlineData("release-reference")]
    [InlineData("attempt-reference")]
    [InlineData("attempt-release-reference")]
    [InlineData("record-subject")]
    [InlineData("attempt-subject")]
    [InlineData("attempt-outcome")]
    [InlineData("attempt-environment")]
    public void IssuanceRejectsIncorrectVerifiedContext(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        RecordReference otherRelease = release.Reference with { Digest = "sha256:" + new string('b', 64) };
        RecordReference attemptRelease = change == "attempt-release-reference" ? otherRelease : release.Reference;
        string outcome = change == "attempt-outcome" ? "non-working" : "in-progress";
        string environment = change == "attempt-environment" ? "staging" : "production";
        string attemptSubject = change == "attempt-subject" ? "another-release" : "release-1";
        AttemptRecord attemptPayload = RecordTestContext.Attempt(attemptRelease, outcome: outcome);
        VerifiedRecord<AttemptRecord> attempt = context.Verify(RecordTestContext.Envelope(attemptPayload, "dual", environment, "attempt", attemptSubject));
        RecordReference releaseReference = change == "release-reference" ? otherRelease : release.Reference;
        RecordReference attemptReference = change == "attempt-reference" ? attempt.Reference with { RecordId = "different-attempt" } : attempt.Reference;
        string subject = change == "record-subject" ? "another-release" : "release-1";
        QualificationRecord qualificationPayload = new(releaseReference, attemptReference, RecordTestContext.Digest, [], [], []);
        VerifiedRecord<QualificationRecord> qualification = context.Verify(RecordTestContext.Envelope(qualificationPayload, "dual", "production", "qualification", subject));
        ProductionPromotedRecord promotionPayload = new(releaseReference, attemptReference, qualification.Reference, RecordTestContext.Digest);
        VerifiedRecord<ProductionPromotedRecord> promotion = context.Verify(RecordTestContext.Envelope(promotionPayload, "dual", "production", "promotion", subject));
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateQualification(qualification, release, attempt)).Code.ShouldBe("invalid-transition");

        // Give the promotion a valid qualification when only the issued record was altered.
        if (change is "release-reference" or "attempt-reference" or "record-subject")
        {
            qualificationPayload = qualificationPayload with { Release = release.Reference, IssuingAttempt = attempt.Reference };
            qualification = context.Verify(RecordTestContext.Envelope(qualificationPayload, "dual", "production", "qualification"));
            promotion = context.Verify(promotion.Record with { Payload = promotionPayload with { Qualification = qualification.Reference } });
        }

        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateProductionPromoted(promotion, release, attempt, qualification)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Production promotion binds qualification content and its verified profile.</summary>
    /// <param name="change">The independent qualification or profile substitution.</param>
    [Theory]
    [InlineData("qualification-reference")]
    [InlineData("qualification-issuer")]
    [InlineData("profile")]
    public void PromotionRequiresItsExactValidQualification(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> attempt = Attempt(context, release);
        const string alternateDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        string qualificationWriter = change == "qualification-issuer" ? "other" : "production";
        VerifiedRecord<QualificationRecord> qualification = context.Verify(RecordTestContext.Envelope(
            new QualificationRecord(release.Reference, attempt.Reference, RecordTestContext.Digest, [], [], []), qualificationWriter, "production", "qualification"));
        RecordReference reference = change == "qualification-reference" ? qualification.Reference with { Digest = alternateDigest } : qualification.Reference;
        string profile = change == "profile" ? alternateDigest : RecordTestContext.Digest;
        ProductionPromotedRecord payload = new(release.Reference, attempt.Reference, reference, profile);
        VerifiedRecord<ProductionPromotedRecord> promotion = context.Verify(RecordTestContext.Envelope(payload, "production", "production", "promotion"));
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateProductionPromoted(promotion, release, attempt, qualification)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Stale epochs, wrong profiles and substituted content references are rejected.</summary>
    [Fact]
    public void RejectsStaleEpochAndMismatchedReferencedContent()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> attempt = Attempt(context, release);
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(attempt, release, 2));
        VerifiedRecord<AttemptRecord> substituted = context.Verify(attempt.Record with { Payload = attempt.Record.Payload with { Release = new(release.Record.RecordId, "sha256:" + new string('b', 64)) } });
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAttempt(substituted, release, 1));
        VerifiedRecord<QualificationRecord> wrongProfile = context.Verify(RecordTestContext.Envelope(new QualificationRecord(release.Reference, attempt.Reference, "sha256:" + new string('b', 64), [], [], []), "production", "production", "qualification"));
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateQualification(wrongProfile, release, attempt));
    }

    /// <summary>Publication availability rejects mismatched verified staging evidence.</summary>
    /// <param name="change">The invalid staging proof or content reference.</param>
    [Theory]
    [InlineData("non-working")]
    [InlineData("in-progress")]
    [InlineData("production")]
    [InlineData("non-release-attempt")]
    [InlineData("release-reference")]
    [InlineData("attempt-reference")]
    [InlineData("attempt-release-reference")]
    [InlineData("available-subject")]
    [InlineData("attempt-subject")]
    [InlineData("composed-image")]
    public void ReleaseAvailableRequiresExactWorkingStagingEvidence(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        string alternateDigest = "sha256:" + new string('b', 64);
        string environment = change == "production" ? "production" : "staging";
        string outcome = change is "non-working" or "in-progress" ? change : "working";
        AttemptRecord payload = RecordTestContext.Attempt(release.Reference, outcome: outcome);
        payload = change switch
        {
            "attempt-release-reference" => payload with { Release = release.Reference with { Digest = alternateDigest } },
            "non-release-attempt" => payload with { AttemptKind = "rotation" },
            _ => payload,
        };

        VerifiedRecord<AttemptRecord> staging = context.Verify(RecordTestContext.Envelope(payload, environment, environment, "staging-attempt", change == "attempt-subject" ? "other-release" : "release-1"));
        ReleaseAvailableRecord available = new(release.Reference, staging.Reference, release.Record.Payload.ComposedHostDigest);
        available = change switch
        {
            "release-reference" => available with { Release = release.Reference with { Digest = alternateDigest } },
            "attempt-reference" => available with { StagingAttempt = staging.Reference with { Digest = alternateDigest } },
            "composed-image" => available with { ComposedImageDigest = alternateDigest },
            _ => available,
        };
        VerifiedRecord<ReleaseAvailableRecord> proposed = context.Verify(RecordTestContext.Envelope(available, "publication", null, "available", change == "available-subject" ? "other-release" : "release-1"));
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateReleaseAvailable(proposed, release, staging)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Latest-working advances only through its same-environment working terminal writer at a higher epoch.</summary>
    /// <param name="change">The invalid pointer move.</param>
    [Theory]
    [InlineData("valid")]
    [InlineData("wrong-writer")]
    [InlineData("nonterminal")]
    [InlineData("non-working")]
    [InlineData("wrong-environment")]
    [InlineData("stale-epoch")]
    [InlineData("equal-epoch")]
    [InlineData("attempt-epoch")]
    [InlineData("pointer-identity")]
    [InlineData("attempt-subject")]
    [InlineData("pointer-release-reference")]
    [InlineData("pointer-attempt-reference")]
    [InlineData("attempt-release-reference")]
    public void LatestWorkingEnforcesWriterOutcomeEnvironmentAndEpoch(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<AttemptRecord> first = Attempt(context, release, outcome: "working");
        VerifiedRecord<LatestWorkingPointerRecord>? previous = context.Verify(RecordTestContext.Envelope(new LatestWorkingPointerRecord(release.Reference, first.Reference, 1), "production", "production", "latest", "production"));
        long epoch = change == "equal-epoch" ? 1 : 2;
        string outcome = change switch { "nonterminal" => "in-progress", "non-working" => "non-working", _ => "working" };
        string alternateDigest = "sha256:" + new string('b', 64);
        RecordReference otherRelease = release.Reference with { Digest = alternateDigest };
        RecordReference attemptRelease = change == "attempt-release-reference" ? otherRelease : release.Reference;
        AttemptRecord attemptPayload = RecordTestContext.Attempt(attemptRelease, change == "attempt-epoch" ? 3 : epoch, outcome);
        string attemptSubject = change == "attempt-subject" ? "different-release" : "release-1";
        VerifiedRecord<AttemptRecord> next = context.Verify(RecordTestContext.Envelope(attemptPayload, "dual", "production", "next-attempt", attemptSubject));
        string environment = change == "wrong-environment" ? "staging" : "production";
        string writer = change == "wrong-writer" ? "other" : "dual";
        string recordId = change == "pointer-identity" ? "different-pointer" : "latest";
        RecordReference pointerRelease = change == "pointer-release-reference" ? otherRelease : release.Reference;
        RecordReference pointerAttempt = change == "pointer-attempt-reference" ? next.Reference with { Digest = alternateDigest } : next.Reference;
        LatestWorkingPointerRecord pointer = new(pointerRelease, pointerAttempt, epoch);
        VerifiedRecord<LatestWorkingPointerRecord> proposed = context.Verify(RecordTestContext.Envelope(pointer, writer, environment, recordId, environment));
        if (change == "valid")
        {
            RecordTransitionValidator.ValidateLatestWorking(proposed, release, next, epoch, previous);
        }
        else
        {
            // With no prior pointer the attempt environment is the only environment comparison.
            previous = change == "wrong-environment" ? null : previous;
            long currentEpoch = change == "stale-epoch" ? 3 : epoch;
            Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateLatestWorking(proposed, release, next, currentEpoch, previous)).Code.ShouldBe("invalid-transition");
        }
    }

    /// <summary>Every permitted stop setter signs only in its enrolled environment.</summary>
    /// <param name="writer">The setter identity.</param>
    [Theory]
    [InlineData("production")]
    [InlineData("recovery")]
    [InlineData("monitor")]
    [InlineData("admin")]
    [InlineData("deputy")]
    public void PermittedWritersCanSetEnvironmentStops(string writer)
    {
        using RecordTestContext context = new();
        VerifiedRecord<PromotionStopRecord> stop = context.Verify(RecordTestContext.Envelope(new PromotionStopRecord(1, null, "cause", []), writer, "production", "stop", "production"));
        RecordTransitionValidator.ValidatePromotionStop(stop, 0);
    }

    /// <summary>Stop lineage binds the exact prior stop in the same environment.</summary>
    /// <param name="change">The inconsistent lineage.</param>
    [Theory]
    [InlineData("wrong-reference")]
    [InlineData("wrong-environment")]
    public void StopRequiresSameEnvironmentAndExactPredecessor(string change)
    {
        using RecordTestContext context = new();
        string previousEnvironment = change == "wrong-environment" ? "staging" : "production";
        VerifiedRecord<PromotionStopRecord> previous = context.Verify(RecordTestContext.Envelope(
            new PromotionStopRecord(1, null, "probe", []), "monitor", previousEnvironment, "stop-1", previousEnvironment));
        RecordReference reference = change == "wrong-reference" ? previous.Reference with { RecordId = "different-stop" } : previous.Reference;
        VerifiedRecord<PromotionStopRecord> next = context.Verify(RecordTestContext.Envelope(
            new PromotionStopRecord(2, reference, "new-cause", []), "monitor", "production", "stop-2", "production"));
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidatePromotionStop(next, 1, previous)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Stop clearance is bound to the exact current revision and Administrator action.</summary>
    [Fact]
    public void ClearanceCannotEraseALaterStop()
    {
        using RecordTestContext context = new();
        VerifiedRecord<PromotionStopRecord> stop = context.Verify(RecordTestContext.Envelope(new PromotionStopRecord(1, null, "probe", []), "monitor", "production", "stop-1", "production"));
        VerifiedRecord<PromotionStopRecord> later = context.Verify(RecordTestContext.Envelope(new PromotionStopRecord(2, stop.Reference, "other-cause", []), "deputy", "production", "stop-2", "production"));
        RecordTransitionValidator.ValidatePromotionStop(later, 1, stop);
        VerifiedRecord<AttemptRecord> working = Attempt(context, Release(context), "working");
        VerifiedRecord<AdministratorRecord> clear = context.Verify(RecordTestContext.Envelope(new AdministratorRecord("clear-promotion-stop", "production", working.Reference, stop.Reference, 1, null, "reviewed", RecordTestContext.Map(("probe", "resolved")), null), "admin", "production", "clear", "production"));
        RecordTransitionValidator.ValidateAdministrator(clear, stop, working);
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(clear, later, working)).Code.ShouldBe("invalid-transition");
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidatePromotionStop(later, 2, stop));
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidatePromotionStop(later, 1));
    }

    /// <summary>Clearance requires a resolved or accepted disposition for the supplied current cause.</summary>
    /// <param name="disposition">The current cause disposition, or null when missing.</param>
    /// <param name="accepted">Whether the disposition authorizes local clearance.</param>
    [Theory]
    [InlineData(null, false)]
    [InlineData("different-cause", false)]
    [InlineData("resolved", true)]
    [InlineData("accepted", true)]
    public void ClearanceRequiresTheCurrentCauseDisposition(string? disposition, bool accepted)
    {
        using RecordTestContext context = new();
        VerifiedRecord<PromotionStopRecord> stop = context.Verify(RecordTestContext.Envelope(new PromotionStopRecord(1, null, "probe", []), "monitor", "production", "stop", "production"));
        VerifiedRecord<AttemptRecord> working = Attempt(context, Release(context), "working");
        ImmutableDictionary<string, string> dispositions = disposition switch
        {
            null => RecordTestContext.Map(),
            "different-cause" => RecordTestContext.Map(("different-cause", "resolved")),
            _ => RecordTestContext.Map(("probe", disposition)),
        };
        AdministratorRecord payload = new("clear-promotion-stop", "production", working.Reference, stop.Reference, 1, null, "reviewed", dispositions, null);
        VerifiedRecord<AdministratorRecord> clear = context.Verify(RecordTestContext.Envelope(payload, "admin", "production", "clear", "production"));
        if (accepted)
        {
            RecordTransitionValidator.ValidateAdministrator(clear, stop, working);
        }
        else
        {
            Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(clear, stop, working)).Code.ShouldBe("invalid-transition");
        }
    }

    /// <summary>Authority predecessor links require exact verified lineage and subject.</summary>
    [Fact]
    public void AdministratorAuthorityLineageCannotSkipOrChangeSubjects()
    {
        using RecordTestContext context = new();
        AdministratorRecord grant = new("grant-admission", "actor", null, null, null, null, "approved", RecordTestContext.Map(), null);
        VerifiedRecord<AdministratorRecord> predecessor = context.Verify(RecordTestContext.Envelope(grant, "admin", "production", "grant", "actor"));
        VerifiedRecord<AdministratorRecord> revoke = context.Verify(RecordTestContext.Envelope(grant with { Action = "revoke-admission", Predecessor = predecessor.Reference }, "admin", "production", "revoke", "actor"));
        RecordTransitionValidator.ValidateAdministrator(revoke, predecessor: predecessor);
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(revoke));
        VerifiedRecord<AdministratorRecord> unrelated = context.Verify(predecessor.Record with { RecordId = "unrelated" });
        _ = Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(revoke, predecessor: unrelated));
        VerifiedRecord<AdministratorRecord> otherTarget = context.Verify(revoke.Record with { Subject = "other-actor", Payload = revoke.Record.Payload with { Target = "other-actor" } });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(otherTarget, predecessor: predecessor)).Code.ShouldBe("invalid-transition");
        VerifiedRecord<AdministratorRecord> otherFamily = context.Verify(revoke.Record with { Payload = revoke.Record.Payload with { Action = "revoke-management" } });
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(otherFamily, predecessor: predecessor)).Code.ShouldBe("invalid-transition");
    }

    /// <summary>Administrator links require exact, same-environment, verified attempt and stop evidence.</summary>
    /// <param name="change">The inconsistency in the supplied attempt or stop evidence.</param>
    [Theory]
    [InlineData("non-working")]
    [InlineData("in-progress")]
    [InlineData("wrong-attempt-environment")]
    [InlineData("wrong-attempt-reference")]
    [InlineData("missing-attempt-context")]
    [InlineData("wrong-stop-reference")]
    [InlineData("wrong-stop-environment")]
    [InlineData("wrong-stop-revision")]
    [InlineData("revision-without-stop")]
    public void AdministratorRejectsInconsistentLinkedEvidence(string change)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        string attemptEnvironment = change == "wrong-attempt-environment" ? "staging" : "production";
        string outcome = change is "non-working" or "in-progress" ? change : "working";
        VerifiedRecord<AttemptRecord> attempt = context.Verify(RecordTestContext.Envelope(
            RecordTestContext.Attempt(release.Reference, outcome: outcome), "dual", attemptEnvironment, "attempt"));
        string stopEnvironment = change == "wrong-stop-environment" ? "staging" : "production";
        VerifiedRecord<PromotionStopRecord> stop = context.Verify(RecordTestContext.Envelope(
            new PromotionStopRecord(1, null, "probe", []), "monitor", stopEnvironment, "stop", stopEnvironment));
        string action = change is "non-working" or "in-progress" or "wrong-attempt-environment" ? "clear-promotion-stop" : "approve-release";
        RecordReference attemptReference = change == "wrong-attempt-reference" ? attempt.Reference with { RecordId = "different-attempt" } : attempt.Reference;
        RecordReference? stopReference = change switch
        {
            "revision-without-stop" => null,
            "wrong-stop-reference" => stop.Reference with { RecordId = "different-stop" },
            _ => stop.Reference,
        };
        long observedRevision = change == "wrong-stop-revision" ? 2 : 1;
        AdministratorRecord payload = new(action, "production", attemptReference, stopReference, observedRevision, null, "reviewed", RecordTestContext.Map(("probe", "resolved")), null);
        VerifiedRecord<AdministratorRecord> verified = context.Verify(RecordTestContext.Envelope(payload, "admin", "production", "action", "production"));
        VerifiedRecord<AttemptRecord>? attemptContext = change == "missing-attempt-context" ? null : attempt;
        VerifiedRecord<PromotionStopRecord>? stopContext = change == "revision-without-stop" ? null : stop;
        Should.Throw<RecordValidationException>(() => RecordTransitionValidator.ValidateAdministrator(verified, stopContext, attemptContext)).Code.ShouldBe("invalid-transition");
    }

    private static VerifiedRecord<AttemptRecord> Attempt(RecordTestContext context, VerifiedRecord<ReleaseRecord> release, string outcome = "in-progress") => context.Verify(RecordTestContext.Envelope(RecordTestContext.Attempt(release.Reference, outcome: outcome), "production", "production", "attempt"));

    private static VerifiedRecord<ReleaseRecord> Release(RecordTestContext context) => context.Verify(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
}