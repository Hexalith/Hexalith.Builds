// <copyright file="ReleaseRecordCodec.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Hexalith.Builds.Tooling.Manifest;

using Json.Schema;

/// <summary>
/// Encodes bounded, closed Platform records and verifies independent P-256 writer signatures.
/// </summary>
public static class ReleaseRecordCodec
{
    /// <summary>The maximum accepted UTF-8 record size, one MiB.</summary>
    public const int MaximumRecordBytes = 1_048_576;

    private static readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    private static readonly JsonSchema _schema = ReadSchema();

    /// <summary>Gets the distinct versioned schema identifier for a payload type.</summary>
    /// <typeparam name="T">The supported payload type.</typeparam>
    /// <returns>The schema identifier.</returns>
    public static string SchemaFor<T>()
        where T : class => "https://schemas.hexalith.com/platform/" + KindFor<T>() + ".v1";

    /// <summary>Gets the fixed wire kind for a payload type.</summary>
    /// <typeparam name="T">The supported payload type.</typeparam>
    /// <returns>The record kind.</returns>
    /// <exception cref="RecordValidationException">The payload type is unsupported.</exception>
    public static string KindFor<T>()
        where T : class => typeof(T).Name switch
        {
            nameof(ReleaseRecord) when typeof(T) == typeof(ReleaseRecord) => "release",
            nameof(AttemptRecord) when typeof(T) == typeof(AttemptRecord) => "attempt",
            nameof(QualificationRecord) when typeof(T) == typeof(QualificationRecord) => "qualification",
            nameof(ProductionPromotedRecord) when typeof(T) == typeof(ProductionPromotedRecord) => "production-promoted",
            nameof(ReleaseAvailableRecord) when typeof(T) == typeof(ReleaseAvailableRecord) => "release-available",
            nameof(LatestWorkingPointerRecord) when typeof(T) == typeof(LatestWorkingPointerRecord) => "latest-working",
            nameof(PromotionStopRecord) when typeof(T) == typeof(PromotionStopRecord) => "promotion-stop",
            nameof(AdministratorRecord) when typeof(T) == typeof(AdministratorRecord) => "administrator",
            _ => throw new RecordValidationException("unsupported-record"),
        };

    /// <summary>Signs every accepted field except the signature value using a caller-held private key.</summary>
    /// <typeparam name="T">The supported payload type.</typeparam>
    /// <param name="record">The envelope to sign; its existing signature is replaced.</param>
    /// <param name="privateKey">The caller-held P-256 private key, never retained.</param>
    /// <returns>The signed, still untrusted envelope.</returns>
    /// <exception cref="RecordValidationException">The record or caller-held signing key is invalid.</exception>
    public static SignedRecord<T> Sign<T>(SignedRecord<T> record, ECDsa privateKey)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(privateKey);
        try
        {
            RecordTrustPolicy.EnsureP256(privateKey);
            SignedRecord<T> candidate = record with { Signature = Convert.ToBase64String(new byte[64]) };
            Validate(candidate);
            byte[] signature = privateKey.SignData(Serialize(candidate, true), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return candidate with { Signature = Convert.ToBase64String(signature) };
        }
        catch (CryptographicException)
        {
            throw new RecordValidationException("invalid-signing-key");
        }
    }

