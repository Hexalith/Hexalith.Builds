// <copyright file="RecordSignatureTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.ReleaseRecords;

using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.ReleaseRecords;

using Shouldly;

using Xunit;

/// <summary>Exercises signature coverage, independent authority and immutable verification.</summary>
public sealed class RecordSignatureTests
{
    /// <summary>Every envelope claim and representative invariant input is authenticated.</summary>
    /// <param name="field">The envelope field to tamper with.</param>
    /// <param name="value">The replacement field value.</param>
    [Theory]
    [InlineData("recordId", "another-record")]
    [InlineData("writerId", "other")]
    [InlineData("keyId", "other-key")]
    [InlineData("subject", "another-subject")]
    [InlineData("createdUtc", "2026-10-21T00:00:00+00:00")]
    [InlineData("recordedUtc", "2026-10-23T00:00:00+00:00")]
    [InlineData("environment", "production")]
    public void TamperingNeverReturnsTrustedData(string field, string value)
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> signed = context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
        JsonObject json = JsonNode.Parse(ReleaseRecordCodec.Encode(signed)).ShouldBeOfType<JsonObject>();
        json[field] = value;
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json.ToJsonString()), context.Policy).Verified.ShouldBeNull();
    }

    /// <summary>Schema-valid alternatives prove every release payload field is signed.</summary>
    [Fact]
    public void EveryReleasePayloadValueIsSigned()
    {
        using RecordTestContext context = new();
        const string alternateDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        SignedRecord<ReleaseRecord> signed = context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
        JsonObject original = JsonNode.Parse(ReleaseRecordCodec.Encode(signed)).ShouldBeOfType<JsonObject>();
        foreach (string field in original["payload"]!.AsObject().Select(entry => entry.Key))
        {
            JsonObject mutation = original.DeepClone().AsObject();
            JsonNode? value = mutation["payload"]![field];
            if (value is JsonObject dictionary)
            {
                string key = dictionary.GetAt(0).Key;
                dictionary[key] = field == "moduleChangeClassifications" ? "unchanged" : alternateDigest;
            }
            else if (value is JsonArray array)
            {
                array[0] = value[0]!.GetValue<string>() + "-other";
            }
            else
            {
                string current = value!.GetValue<string>();
                mutation["payload"]![field] = current.StartsWith("sha256:", StringComparison.Ordinal) ? alternateDigest : current + "-other";
                if (field == "releaseId")
                {
                    mutation["subject"] = mutation["payload"]![field]!.GetValue<string>();
                }
            }

            SignedRecord<ReleaseRecord> alternative = mutation.Deserialize<SignedRecord<ReleaseRecord>>(JsonSerializerOptions.Web).ShouldNotBeNull();
            _ = context.Verify(alternative);
            ReleaseRecordCodec.Verify<ReleaseRecord>(ReleaseRecordCodec.Encode(alternative), context.Policy).FailureCode.ShouldBe("invalid-signature");
        }
    }

    /// <summary>Contradictory phase and outage timestamps fail before signature trust.</summary>
    /// <param name="change">The contradictory timestamp.</param>
    [Theory]
    [InlineData("rollout-before-entry")]
    [InlineData("verification-before-entry")]
    [InlineData("outage-after-record")]
    [InlineData("rollout-after-lifetime")]
    [InlineData("verification-after-lifetime")]
    public void RejectsContradictoryAttemptTiming(string change)
    {
        using RecordTestContext context = new();
        AttemptRecord initial = RecordTestContext.Attempt(new("release", RecordTestContext.Digest));
        AttemptRecord changed = change switch
        {
            "rollout-before-entry" => initial with { RolloutDeadlineUtc = RecordTestContext.Now.AddSeconds(-1) },
            "verification-before-entry" => initial with { VerificationDeadlineUtc = RecordTestContext.Now.AddSeconds(-1) },
            "outage-after-record" => initial with { OutageUtc = RecordTestContext.Now.AddSeconds(1) },
            "rollout-after-lifetime" => initial with { RolloutDeadlineUtc = initial.MaximumLifetimeUtc.AddSeconds(1) },
            _ => initial with { VerificationDeadlineUtc = initial.MaximumLifetimeUtc.AddSeconds(1) },
        };
        SignedRecord<AttemptRecord> signed = context.Sign(RecordTestContext.Envelope(initial, "production", "production", "attempt"));
        SignedRecord<AttemptRecord> invalid = signed with { Payload = changed };
        ReleaseRecordCodec.Verify<AttemptRecord>(JsonSerializer.SerializeToUtf8Bytes(invalid, JsonSerializerOptions.Web), context.Policy).FailureCode.ShouldBe("invalid-attempt");
        Should.Throw<RecordValidationException>(() => context.Sign(invalid)).Code.ShouldBe("invalid-attempt");
    }

    /// <summary>Expected signing-key failures expose a fixed metadata-only error.</summary>
    /// <param name="failure">The unusable key form.</param>
    [Theory]
    [InlineData("unsupported-curve")]
    [InlineData("public-only")]
    public void SigningKeyFailuresHaveStableCodes(string failure)
    {
        using RecordTestContext context = new();
        using ECDsa key = ECDsa.Create();
        if (failure == "unsupported-curve")
        {
            key.GenerateKey(ECCurve.NamedCurves.nistP384);
        }
        else
        {
            key.ImportFromPem(context.Anchors[0].PublicKeyPem);
        }

        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release");
        RecordValidationException error = Should.Throw<RecordValidationException>(() => ReleaseRecordCodec.Sign(envelope, key));
        error.Code.ShouldBe("invalid-signing-key");
        error.Message.ShouldBe("Release record rejected (invalid-signing-key).");
        error.InnerException.ShouldBeNull();
    }

    /// <summary>Signing still reports caller argument-null mistakes normally.</summary>
    [Fact]
    public void SigningPreservesArgumentNullFailures()
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release");
        Should.Throw<ArgumentNullException>(() => ReleaseRecordCodec.Sign<ReleaseRecord>(null!, key)).ParamName.ShouldBe("record");
        Should.Throw<ArgumentNullException>(() => ReleaseRecordCodec.Sign(envelope, null!)).ParamName.ShouldBe("privateKey");
    }

    /// <summary>All eight kinds require an independently entitled writer despite a real valid signature.</summary>
    [Fact]
    public void EveryKindRejectsAnUnentitledWriter()
    {
        using RecordTestContext context = new();
        RecordReference reference = new("reference", RecordTestContext.Digest);
        AssertDenied(context, RecordTestContext.Envelope(RecordTestContext.Release(), "monitor", null, "release"));
        AssertDenied(context, RecordTestContext.Envelope(RecordTestContext.Attempt(reference), "monitor", "production", "attempt"));
        AssertDenied(context, RecordTestContext.Envelope(new QualificationRecord(reference, reference, RecordTestContext.Digest, [], [], []), "monitor", "production", "qualification"));
        AssertDenied(context, RecordTestContext.Envelope(new ProductionPromotedRecord(reference, reference, reference, RecordTestContext.Digest), "monitor", "production", "promoted"));
        AssertDenied(context, RecordTestContext.Envelope(new ReleaseAvailableRecord(reference, reference, RecordTestContext.Digest), "monitor", null, "available"));
        AssertDenied(context, RecordTestContext.Envelope(new LatestWorkingPointerRecord(reference, reference, 1), "monitor", "production", "latest", "production"));
        AssertDenied(context, RecordTestContext.Envelope(new PromotionStopRecord(1, null, "cause", []), "publication", "production", "stop", "production"));
        AssertDenied(context, RecordTestContext.Envelope(new AdministratorRecord("return-to-g2", "production", null, null, null, null, "reviewed", RecordTestContext.Map(), null), "monitor", "production", "administrator", "production"));
    }

    /// <summary>Valid cryptography under the wrong enrolled key is insufficient.</summary>
    [Fact]
    public void RejectsSignatureFromAnotherPrivateKey()
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> signed = context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"), "production");
        ReleaseRecordCodec.Verify<ReleaseRecord>(ReleaseRecordCodec.Encode(signed), context.Policy).FailureCode.ShouldBe("invalid-signature");
    }

    /// <summary>Key resolution honors independent revocation, identity, key and kind enrollment.</summary>
    /// <param name="change">The trust configuration change.</param>
    [Theory]
    [InlineData("revoked")]
    [InlineData("unknown-writer")]
    [InlineData("unknown-key")]
    [InlineData("wrong-kind")]
    public void RejectsUnentitledEnrollment(string change)
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> signed = context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
        RecordWriterKey anchor = context.Anchors.Single(anchor => anchor.WriterId == "publication");
        anchor = change switch
        {
            "revoked" => anchor with { Revoked = true },
            "unknown-writer" => anchor with { WriterId = "unknown" },
            "unknown-key" => anchor with { KeyId = "unknown" },
            _ => anchor with { Kinds = ImmutableHashSet.Create(StringComparer.Ordinal, "attempt") },
        };
        ReleaseRecordCodec.Verify<ReleaseRecord>(ReleaseRecordCodec.Encode(signed), new([anchor])).FailureCode.ShouldBe("untrusted-writer");
    }

    /// <summary>Real executor signatures cannot be replayed into another environment.</summary>
    [Fact]
    public void RejectsCrossEnvironmentReplayAndWronglyScopedSignature()
    {
        using RecordTestContext context = new();
        RecordReference release = new("release-record", RecordTestContext.Digest);
        SignedRecord<AttemptRecord> production = context.Sign(RecordTestContext.Envelope(RecordTestContext.Attempt(release), "production", "production", "attempt"));
        SignedRecord<AttemptRecord> replay = production with { Environment = "staging" };
        ReleaseRecordCodec.Verify<AttemptRecord>(ReleaseRecordCodec.Encode(replay), context.Policy).Verified.ShouldBeNull();
        replay = context.Sign(replay);
        ReleaseRecordCodec.Verify<AttemptRecord>(ReleaseRecordCodec.Encode(replay), context.Policy).FailureCode.ShouldBe("untrusted-writer");
    }

    /// <summary>No operational role can claim Administrator, even with a valid signature.</summary>
    /// <param name="writer">The non-Administrator identity.</param>
    [Theory]
    [InlineData("publication")]
    [InlineData("production")]
    [InlineData("staging")]
    [InlineData("recovery")]
    [InlineData("monitor")]
    [InlineData("deputy")]
    public void AdministratorCannotBeSelfDeclared(string writer)
    {
        using RecordTestContext context = new();
        AdministratorRecord payload = new("return-to-g2", "production", null, null, null, null, "reviewed", RecordTestContext.Map(), null);
        SignedRecord<AdministratorRecord> signed = context.Sign(RecordTestContext.Envelope(payload, writer, "production", "admin-action", "production"));
        ReleaseRecordCodec.Verify<AdministratorRecord>(ReleaseRecordCodec.Encode(signed), context.Policy).FailureCode.ShouldBe("untrusted-writer");
    }

    /// <summary>Executor and recovery executor authority is separated by attempt kind.</summary>
    [Fact]
    public void RecoveryWriterCannotSignAnOrdinaryProductionAttempt()
    {
        using RecordTestContext context = new();
        AttemptRecord payload = RecordTestContext.Attempt(new("release", RecordTestContext.Digest));
        SignedRecord<AttemptRecord> recoveryRelease = context.Sign(RecordTestContext.Envelope(payload, "recovery", "production", "attempt"));
        ReleaseRecordCodec.Verify<AttemptRecord>(ReleaseRecordCodec.Encode(recoveryRelease), context.Policy).FailureCode.ShouldBe("untrusted-writer");
        payload = payload with { AttemptKind = "disaster-recovery", RecoveryAllowance = 0 };
        SignedRecord<AttemptRecord> executorDr = context.Sign(RecordTestContext.Envelope(payload, "production", "production", "attempt"));
        ReleaseRecordCodec.Verify<AttemptRecord>(ReleaseRecordCodec.Encode(executorDr), context.Policy).FailureCode.ShouldBe("untrusted-writer");
    }

    /// <summary>Identity and public-key aliases cannot hide shared Administrator custody.</summary>
    /// <param name="alias">The conflicting enrollment form.</param>
    [Theory]
    [InlineData("identity")]
    [InlineData("key")]
    [InlineData("mixed-role")]
    [InlineData("no-custody")]
    public void RejectsAdministratorCustodyOverlapAndAliases(string alias)
    {
        using RecordTestContext context = new();
        RecordWriterKey administrator = context.Anchors.Single(anchor => anchor.WriterId == "admin");
        RecordWriterKey executor = context.Anchors.Single(anchor => anchor.WriterId == "production");
        RecordWriterKey conflicting = alias switch
        {
            "identity" => executor with { WriterId = administrator.WriterId },
            "key" => executor with { PublicKeyPem = administrator.PublicKeyPem.Replace("\n", "\r\n", StringComparison.Ordinal) },
            "mixed-role" => administrator with { Roles = administrator.Roles.Add("executor"), KeyId = "alias-key" },
            _ => administrator with { ExclusiveAdministratorCustody = false, KeyId = "alias-key" },
        };
        _ = Should.Throw<RecordValidationException>(() => new RecordTrustPolicy([administrator, conflicting]));
    }

    /// <summary>Unsupported curves cannot enter independent public enrollment.</summary>
    [Fact]
    public void RejectsUnsupportedEllipticCurve()
    {
        using RecordTestContext context = new();
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        RecordWriterKey anchor = context.Anchors[0] with { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() };
        Should.Throw<RecordValidationException>(() => new RecordTrustPolicy([anchor])).Code.ShouldBe("invalid-trust-policy");
    }

    /// <summary>Verified context does not retain caller-mutable byte buffers or DTO state.</summary>
    [Fact]
    public void VerifiedInputsStayImmutableAfterCallerChanges()
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> signed = context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
        byte[] input = ReleaseRecordCodec.Encode(signed);
        VerifiedRecord<ReleaseRecord> verified = ReleaseRecordCodec.Verify<ReleaseRecord>(input, context.Policy).Verified.ShouldNotBeNull();
        string digest = verified.Digest;
        Array.Fill(input, (byte)0);
        signed = signed with { WriterId = "attacker", Payload = signed.Payload with { QualifiedEnvironmentLayer = ["unqualified"] } };
        verified.Record.WriterId.ShouldBe("publication");
        verified.Record.Payload.QualifiedEnvironmentLayer.ShouldBe(["postgres-18"]);
        verified.Digest.ShouldBe(digest);
        typeof(VerifiedRecord<ReleaseRecord>).GetConstructors().ShouldBeEmpty();
        signed.WriterId.ShouldBe("attacker");
    }

    /// <summary>Ordering, whitespace and equivalent escaped field names do not change authenticated content.</summary>
    [Fact]
    public void DeterministicEncodingIgnoresDictionaryAndPropertyOrder()
    {
        using RecordTestContext context = new();
        ReleaseRecord first = RecordTestContext.Release() with { ModuleArtifactDigests = RecordTestContext.Map(("z", RecordTestContext.Digest), ("a", RecordTestContext.Digest)) };
        ReleaseRecord second = first with { ModuleArtifactDigests = RecordTestContext.Map(("a", RecordTestContext.Digest), ("z", RecordTestContext.Digest)) };
        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(first, "publication", null, "release-record");
        ReleaseRecordCodec.GetSigningBytes(envelope).ShouldBe(ReleaseRecordCodec.GetSigningBytes(envelope with { Payload = second }));
        byte[] signed = ReleaseRecordCodec.Encode(context.Sign(envelope));
        JsonObject original = JsonNode.Parse(signed).ShouldBeOfType<JsonObject>();
        JsonObject reordered = new(original.Reverse().Select(entry => KeyValuePair.Create(entry.Key, entry.Value?.DeepClone())));
        byte[] reencoded = Encoding.UTF8.GetBytes(reordered.ToJsonString(new() { WriteIndented = true }));
        ReleaseRecordCodec.Verify<ReleaseRecord>(reencoded, context.Policy).Verified.ShouldNotBeNull().Digest.ShouldBe(ReleaseRecordCodec.Verify<ReleaseRecord>(signed, context.Policy).Verified.ShouldNotBeNull().Digest);
    }

    /// <summary>A fixed wire vector locks canonical spelling, sorting and signature exclusion.</summary>
    [Fact]
    public void CanonicalSigningVectorIsStable()
    {
        SignedRecord<PromotionStopRecord> envelope = RecordTestContext.Envelope(new PromotionStopRecord(1, null, "probe", []), "monitor", "production", "stop", "production");
        const string vector = """{"algorithm":"ES256","createdUtc":"2026-10-22T00:00:00\u002B00:00","environment":"production","keyId":"monitor-key","kind":"promotion-stop","payload":{"cause":"probe","evidenceDigests":[],"previousStop":null,"revision":1},"recordId":"stop","recordedUtc":"2026-10-22T00:00:00\u002B00:00","schema":"https://schemas.hexalith.com/platform/promotion-stop.v1","subject":"production","writerId":"monitor"}""";
        Encoding.UTF8.GetString(ReleaseRecordCodec.GetSigningBytes(envelope)).ShouldBe(vector);
    }

    private static void AssertDenied<T>(RecordTestContext context, SignedRecord<T> envelope)
        where T : class => ReleaseRecordCodec.Verify<T>(ReleaseRecordCodec.Encode(context.Sign(envelope)), context.Policy).FailureCode.ShouldBe("untrusted-writer");
}