// <copyright file="RecordSchemaTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.ReleaseRecords;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.ReleaseRecords;

using Shouldly;

using Xunit;

/// <summary>Exercises the eight closed, distinct versioned record contracts.</summary>
public sealed class RecordSchemaTests
{
    /// <summary>All eight kinds round-trip under separately enrolled real keys.</summary>
    [Fact]
    public void AllEightKindsRoundTripWithDistinctSchemas()
    {
        using RecordTestContext context = new();
        VerifiedRecord<ReleaseRecord> release = context.Verify(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record"));
        VerifiedRecord<AttemptRecord> staging = context.Verify(RecordTestContext.Envelope(RecordTestContext.Attempt(release.Reference, outcome: "working"), "staging", "staging", "staging-attempt"));
        VerifiedRecord<AttemptRecord> production = context.Verify(RecordTestContext.Envelope(RecordTestContext.Attempt(release.Reference), "production", "production", "production-attempt"));
        VerifiedRecord<QualificationRecord> qualification = context.Verify(RecordTestContext.Envelope(new QualificationRecord(release.Reference, production.Reference, RecordTestContext.Digest, ["postgres-18"], ["kubernetes-1.35"], [RecordTestContext.Digest]), "production", "production", "qualification"));
        VerifiedRecord<ProductionPromotedRecord> promoted = context.Verify(RecordTestContext.Envelope(new ProductionPromotedRecord(release.Reference, production.Reference, qualification.Reference, RecordTestContext.Digest), "production", "production", "promotion"));
        VerifiedRecord<ReleaseAvailableRecord> available = context.Verify(RecordTestContext.Envelope(new ReleaseAvailableRecord(release.Reference, staging.Reference, RecordTestContext.Digest), "publication", null, "available"));
        VerifiedRecord<LatestWorkingPointerRecord> latest = context.Verify(RecordTestContext.Envelope(new LatestWorkingPointerRecord(release.Reference, staging.Reference, 1), "staging", "staging", "latest-staging", "staging"));
        VerifiedRecord<PromotionStopRecord> stop = context.Verify(RecordTestContext.Envelope(new PromotionStopRecord(1, null, "probe-failed", [RecordTestContext.Digest]), "monitor", "production", "stop", "production"));
        VerifiedRecord<AdministratorRecord> administrator = context.Verify(RecordTestContext.Envelope(new AdministratorRecord("clear-promotion-stop", "production", null, stop.Reference, 1, null, "reviewed", RecordTestContext.Map(("probe-failed", "resolved")), null), "admin", "production", "clear", "production"));
        string[] schemas = [release.Record.Schema, staging.Record.Schema, qualification.Record.Schema, promoted.Record.Schema, available.Record.Schema, latest.Record.Schema, stop.Record.Schema, administrator.Record.Schema];
        schemas.Distinct(StringComparer.Ordinal).Count().ShouldBe(8);
        RecordTransitionValidator.ValidateAttempt(staging, release, 1);
        RecordTransitionValidator.ValidateQualification(qualification, release, production);
        RecordTransitionValidator.ValidateProductionPromoted(promoted, release, production, qualification);
        RecordTransitionValidator.ValidateReleaseAvailable(available, release, staging);
        RecordTransitionValidator.ValidateLatestWorking(latest, release, staging, 1);
        RecordTransitionValidator.ValidatePromotionStop(stop, 0);
        RecordTransitionValidator.ValidateAdministrator(administrator, stop);
    }

    /// <summary>Unknown fields and unsupported discriminators fail before trusted data is returned.</summary>
    /// <param name="field">The changed envelope field.</param>
    /// <param name="value">The substituted value.</param>
    [Theory]
    [InlineData("schema", "https://schemas.hexalith.com/platform/release.v2")]
    [InlineData("kind", "unknown")]
    [InlineData("algorithm", "HS256")]
    [InlineData("unexpected", "sensitive-input")]
    public void RejectsUnknownEnvelopeFieldsAndDiscriminators(string field, string value)
    {
        using RecordTestContext context = new();
        JsonObject json = SignedRelease(context);
        json[field] = value;
        RecordVerificationResult<ReleaseRecord> result = ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json.ToJsonString()), context.Policy);
        result.Verified.ShouldBeNull();
        result.FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Nested extension fields cannot bypass the closed payload contract.</summary>
    [Fact]
    public void RejectsUnknownPayloadFields()
    {
        using RecordTestContext context = new();
        JsonObject json = SignedRelease(context);
        json["payload"]!["administratorCustody"] = true;
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json.ToJsonString()), context.Policy).FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Escaped property names are compared after JSON decoding.</summary>
    /// <param name="replacement">An inserted duplicate field sequence.</param>
    [Theory]
    [InlineData("\"kind\":\"release\",\"kind\":\"release\",")]
    [InlineData("\"kind\":\"release\",\"k\\u0069nd\":\"release\",")]
    public void RejectsDuplicateAndEscapedDuplicateProperties(string replacement)
    {
        using RecordTestContext context = new();
        string json = SignedRelease(context).ToJsonString().Replace("\"kind\":\"release\",", replacement, StringComparison.Ordinal);
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json), context.Policy).FailureCode.ShouldBe("ambiguous-json");
    }

    /// <summary>Nested duplicates are rejected before dictionary binding can discard them.</summary>
    [Fact]
    public void RejectsNestedDuplicateProperties()
    {
        using RecordTestContext context = new();
        string json = SignedRelease(context).ToJsonString().Replace("\"module\":", "\"module\":\"discarded\",\"module\":", StringComparison.Ordinal);
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json), context.Policy).FailureCode.ShouldBe("ambiguous-json");
    }

    /// <summary>Input is bounded and trailing syntax is rejected.</summary>
    /// <param name="input">Invalid JSON text.</param>
    [Theory]
    [InlineData("{} {}")]
    [InlineData("{\"kind\":\"release\",}")]
    [InlineData("/*comment*/{}")]
    [InlineData("null")]
    public void RejectsMalformedJson(string input)
    {
        using RecordTestContext context = new();
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(input), context.Policy).Verified.ShouldBeNull();
    }

    /// <summary>Oversize, malformed UTF-8 and excessive nesting fail closed.</summary>
    [Fact]
    public void EnforcesEncodingBounds()
    {
        using RecordTestContext context = new();
        ReleaseRecordCodec.Verify<ReleaseRecord>(new byte[ReleaseRecordCodec.MaximumRecordBytes + 1], context.Policy).FailureCode.ShouldBe("record-too-large");
        ReleaseRecordCodec.Verify<ReleaseRecord>(new byte[] { 0xff }, context.Policy).Verified.ShouldBeNull();
        string deep = new string('[', 40) + "0" + new string(']', 40);
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(deep), context.Policy).Verified.ShouldBeNull();
    }

    /// <summary>The requested typed contract cannot reinterpret another valid record kind.</summary>
    [Fact]
    public void RejectsWrongRequestedPayloadType()
    {
        using RecordTestContext context = new();
        ReleaseRecordCodec.Verify<AttemptRecord>(Encoding.UTF8.GetBytes(SignedRelease(context).ToJsonString()), context.Policy).Verified.ShouldBeNull();
    }

    /// <summary>True whole-string patterns reject signed identifier and digest suffixes.</summary>
    /// <param name="field">The modified field family.</param>
    [Theory]
    [InlineData("identifier")]
    [InlineData("digest")]
    public void RejectsSignedTrailingNewlines(string field)
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release");
        RecordVerificationResult<ReleaseRecord> result = VerifySignedMutation(context, envelope, json =>
        {
            if (field == "identifier")
            {
                json["recordId"] = "release\n";
            }
            else
            {
                json["payload"]!["applicationPackageDigest"] = RecordTestContext.Digest + "\n";
            }
        });
        result.FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Signature spelling rejects whitespace and nonzero unused base64 padding bits.</summary>
    /// <param name="change">The equivalent but noncanonical signature spelling.</param>
    [Theory]
    [InlineData("newline")]
    [InlineData("padding-bits")]
    public void RejectsNoncanonicalSignatureText(string change)
    {
        using RecordTestContext context = new();
        JsonObject json = SignedRelease(context);
        string original = json["signature"]!.GetValue<string>();
        char[] characters = original.ToCharArray();
        characters[85]++;
        string changed = change == "newline" ? original + "\n" : new string(characters);
        Convert.FromBase64String(changed).ShouldBe(Convert.FromBase64String(original));
        json["signature"] = changed;
        ReleaseRecordCodec.Verify<ReleaseRecord>(Encoding.UTF8.GetBytes(json.ToJsonString()), context.Policy).FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Every identifier endpoint excludes both C0 and C1 control characters.</summary>
    /// <param name="identifier">The invalid endpoint spelling.</param>
    [Theory]
    [InlineData("\0release")]
    [InlineData("release\0")]
    [InlineData("\u001frelease")]
    [InlineData("release\u001f")]
    [InlineData("\u007frelease")]
    [InlineData("release\u007f")]
    [InlineData("\u009frelease")]
    [InlineData("release\u009f")]
    public void RejectsSignedIdentifierEndpointControls(string identifier)
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release");
        VerifySignedMutation(context, envelope, json => json["recordId"] = identifier).FailureCode.ShouldBe("schema-rejected");
    }

    /// <summary>Ordinary single-character, spaced and Unicode identifiers retain their spelling.</summary>
    /// <param name="identifier">The accepted spelling.</param>
    [Theory]
    [InlineData("x")]
    [InlineData("release-1")]
    [InlineData("release with spaces")]
    [InlineData("é-release")]
    public void PreservesOrdinaryIdentifierSpelling(string identifier)
    {
        using RecordTestContext context = new();
        SignedRecord<ReleaseRecord> envelope = RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release");
        VerifySignedMutation(context, envelope, json => json["recordId"] = identifier).Verified.ShouldNotBeNull().Record.RecordId.ShouldBe(identifier);
    }

    /// <summary>The wire duration bound agrees with the typed Int32 recovery duration.</summary>
    /// <param name="duration">The signed duration.</param>
    /// <param name="accepted">Whether the typed boundary admits it.</param>
    [Theory]
    [InlineData(2147483647L, true)]
    [InlineData(2147483648L, false)]
    public void NamedRecoveryDurationMatchesTheInt32Boundary(long duration, bool accepted)
    {
        using RecordTestContext context = new();
        AdministratorRecord payload = new("approve-release", "release-1", null, null, null, null, "reviewed", RecordTestContext.Map(), new("restore", ["smoke"], 1));
        SignedRecord<AdministratorRecord> envelope = RecordTestContext.Envelope(payload, "admin", "production", "approval");
        RecordVerificationResult<AdministratorRecord> result = VerifySignedMutation(context, envelope, json => json["payload"]!["namedRecovery"]!["maximumDurationSeconds"] = duration);
        if (accepted)
        {
            result.Verified.ShouldNotBeNull().Record.Payload.NamedRecovery.ShouldNotBeNull().MaximumDurationSeconds.ShouldBe(int.MaxValue);
        }
        else
        {
            result.FailureCode.ShouldBe("schema-rejected");
            result.Verified.ShouldBeNull();
        }
    }

    private static RecordVerificationResult<T> VerifySignedMutation<T>(RecordTestContext context, SignedRecord<T> envelope, Action<JsonObject> mutate)
        where T : class
    {
        using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        JsonObject json = JsonNode.Parse(ReleaseRecordCodec.GetSigningBytes(envelope)).ShouldBeOfType<JsonObject>();
        mutate(json);
        byte[] signature = key.SignData(Encoding.UTF8.GetBytes(json.ToJsonString()), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        json["signature"] = Convert.ToBase64String(signature);
        RecordWriterKey anchor = context.Anchors.Single(anchor => anchor.WriterId == envelope.WriterId) with { PublicKeyPem = key.ExportSubjectPublicKeyInfoPem() };
        return ReleaseRecordCodec.Verify<T>(Encoding.UTF8.GetBytes(json.ToJsonString()), new([anchor]));
    }

    private static JsonObject SignedRelease(RecordTestContext context) => JsonNode.Parse(ReleaseRecordCodec.Encode(context.Sign(RecordTestContext.Envelope(RecordTestContext.Release(), "publication", null, "release-record")))).ShouldBeOfType<JsonObject>();
}