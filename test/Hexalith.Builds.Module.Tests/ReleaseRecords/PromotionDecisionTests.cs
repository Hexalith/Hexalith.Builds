// <copyright file="PromotionDecisionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.ReleaseRecords;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.ReleaseRecords;

using Shouldly;

using Xunit;

/// <summary>Exercises the v2 signed decision action and attempt contracts.</summary>
public sealed class PromotionDecisionTests
{
    private const ReleaseRecordVersion _version = ReleaseRecordVersion.V2;
    private static readonly StagingEvidenceBinding _decision = new(StagingEvidenceBinding.PolicyV2, "decision-1");
    private static readonly StagingEvidenceBinding _otherDecision = _decision with { DecisionId = "decision-2" };

    /// <summary>Enrollment independently determines who can sign each action.</summary>
    /// <param name="action">The signed action.</param>
    /// <param name="writer">The enrolled principal and actual signer.</param>
    /// <param name="entitled">Whether the writer may sign the action.</param>
    [Theory]
    [InlineData("reserve-admission", "evaluator", true)]
    [InlineData("reserve-admission", "staging-evaluator", false)]
    [InlineData("reserve-admission", "production", false)]
    [InlineData("reserve-admission", "admin", false)]
    [InlineData("reserve-administrator", "admin", true)]
    [InlineData("reserve-administrator", "production", false)]
    [InlineData("reserve-administrator", "evaluator", false)]
    [InlineData("reserve-administrator", "deputy", false)]
    [InlineData("supersede", "staging", true)]
    [InlineData("supersede", "production", false)]
    [InlineData("supersede", "staging-evaluator", false)]
    [InlineData("supersede", "recovery", false)]
    [InlineData("complete", "production", true)]
    [InlineData("complete", "evaluator", false)]
    [InlineData("release-reservation", "production", true)]
    [InlineData("release-reservation", "deputy", false)]
    public void VerifiesOnlyEnrolledActionWriters(string action, string writer, bool entitled)
    {
        using RecordTestContext context = new();
        RecordReference reference = new("record", RecordTestContext.Digest);
        PromotionDecisionRecord payload = action switch
        {
            "reserve-administrator" => new(action, _decision, reference),
            "complete" => new(action, _decision, reference, reference, 1, reference, reference),
            "release-reservation" => new(action, _decision, reference, reference, 1, reference),
            _ => new(action, _decision, reference, reference, 1),
        };
        SignedRecord<PromotionDecisionRecord> signed = context.Sign(Envelope(payload, writer, action == "supersede" ? "staging" : "production"), version: _version);
        RecordVerificationResult<PromotionDecisionRecord> result = Verify(context, signed);
        if (entitled)
        {
            _ = result.Verified.ShouldNotBeNull();
            result.FailureCode.ShouldBeNull();
        }
        else
        {
            result.Verified.ShouldBeNull();
            result.FailureCode.ShouldBe("untrusted-writer");
        }
    }

