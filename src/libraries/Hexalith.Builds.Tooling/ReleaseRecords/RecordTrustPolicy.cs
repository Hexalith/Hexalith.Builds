// <copyright file="RecordTrustPolicy.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;
using System.Security.Cryptography;

/// <summary>
/// Snapshots independent public-key enrollment and rejects Administrator custody overlap.
/// </summary>
public sealed class RecordTrustPolicy
{
    private static readonly ImmutableHashSet<string> _roles = ImmutableHashSet.Create(StringComparer.Ordinal, "publication", "executor", "recovery-executor", "monitor", "administrator", "deputy");
    private readonly Dictionary<(string Writer, string Key), RecordWriterKey> _anchors = [];

    /// <summary>Initializes a new instance of the <see cref="RecordTrustPolicy"/> class from independent enrollment.</summary>
    /// <param name="anchors">Public keys, revocation state, explicit roles and environment/kind scopes.</param>
    public RecordTrustPolicy(IEnumerable<RecordWriterKey> anchors)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        Dictionary<string, bool> identities = new(StringComparer.Ordinal);
        Dictionary<string, bool> fingerprints = new(StringComparer.Ordinal);
        foreach (RecordWriterKey supplied in anchors)
        {
            if (supplied is null || string.IsNullOrWhiteSpace(supplied.WriterId) || string.IsNullOrWhiteSpace(supplied.KeyId) ||
                supplied.Roles is null || supplied.Kinds is null || supplied.Environments is null || supplied.PublicKeyPem is null)
            {
                throw new RecordValidationException("invalid-trust-policy");
            }

            RecordWriterKey anchor = supplied with
            {
                Roles = supplied.Roles.ToImmutableHashSet(StringComparer.Ordinal),
                Kinds = supplied.Kinds.ToImmutableHashSet(StringComparer.Ordinal),
                Environments = supplied.Environments.ToImmutableHashSet(StringComparer.Ordinal),
            };
            bool administrator = anchor.Roles.Contains("administrator");
            if (anchor.Roles.Count == 0 || !anchor.Roles.IsSubsetOf(_roles) || anchor.Kinds.Count == 0 ||
                anchor.Kinds.Any(kind => !ReleaseRecordCodec.IsSupportedKind(kind)) ||
                anchor.Environments.Any(environment => environment is not ("staging" or "production")) ||
                (administrator && anchor.Roles.Count != 1) || administrator != anchor.ExclusiveAdministratorCustody ||
                !_anchors.TryAdd((anchor.WriterId, anchor.KeyId), anchor))
            {
                throw new RecordValidationException("invalid-trust-policy");
            }

            string fingerprint;
            try
            {
                using ECDsa key = ImportPublicKey(anchor.PublicKeyPem);
                fingerprint = Convert.ToHexStringLower(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            }
            catch (Exception exception) when (exception is CryptographicException or ArgumentException)
            {
                throw new RecordValidationException("invalid-trust-policy");
            }

            if ((identities.TryGetValue(anchor.WriterId, out bool identityAdministrator) && identityAdministrator != administrator) ||
                (fingerprints.TryGetValue(fingerprint, out bool keyAdministrator) && keyAdministrator != administrator))
            {
                throw new RecordValidationException("administrator-custody-overlap");
            }

            identities[anchor.WriterId] = administrator;
            fingerprints[fingerprint] = administrator;
        }
    }

    /// <summary>Imports a P-256 public anchor without accepting private-key PEM.</summary>
    /// <param name="pem">The public-key PEM.</param>
    /// <returns>The imported public key, owned by the caller.</returns>
    /// <exception cref="CryptographicException">The key is not an admitted public P-256 key.</exception>
    internal static ECDsa ImportPublicKey(string pem)
    {
        if (!pem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
            !pem.TrimEnd().EndsWith("-----END PUBLIC KEY-----", StringComparison.Ordinal))
        {
            throw new CryptographicException();
        }

        ECDsa key = ECDsa.Create();
        try
        {
            key.ImportFromPem(pem);
            EnsureP256(key);
            return key;
        }
        catch
        {
            key.Dispose();
            throw;
        }
    }

    /// <summary>Requires the standard P-256 curve.</summary>
    /// <param name="key">The key to inspect.</param>
    /// <exception cref="CryptographicException">The curve is unsupported.</exception>
    internal static void EnsureP256(ECDsa key)
    {
        if (key.ExportParameters(false).Curve.Oid.Value != "1.2.840.10045.3.1.7")
        {
            throw new CryptographicException();
        }
    }

    /// <summary>Resolves an entitled, non-revoked independently enrolled writer.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="record">The record claims to compare with enrollment.</param>
    /// <returns>The entitled public anchor, or null.</returns>
    internal RecordWriterKey? Resolve<T>(SignedRecord<T> record)
        where T : class
    {
        if (!_anchors.TryGetValue((record.WriterId, record.KeyId), out RecordWriterKey? anchor) || anchor.Revoked ||
            !anchor.Kinds.Contains(record.Kind) ||
            (record.Environment is not null && !anchor.Environments.Contains(record.Environment)))
        {
            return null;
        }

        bool entitled = record.Kind switch
        {
            "release" or "release-available" => anchor.Roles.Contains("publication"),
            "administrator" => anchor.Roles.Contains("administrator") && anchor.ExclusiveAdministratorCustody,
            "attempt" when record.Payload is AttemptRecord { Outcome: "superseded" } => IsExecutor(anchor, record.Environment),
            "attempt" when record.Payload is AttemptRecord { AttemptKind: "disaster-recovery" } => record.Environment == "production" && anchor.Roles.Contains("recovery-executor"),
            "attempt" => anchor.Roles.Contains("executor"),
            "qualification" or "latest-working" => IsExecutor(anchor, record.Environment),
            "production-promoted" => record.Environment == "production" && IsExecutor(anchor, record.Environment),
            "promotion-stop" => IsExecutor(anchor, record.Environment) || anchor.Roles.Overlaps(["monitor", "administrator", "deputy"]),
            _ => false,
        };
        return entitled ? anchor : null;
    }

    private static bool IsExecutor(RecordWriterKey anchor, string? environment) =>
        anchor.Roles.Contains("executor") || (environment == "production" && anchor.Roles.Contains("recovery-executor"));
}