    /// <summary>Encodes a validated envelope as deterministic UTF-8 JSON without a BOM or newline.</summary>
    /// <typeparam name="T">The supported payload type.</typeparam>
    /// <param name="record">The untrusted signed envelope.</param>
    /// <returns>The canonical encoded bytes.</returns>
    public static byte[] Encode<T>(SignedRecord<T> record)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record);
        return Serialize(record, false);
    }

    /// <summary>Returns the canonical signing bytes, including algorithm but excluding signature.</summary>
    /// <typeparam name="T">The supported payload type.</typeparam>
    /// <param name="record">The envelope whose content is to be signed.</param>
    /// <returns>Deterministic signing bytes.</returns>
    public static byte[] GetSigningBytes<T>(SignedRecord<T> record)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(record);
        Validate(record with { Signature = Convert.ToBase64String(new byte[64]) });
        return Serialize(record, true);
    }

    /// <summary>Strictly decodes and verifies a record without trusting any self-declared authority.</summary>
    /// <typeparam name="T">The required payload type; a different kind fails closed.</typeparam>
    /// <param name="utf8">The bounded UTF-8 JSON input.</param>
    /// <param name="trust">The independently supplied public trust configuration.</param>
    /// <returns>Immutable trusted content and its digest, or a fixed failure code.</returns>
    public static RecordVerificationResult<T> Verify<T>(ReadOnlyMemory<byte> utf8, RecordTrustPolicy trust)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(trust);
        if (utf8.Length > MaximumRecordBytes)
        {
            return new(null, "record-too-large");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (JsonDuplicatePropertyValidator.FindDuplicateProperty(root) is not null)
            {
                return new(null, "ambiguous-json");
            }

            ValidateSchema(root);
            SignedRecord<T>? record = root.Deserialize<SignedRecord<T>>(_json);
            if (record is null)
            {
                return new(null, "schema-rejected");
            }

            Validate(record);

            // Reject alternative number/date spellings that would lose accepted fields on typed binding.
            if (!Canonicalize(root, false).AsSpan().SequenceEqual(Serialize(record, false)))
            {
                return new(null, "noncanonical-value");
            }

            RecordWriterKey? anchor = trust.Resolve(record);
            if (anchor is null)
            {
                return new(null, "untrusted-writer");
            }

            byte[] signingBytes = Serialize(record, true);
            byte[] signature = Convert.FromBase64String(record.Signature);
            using ECDsa publicKey = RecordTrustPolicy.ImportPublicKey(anchor.PublicKeyPem);
            if (signature.Length != 64 || !publicKey.VerifyData(signingBytes, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return new(null, "invalid-signature");
            }

            string digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(signingBytes));
            return new(new VerifiedRecord<T>(record, digest), null);
        }
        catch (RecordValidationException exception)
        {
            return new(null, exception.Code);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or ArgumentException or InvalidOperationException or CryptographicException or NotSupportedException or OverflowException)
        {
            return new(null, "invalid-record");
        }
    }

    /// <summary>Checks the closed wire-kind vocabulary.</summary>
    /// <param name="kind">The candidate kind.</param>
    /// <returns>Whether the kind is supported.</returns>
    internal static bool IsSupportedKind(string kind) => kind is "release" or "attempt" or "qualification" or "production-promoted" or "release-available" or "latest-working" or "promotion-stop" or "administrator";

    /// <summary>Compares canonical typed content independent of dictionary order.</summary>
    /// <typeparam name="T">The content type.</typeparam>
    /// <param name="left">The first content.</param>
    /// <param name="right">The second content.</param>
    /// <returns>Whether the canonical content is identical.</returns>
    internal static bool SameContent<T>(T left, T right)
        where T : class => Canonicalize(JsonSerializer.SerializeToElement(left, _json), false).AsSpan().SequenceEqual(Canonicalize(JsonSerializer.SerializeToElement(right, _json), false));

    private static byte[] Canonicalize(JsonElement element, bool omitSignature)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            WriteCanonical(writer, element, omitSignature);
        }

        return stream.ToArray();
    }

    private static JsonSchema ReadSchema()
    {
        using Stream stream = typeof(ReleaseRecordCodec).Assembly.GetManifestResourceStream("Hexalith.Builds.Tooling.ReleaseRecords.hexalith.release-records.v1.json")
            ?? throw new InvalidOperationException("The embedded release records schema is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return JsonSchema.FromText(reader.ReadToEnd(), new BuildOptions
        {
            SchemaRegistry = new SchemaRegistry { Fetch = (_, _) => throw new InvalidOperationException("Release record schemas cannot fetch remote resources.") },
        });
    }

    private static byte[] Serialize<T>(SignedRecord<T> record, bool omitSignature)
        where T : class => Canonicalize(JsonSerializer.SerializeToElement(record, _json), omitSignature);

    private static void Validate<T>(SignedRecord<T> record)
        where T : class
    {
        if (record.Kind != KindFor<T>() || record.Schema != SchemaFor<T>())
        {
            throw new RecordValidationException("unsupported-record");
        }

        byte[] encoded = Serialize(record, false);
        if (encoded.Length > MaximumRecordBytes)
        {
            throw new RecordValidationException("record-too-large");
        }

        using JsonDocument document = JsonDocument.Parse(encoded);
        ValidateSchema(document.RootElement);
        if (record.CreatedUtc.Offset != TimeSpan.Zero || record.RecordedUtc.Offset != TimeSpan.Zero || record.RecordedUtc < record.CreatedUtc)
        {
            throw new RecordValidationException("invalid-timing");
        }

        if (record.Payload is AttemptRecord attempt)
        {
            DateTimeOffset?[] timestamps = [attempt.EntryUtc, attempt.FirstMutationUtc, attempt.OutageUtc, attempt.PreMutationDeadlineUtc, attempt.RolloutDeadlineUtc, attempt.VerificationDeadlineUtc, attempt.MaximumLifetimeUtc];
            bool superseded = attempt.Outcome == "superseded";
            bool hasSuccessor = attempt.SupersededBy is not null;
            bool recovery = attempt.AttemptKind is "in-place-restore" or "disaster-recovery";
            DateTimeOffset?[] phaseDeadlines = [attempt.RolloutDeadlineUtc, attempt.VerificationDeadlineUtc];
            if (timestamps.Any(time => time.HasValue && time.Value.Offset != TimeSpan.Zero) ||
                attempt.EntryUtc > record.RecordedUtc || attempt.PreMutationDeadlineUtc <= attempt.EntryUtc || attempt.MaximumLifetimeUtc <= attempt.EntryUtc ||
                attempt.GraceSeconds > attempt.CheckTimeoutSeconds + 30 || attempt.RecoveriesConsumed > attempt.RecoveryAllowance ||
                (attempt.AttemptKind is "in-place-restore" or "disaster-recovery" && attempt.RecoveryAllowance != 0) ||
                (attempt.FirstMutationUtc.HasValue && (attempt.FirstMutationUtc < attempt.EntryUtc || attempt.FirstMutationUtc > record.RecordedUtc)) ||
                attempt.OutageUtc > record.RecordedUtc || phaseDeadlines.Any(deadline => deadline < attempt.EntryUtc) ||
                (!recovery && phaseDeadlines.Any(deadline => deadline > attempt.MaximumLifetimeUtc)) ||
                superseded != hasSuccessor)
            {
                throw new RecordValidationException("invalid-attempt");
            }
        }

        if ((record.Payload is ReleaseRecord release && record.Subject != release.ReleaseId) ||
            (record.Payload is PromotionStopRecord or LatestWorkingPointerRecord && record.Subject != record.Environment) ||
            (record.Payload is AdministratorRecord administrator && record.Subject != administrator.Target))
        {
            throw new RecordValidationException("invalid-subject");
        }

        if (record.Payload is AdministratorRecord { Action: "clear-promotion-stop" } clear &&
            (clear.ObservedStop is null || clear.ExpectedStopRevision is null))
        {
            throw new RecordValidationException("invalid-stop-clearance");
        }
    }

    private static void ValidateSchema(JsonElement root)
    {
        if (!_schema.Evaluate(root).IsValid)
        {
            throw new RecordValidationException("schema-rejected");
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element, bool omitSignature = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    if (omitSignature && property.Name == "signature")
                    {
                        continue;
                    }

                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteNumberValue(element.GetInt64());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
            case JsonValueKind.Null:
                element.WriteTo(writer);
                break;
            case JsonValueKind.Undefined:
                throw new RecordValidationException("undefined-json-value");
            default:
                throw new RecordValidationException("invalid-record");
        }
    }
}