    /// <summary>Admission authenticates the exact pre-mutation production release attempt and its current epoch.</summary>
    [Fact]
    public void AdmissionMatchesExactCurrentAttempt()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> envelope = Attempt(release);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(envelope, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        RecordTransitionValidator.ValidatePromotionDecision(reservation, _decision, release, admitted, 1);

        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(reservation, _decision, release, admitted, 2));
        VerifiedRecord<PromotionDecisionRecord> currentReservation = context.Verify(reservation.Record with { Payload = reservation.Record.Payload with { LockEpoch = 2 } }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(currentReservation, _decision, release, admitted, 2));
        VerifiedRecord<AttemptRecord> laterRevision = context.Verify(envelope with { RecordedUtc = RecordTestContext.Now.AddSeconds(1) }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(reservation, _decision, release, laterRevision, 1));
        RecordTransitionValidator.ValidatePromotionDecision(Admission(context, release, laterRevision), _decision, release, laterRevision, 1);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(reservation, _otherDecision, release, admitted, 1));
        VerifiedRecord<ReleaseRecord> changedRelease = context.Verify(RecordTestContext.Envelope(release.Record.Payload with { DaprSidecarPatch = "1.18.4" }, "publication", null, release.Record.RecordId));
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(reservation, _decision, changedRelease, admitted, 1));

        foreach (AttemptRecord payload in new[]
        {
            envelope.Payload with { FirstMutationUtc = RecordTestContext.Now },
            envelope.Payload with { Outcome = "non-working" },
            envelope.Payload with { AttemptKind = "rollback" },
            envelope.Payload with { StagingEvidence = _otherDecision },
            envelope.Payload with { Release = changedRelease.Reference },
        })
        {
            VerifiedRecord<AttemptRecord> changed = context.Verify(envelope with { Payload = payload }, _version);
            Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(reservation, _decision, release, changed, 1));
            VerifiedRecord<PromotionDecisionRecord> wrongReservation = Admission(context, release, changed);
            Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(wrongReservation, _decision, release, changed, 1));
        }

        VerifiedRecord<AttemptRecord> staging = context.Verify(Attempt(release, writer: "staging", environment: "staging"), _version);
        VerifiedRecord<PromotionDecisionRecord> wrongScope = Admission(context, release, staging);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(wrongScope, _decision, release, staging, 1));
    }

    /// <summary>The Administrator action binds the decision and release without attempt or epoch context.</summary>
    [Fact]
    public void AdministratorReservationIsSeparate()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<PromotionDecisionRecord> envelope = Envelope(new PromotionDecisionRecord("reserve-administrator", _decision, release.Reference), "admin", "production");
        VerifiedRecord<PromotionDecisionRecord> verified = context.Verify(envelope, _version);
        RecordTransitionValidator.ValidatePromotionDecision(verified, _decision, release);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(verified, _otherDecision, release));
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(verified, _decision, release, currentEpoch: 1));
        RecordReference unexpectedAttempt = new("attempt", RecordTestContext.Digest);
        Should.Throw<RecordValidationException>(() => context.Sign(envelope with { Payload = envelope.Payload with { Attempt = unexpectedAttempt } }, version: _version)).Code.ShouldBe("schema-rejected");
        Should.Throw<RecordValidationException>(() => context.Sign(envelope with { Payload = envelope.Payload with { LockEpoch = 1 } }, version: _version)).Code.ShouldBe("schema-rejected");

        SignedRecord<PromotionDecisionRecord> signed = context.Sign(envelope, version: _version);
        JsonObject invalid = JsonNode.Parse(ReleaseRecordCodec.Encode(signed, _version)).ShouldBeOfType<JsonObject>();
        invalid["payload"]!["attempt"] = null;
        ReleaseRecordCodec.Verify<PromotionDecisionRecord>(Encoding.UTF8.GetBytes(invalid.ToJsonString()), context.Policy, _version).FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Staging binds the actual writer, its current staging epoch and the separate target decision.</summary>
    /// <param name="kind">The staging start or prerequisite reset attempt kind.</param>
    [Theory]
    [InlineData("release")]
    [InlineData("in-place-restore")]
    public void StagingMaySupersedeAnotherReleaseDecision(string kind)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        VerifiedRecord<ReleaseRecord> later = Release(context, "release-2");
        SignedRecord<AttemptRecord> envelope = Attempt(later, _otherDecision, writer: "staging", environment: "staging", epoch: 3);
        envelope = envelope with { Payload = envelope.Payload with { AttemptKind = kind, RecoveryAllowance = 0 } };
        VerifiedRecord<AttemptRecord> initiating = context.Verify(envelope, _version);
        SignedRecord<PromotionDecisionRecord> supersession = Envelope(new PromotionDecisionRecord("supersede", _decision, release.Reference, initiating.Reference, 3), "staging", "staging");
        VerifiedRecord<PromotionDecisionRecord> verified = context.Verify(supersession, _version);
        RecordTransitionValidator.ValidatePromotionDecision(verified, _decision, release, initiating, 3);

        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(verified, _decision, release, initiating, 4));
        VerifiedRecord<PromotionDecisionRecord> currentAction = context.Verify(supersession with { Payload = supersession.Payload with { LockEpoch = 4 } }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(currentAction, _decision, release, initiating, 4));
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(verified, _otherDecision, release, initiating, 3));
        VerifiedRecord<PromotionDecisionRecord> wrongWriter = context.Verify(supersession with { WriterId = "dual", KeyId = "dual-key" }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(wrongWriter, _decision, release, initiating, 3));
        Should.Throw<RecordValidationException>(() => context.Sign(supersession with { Environment = "production" }, version: _version)).Code.ShouldBe("schema-rejected");
    }

    /// <summary>Cross-release supersession requires a distinct retained release record and decision.</summary>
    /// <param name="reusedDecision">Whether the different release reuses the target's decision ID.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CrossReleaseSupersessionRejectsIdentityConflicts(bool reusedDecision)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> target = Release(context);
        VerifiedRecord<ReleaseRecord> later = context.Verify(RecordTestContext.Envelope(
            RecordTestContext.Release() with { ReleaseId = "release-2" }, "publication", null, reusedDecision ? "other-release-record" : target.Record.RecordId, "release-2"));
        SignedRecord<AttemptRecord> attempt = Attempt(later, reusedDecision ? _decision : _otherDecision, writer: "staging", environment: "staging");
        VerifiedRecord<AttemptRecord> initiating = context.Verify(attempt, _version);
        RecordTransitionValidator.ValidateAttempt(initiating, later, 1);
        VerifiedRecord<PromotionDecisionRecord> supersession = context.Verify(Envelope(new PromotionDecisionRecord("supersede", _decision, target.Reference, initiating.Reference, 1), "staging", "staging"), _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(supersession, _decision, target, initiating, 1));
    }

    /// <summary>A same-release staging start leaves the decision pending, while a prerequisite reset can target it.</summary>
    [Fact]
    public void SameReleaseSupersessionRequiresPrerequisiteReset()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> start = Attempt(release, writer: "staging", environment: "staging");
        VerifiedRecord<AttemptRecord> staging = context.Verify(start, _version);
        PromotionDecisionRecord payload = new("supersede", _decision, release.Reference, staging.Reference, 1);
        VerifiedRecord<PromotionDecisionRecord> supersession = context.Verify(Envelope(payload, "staging", "staging"), _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(supersession, _decision, release, staging, 1));

        VerifiedRecord<AttemptRecord> reset = context.Verify(start with { Payload = start.Payload with { AttemptKind = "in-place-restore", RecoveryAllowance = 0 } }, _version);
        VerifiedRecord<PromotionDecisionRecord> resetSupersession = context.Verify(Envelope(payload with { Attempt = reset.Reference }, "staging", "staging"), _version);
        RecordTransitionValidator.ValidatePromotionDecision(resetSupersession, _decision, release, reset, 1);
    }

    /// <summary>Closure binds separate admitted and terminal revisions and completes with the exact pointer.</summary>
    /// <param name="outcome">The terminal outcome.</param>
    /// <param name="action">The action taken against the reservation.</param>
    [Theory]
    [InlineData("working", "complete")]
    [InlineData("non-working", "release-reservation")]
    public void ClosureMatchesReservationTerminalAndPointer(string outcome, string action)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> attempt = Attempt(release);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(attempt, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        VerifiedRecord<AttemptRecord> terminal = context.Verify(attempt with { RecordedUtc = RecordTestContext.Now.AddSeconds(2), Payload = attempt.Payload with { Outcome = outcome } }, _version);
        terminal.Reference.ShouldNotBe(admitted.Reference);
        VerifiedRecord<LatestWorkingPointerRecord>? working = action == "complete" ? Pointer(context, release, terminal) : null;
        SignedRecord<PromotionDecisionRecord> envelope = Envelope(new PromotionDecisionRecord(action, _decision, release.Reference, terminal.Reference, 1, reservation.Reference, working?.Reference), "production", "production");
        VerifiedRecord<PromotionDecisionRecord> closure = context.Verify(envelope, _version);
        RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, 1, reservation, admitted, working);

        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, 2, reservation, admitted, working));
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, admitted, 1, reservation, admitted, working));
        VerifiedRecord<AttemptRecord> laterAdmission = context.Verify(attempt with { RecordedUtc = RecordTestContext.Now.AddSeconds(1) }, _version);
        RecordTransitionValidator.ValidatePromotionDecision(Admission(context, release, laterAdmission), _decision, release, laterAdmission, 1);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, 1, reservation, laterAdmission, working));
        VerifiedRecord<PromotionDecisionRecord> otherReservation = context.Verify(reservation.Record with { RecordedUtc = RecordTestContext.Now.AddSeconds(1) }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, 1, otherReservation, admitted, working));
        VerifiedRecord<PromotionDecisionRecord> otherWriter = context.Verify(envelope with { WriterId = "other", KeyId = "other-key" }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(otherWriter, _decision, release, terminal, 1, reservation, admitted, working));
        VerifiedRecord<AttemptRecord> otherLineage = context.Verify(terminal.Record with { RecordId = "another-attempt" }, _version);
        VerifiedRecord<PromotionDecisionRecord> wrongLineage = context.Verify(envelope with { Payload = envelope.Payload with { Attempt = otherLineage.Reference } }, _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(wrongLineage, _decision, release, otherLineage, 1, reservation, admitted, working));

        if (working is not null)
        {
            VerifiedRecord<LatestWorkingPointerRecord> changedPointer = context.Verify(working.Record with { Payload = working.Record.Payload with { TerminalAttempt = admitted.Reference } });
            Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, 1, reservation, admitted, changedPointer));
            VerifiedRecord<PromotionDecisionRecord> wrongPointer = context.Verify(envelope with { Payload = envelope.Payload with { WorkingPointer = changedPointer.Reference } }, _version);
            Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(wrongPointer, _decision, release, terminal, 1, reservation, admitted, changedPointer));
        }
    }

    /// <summary>The closure keeps admitted fields fixed and may use a later current epoch on the same attempt.</summary>
    /// <param name="outcome">The authoritative terminal outcome.</param>
    /// <param name="action">The action against the reservation.</param>
    [Theory]
    [InlineData("working", "complete")]
    [InlineData("non-working", "release-reservation")]
    public void ClosureKeepsImmutableFieldsAndRejectsEarlierEpoch(string outcome, string action)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> attempt = Attempt(release, epoch: 2);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(attempt, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        AttemptRecord terminalPayload = attempt.Payload with { Outcome = outcome, LockEpoch = 3 };
        CheckClosure(terminalPayload, accepted: true);
        CheckClosure(terminalPayload with { CheckTimeoutSeconds = terminalPayload.CheckTimeoutSeconds + 1 }, accepted: false);
        CheckClosure(terminalPayload with { LockEpoch = 1 }, accepted: false);

        void CheckClosure(AttemptRecord payload, bool accepted)
        {
            VerifiedRecord<AttemptRecord> terminal = context.Verify(attempt with { Payload = payload }, _version);
            VerifiedRecord<LatestWorkingPointerRecord>? pointer = outcome == "working" ? Pointer(context, release, terminal) : null;
            PromotionDecisionRecord decision = new(action, _decision, release.Reference, terminal.Reference, payload.LockEpoch, reservation.Reference, pointer?.Reference);
            VerifiedRecord<PromotionDecisionRecord> closure = context.Verify(Envelope(decision, "production", "production"), _version);
            if (accepted)
            {
                RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, payload.LockEpoch, reservation, admitted, pointer);
            }
            else
            {
                Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(closure, _decision, release, terminal, payload.LockEpoch, reservation, admitted, pointer));
            }
        }
    }

    /// <summary>A rotated key retains closure authority when independently enrolled as an executor for the same writer.</summary>
    /// <param name="outcome">The authoritative terminal outcome.</param>
    /// <param name="action">The action against the reservation.</param>
    [Theory]
    [InlineData("working", "complete")]
    [InlineData("non-working", "release-reservation")]
    public void ClosureKeyRotationRequiresExecutorEnrollment(string outcome, string action)
    {
        using RecordTestContext context = new();
        using ECDsa rotatedKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> attempt = Attempt(release);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(attempt, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        VerifiedRecord<AttemptRecord> terminal = context.Verify(attempt with { Payload = attempt.Payload with { Outcome = outcome } }, _version);
        VerifiedRecord<LatestWorkingPointerRecord>? pointer = outcome == "working" ? Pointer(context, release, terminal) : null;
        PromotionDecisionRecord payload = new(action, _decision, release.Reference, terminal.Reference, 1, reservation.Reference, pointer?.Reference);
        SignedRecord<PromotionDecisionRecord> rotated = ReleaseRecordCodec.Sign(Envelope(payload, "production", "production") with { KeyId = "rotated-key" }, rotatedKey, _version);
        RecordWriterKey executor = context.Anchors.Single(anchor => anchor.WriterId == "production") with { KeyId = rotated.KeyId, PublicKeyPem = rotatedKey.ExportSubjectPublicKeyInfoPem() };
        RecordWriterKey recoveryOnly = executor with { Roles = ImmutableHashSet.Create(StringComparer.Ordinal, "recovery-executor") };
        byte[] bytes = ReleaseRecordCodec.Encode(rotated, _version);
        RecordVerificationResult<PromotionDecisionRecord> refused = ReleaseRecordCodec.Verify<PromotionDecisionRecord>(bytes, new([.. context.Anchors, recoveryOnly]), _version);
        refused.FailureCode.ShouldBe("untrusted-writer");
        refused.Verified.ShouldBeNull();
        RecordVerificationResult<PromotionDecisionRecord> allowed = ReleaseRecordCodec.Verify<PromotionDecisionRecord>(bytes, new([.. context.Anchors, executor]), _version);
        allowed.FailureCode.ShouldBeNull();
        RecordTransitionValidator.ValidatePromotionDecision(allowed.Verified.ShouldNotBeNull(), _decision, release, terminal, 1, reservation, admitted, pointer);
    }

    /// <summary>An in-progress or superseded attempt is insufficient to release a reservation.</summary>
    /// <param name="outcome">The uncertain or superseded outcome.</param>
    [Theory]
    [InlineData("in-progress")]
    [InlineData("superseded")]
    public void OnlyAuthoritativeNonWorkingOutcomeReleases(string outcome)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> envelope = Attempt(release);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(envelope, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        RecordReference? successor = outcome == "superseded" ? new("successor", RecordTestContext.Digest) : null;
        VerifiedRecord<AttemptRecord> attempt = context.Verify(envelope with { Payload = envelope.Payload with { LockEpoch = 2, Outcome = outcome, SupersededBy = successor } }, _version);
        VerifiedRecord<PromotionDecisionRecord> releaseReservation = context.Verify(Envelope(new PromotionDecisionRecord("release-reservation", _decision, release.Reference, attempt.Reference, 2, reservation.Reference), "production", "production"), _version);
        Refuses(() => RecordTransitionValidator.ValidatePromotionDecision(releaseReservation, _decision, release, attempt, 2, reservation, admitted));
    }

    /// <summary>A schema-valid and independently entitled mutation must fail at signature verification.</summary>
    [Fact]
    public void SignedAttemptAndActionBindingsRejectTampering()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> signedAttempt = context.Sign(Attempt(release), version: _version);
        SignedRecord<AttemptRecord> changedAttempt = signedAttempt with { Payload = signedAttempt.Payload with { StagingEvidence = _otherDecision } };
        _ = context.Verify(changedAttempt, _version).ShouldNotBeNull();
        Verify(context, changedAttempt).FailureCode.ShouldBe("invalid-signature");

        VerifiedRecord<AttemptRecord> admitted = context.Verify(signedAttempt, _version);
        SignedRecord<PromotionDecisionRecord> signed = Admission(context, release, admitted).Record;
        foreach (PromotionDecisionRecord payload in new[]
        {
            signed.Payload with { StagingEvidence = _otherDecision },
            signed.Payload with { Release = release.Reference with { RecordId = "other-release" } },
            signed.Payload with { Release = release.Reference with { Digest = RecordTestContext.Digest } },
            signed.Payload with { Attempt = admitted.Reference with { Digest = RecordTestContext.Digest } },
            signed.Payload with { LockEpoch = 2 },
        })
        {
            SignedRecord<PromotionDecisionRecord> changed = signed with { Payload = payload };
            _ = context.Verify(changed, _version).ShouldNotBeNull();
            Verify(context, changed).FailureCode.ShouldBe("invalid-signature");
        }
    }

    /// <summary>Closure signatures distinguish complete from release and cover the pointer and reservation.</summary>
    [Fact]
    public void ClosureSignatureCoversActionAndReferences()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> envelope = Attempt(release);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(envelope, _version);
        VerifiedRecord<PromotionDecisionRecord> reservation = Admission(context, release, admitted);
        VerifiedRecord<AttemptRecord> terminal = context.Verify(envelope with { Payload = envelope.Payload with { Outcome = "working" } }, _version);
        VerifiedRecord<LatestWorkingPointerRecord> pointer = Pointer(context, release, terminal);
        PromotionDecisionRecord payload = new("complete", _decision, release.Reference, terminal.Reference, 1, reservation.Reference, pointer.Reference);
        SignedRecord<PromotionDecisionRecord> signed = context.Sign(Envelope(payload, "production", "production"), version: _version);
        foreach (PromotionDecisionRecord variant in new[]
        {
            payload with { Action = "release-reservation", WorkingPointer = null },
            payload with { Reservation = reservation.Reference with { Digest = RecordTestContext.Digest } },
            payload with { WorkingPointer = pointer.Reference with { Digest = RecordTestContext.Digest } },
        })
        {
            SignedRecord<PromotionDecisionRecord> changed = signed with { Payload = variant };
            _ = context.Verify(changed, _version).ShouldNotBeNull();
            RecordVerificationResult<PromotionDecisionRecord> result = Verify(context, changed);
            result.FailureCode.ShouldBe("invalid-signature");
            result.Verified.ShouldBeNull();
        }
    }

    /// <summary>Missing policy, unknown policy and extra nested fields fail at schema validation.</summary>
    /// <param name="field">The field to alter.</param>
    /// <param name="value">The replacement value, or null to remove the field.</param>
    [Theory]
    [InlineData("policyId", null)]
    [InlineData("policyId", "hexalith.platform.staging-evidence-policy.v1")]
    [InlineData("policyId", "hexalith.platform.staging-evidence-policy.v3")]
    [InlineData("extra", "private-input")]
    public void V2RequiresClosedPolicyBinding(string field, string? value)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> signed = context.Sign(Attempt(release), version: _version);
        VerifiedRecord<AttemptRecord> admitted = context.Verify(signed, _version);
        SignedRecord<PromotionDecisionRecord> action = Admission(context, release, admitted).Record;
        RecordVerificationResult<AttemptRecord> attemptResult = ReleaseRecordCodec.Verify<AttemptRecord>(MutateBinding(signed, field, value), context.Policy, _version);
        RecordVerificationResult<PromotionDecisionRecord> actionResult = ReleaseRecordCodec.Verify<PromotionDecisionRecord>(MutateBinding(action, field, value), context.Policy, _version);
        attemptResult.FailureCode.ShouldBe("schema-rejected");
        attemptResult.Verified.ShouldBeNull();
        actionResult.FailureCode.ShouldBe("schema-rejected");
        actionResult.Verified.ShouldBeNull();
    }

    /// <summary>Explicit version selection has no fallback and attempt revisions preserve their binding.</summary>
    [Fact]
    public void VersionSelectionAndAttemptBindingAreStrict()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> v2 = context.Sign(Attempt(release), version: _version);
        SignedRecord<AttemptRecord> v1 = context.Sign(v2 with { Schema = ReleaseRecordCodec.SchemaFor<AttemptRecord>(), Payload = v2.Payload with { StagingEvidence = null } });
        byte[] v1Bytes = ReleaseRecordCodec.Encode(v1);
        byte[] v2Bytes = ReleaseRecordCodec.Encode(v2, _version);
        Encoding.UTF8.GetString(v1Bytes).ShouldNotContain("stagingEvidence");
        ReleaseRecordCodec.Verify<AttemptRecord>(v1Bytes, context.Policy, _version).FailureCode.ShouldBe("schema-rejected");
        ReleaseRecordCodec.Verify<AttemptRecord>(v2Bytes, context.Policy).FailureCode.ShouldBe("schema-rejected");
        Should.Throw<RecordValidationException>(() => context.Sign(v2)).Code.ShouldBe("unsupported-record");
        Should.Throw<RecordValidationException>(() => context.Sign(v1, version: _version)).Code.ShouldBe("unsupported-record");
        Should.Throw<RecordValidationException>(() => context.Sign(v1 with { Payload = v2.Payload })).Code.ShouldBe("schema-rejected");
        Should.Throw<RecordValidationException>(() => context.Sign(v2 with { Payload = v1.Payload }, version: _version)).Code.ShouldBe("schema-rejected");

        VerifiedRecord<AttemptRecord> previous = context.Verify(v2, _version);
        SignedRecord<PromotionDecisionRecord> action = Admission(context, release, previous).Record;
        ReleaseRecordCodec.Verify<PromotionDecisionRecord>(ReleaseRecordCodec.Encode(action, _version), context.Policy).FailureCode.ShouldBe("schema-rejected");
        VerifiedRecord<AttemptRecord> changed = context.Verify(v2 with { Payload = v2.Payload with { StagingEvidence = _otherDecision } }, _version);
        Refuses(() => RecordTransitionValidator.ValidateAttempt(changed, release, 1, previous));
        Refuses(() => RecordTransitionValidator.ValidateAttempt(context.Verify(v1), release, 1, previous));
        Refuses(() => RecordTransitionValidator.ValidateAttempt(previous, release, 1, context.Verify(v1)));
        VerifiedRecord<AttemptRecord> terminal = context.Verify(v2 with { Payload = v2.Payload with { Outcome = "working" } }, _version);
        RecordTransitionValidator.ValidateAttempt(terminal, release, 1, previous);
    }

    /// <summary>V2 takeover retains the predecessor's wire version and immutable decision binding.</summary>
    /// <param name="predecessorIsV2">Whether the predecessor uses the v2 attempt schema.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedV2TakeoverRetainsPredecessorSchemaAndBinding(bool predecessorIsV2)
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = Release(context);
        SignedRecord<AttemptRecord> predecessor = Attempt(release);
        ReleaseRecordVersion predecessorVersion = predecessorIsV2 ? _version : ReleaseRecordVersion.V1;
        if (!predecessorIsV2)
        {
            predecessor = predecessor with { Schema = ReleaseRecordCodec.SchemaFor<AttemptRecord>(), Payload = predecessor.Payload with { StagingEvidence = null } };
        }

        VerifiedRecord<AttemptRecord> prior = context.Verify(predecessor, predecessorVersion);
        SignedRecord<AttemptRecord> later = Attempt(release, writer: "other", epoch: 2);
        later = later with { RecordId = "successor", Payload = later.Payload with { Supersedes = prior.Reference } };
        VerifiedRecord<AttemptRecord> successor = context.Verify(later, _version);
        AttemptRecord superseded = predecessor.Payload with { LockEpoch = 2, Outcome = "superseded", SupersededBy = successor.Reference };
        SignedRecord<AttemptRecord> final = predecessor with { WriterId = "other", KeyId = "other-key", Payload = superseded };
        RecordTransitionValidator.ValidateAttempt(context.Verify(final, predecessorVersion), release, 2, prior, successor);

        if (predecessorIsV2)
        {
            VerifiedRecord<AttemptRecord> rebound = context.Verify(final with { Payload = superseded with { StagingEvidence = _otherDecision } }, _version);
            Refuses(() => RecordTransitionValidator.ValidateAttempt(rebound, release, 2, prior, successor));
            VerifiedRecord<AttemptRecord> v1 = context.Verify(later with { Schema = ReleaseRecordCodec.SchemaFor<AttemptRecord>(), Payload = later.Payload with { StagingEvidence = null } });
            VerifiedRecord<AttemptRecord> withV1Successor = context.Verify(final with { Payload = superseded with { SupersededBy = v1.Reference } }, _version);
            Refuses(() => RecordTransitionValidator.ValidateAttempt(withV1Successor, release, 2, prior, v1));
        }
    }

    /// <summary>Public v2 signing bytes pin the canonical digest and can be signed by an independently enrolled key.</summary>
    [Fact]
    public void V2CanonicalSigningBytesVerifyWithExternalSignature()
    {
        using RecordTestContext context = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        RecordReference release = new("release-record", RecordTestContext.Digest);
        RecordReference attempt = new("attempt-record", RecordTestContext.Digest);
        SignedRecord<PromotionDecisionRecord> envelope = Envelope(new PromotionDecisionRecord("reserve-admission", _decision, release, attempt, 1), "evaluator", "production");
        byte[] signingBytes = ReleaseRecordCodec.GetSigningBytes(envelope, _version);
        const string digest = "sha256:38bd3fd9ce33c5b390c35bcb1e54fe5edea36a58182d8a323ea4f6ca5b6d1b93";
        ("sha256:" + Convert.ToHexStringLower(SHA256.HashData(signingBytes))).ShouldBe(digest);
        byte[] signature = key.SignData(signingBytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        byte[] bytes = ReleaseRecordCodec.Encode(envelope with { Signature = Convert.ToBase64String(signature) }, _version);
        RecordWriterKey anchor = context.Anchors.Single(anchor => anchor.WriterId == "evaluator") with { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() };
        RecordVerificationResult<PromotionDecisionRecord> result = ReleaseRecordCodec.Verify<PromotionDecisionRecord>(bytes, new([anchor]), _version);
        result.FailureCode.ShouldBeNull();
        result.Verified.ShouldNotBeNull().Digest.ShouldBe(digest);
    }

    /// <summary>The action determines which fields the decoder requires or forbids.</summary>
    /// <param name="action">The valid starting action.</param>
    /// <param name="field">The field removed or added.</param>
    /// <param name="addField">Whether to add the forbidden field.</param>
    [Theory]
    [InlineData("reserve-admission", "attempt", false)]
    [InlineData("reserve-admission", "lockEpoch", false)]
    [InlineData("complete", "reservation", false)]
    [InlineData("complete", "workingPointer", false)]
    [InlineData("release-reservation", "workingPointer", true)]
    public void V2ActionRequiresOnlyItsSchemaFields(string action, string field, bool addField)
    {
        using RecordTestContext context = new();
        RecordReference reference = new("record", RecordTestContext.Digest);
        PromotionDecisionRecord payload = new(
            action,
            _decision,
            reference,
            reference,
            1,
            action == "reserve-admission" ? null : reference,
            action == "complete" ? reference : null);
        string writer = action == "reserve-admission" ? "evaluator" : "production";
        SignedRecord<PromotionDecisionRecord> signed = context.Verify(Envelope(payload, writer, "production"), _version).Record;
        JsonObject json = JsonNode.Parse(ReleaseRecordCodec.Encode(signed, _version)).ShouldBeOfType<JsonObject>();
        JsonObject fields = json["payload"]!.AsObject();
        if (addField)
        {
            fields[field] = fields["release"]!.DeepClone();
        }
        else
        {
            fields.Remove(field).ShouldBeTrue();
        }

        RecordVerificationResult<PromotionDecisionRecord> result = ReleaseRecordCodec.Verify<PromotionDecisionRecord>(Encoding.UTF8.GetBytes(json.ToJsonString()), context.Policy, _version);
        result.FailureCode.ShouldBe("schema-rejected");
        result.Verified.ShouldBeNull();
    }

    private static VerifiedRecord<ReleaseRecord> Release(RecordTestContext context, string id = "release-1") =>
        context.Verify(RecordTestContext.Envelope(RecordTestContext.Release() with { ReleaseId = id }, "publication", null, id + "-record", id));

    private static SignedRecord<AttemptRecord> Attempt(VerifiedRecord<ReleaseRecord> release, StagingEvidenceBinding? decision = null, string writer = "production", string environment = "production", long epoch = 1) =>
        Envelope(RecordTestContext.Attempt(release.Reference, epoch) with { StagingEvidence = decision ?? _decision }, writer, environment, "attempt", release.Record.Payload.ReleaseId);

    private static VerifiedRecord<PromotionDecisionRecord> Admission(RecordTestContext context, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> attempt) =>
        context.Verify(Envelope(new PromotionDecisionRecord("reserve-admission", _decision, release.Reference, attempt.Reference, attempt.Record.Payload.LockEpoch), "evaluator", "production", "reservation"), _version);

    private static VerifiedRecord<LatestWorkingPointerRecord> Pointer(RecordTestContext context, VerifiedRecord<ReleaseRecord> release, VerifiedRecord<AttemptRecord> terminal) =>
        context.Verify(RecordTestContext.Envelope(new LatestWorkingPointerRecord(release.Reference, terminal.Reference, terminal.Record.Payload.LockEpoch), "production", "production", "pointer", "production"));

    private static SignedRecord<T> Envelope<T>(T payload, string writer, string environment, string id = "action", string subject = "release-1")
        where T : class => new(ReleaseRecordCodec.SchemaFor<T>(_version), ReleaseRecordCodec.KindFor<T>(), id, writer, writer + "-key", subject, environment, RecordTestContext.Now, RecordTestContext.Now, payload, "ES256", string.Empty);

    private static RecordVerificationResult<T> Verify<T>(RecordTestContext context, SignedRecord<T> signed)
        where T : class => ReleaseRecordCodec.Verify<T>(ReleaseRecordCodec.Encode(signed, _version), context.Policy, _version);

    private static void Refuses(Action transition) => Should.Throw<RecordValidationException>(transition).Code.ShouldBe("invalid-transition");

    private static byte[] MutateBinding<T>(SignedRecord<T> signed, string field, string? value)
        where T : class
    {
        JsonObject json = JsonNode.Parse(ReleaseRecordCodec.Encode(signed, _version)).ShouldBeOfType<JsonObject>();
        JsonObject binding = json["payload"]!["stagingEvidence"]!.AsObject();
        if (value is null)
        {
            _ = binding.Remove(field);
        }
        else
        {
            binding[field] = value;
        }

        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }
}