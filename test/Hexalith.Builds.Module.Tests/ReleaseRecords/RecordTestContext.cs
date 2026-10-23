// <copyright file="RecordTestContext.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.ReleaseRecords;

using System.Collections.Immutable;
using System.Security.Cryptography;

using Hexalith.Builds.Tooling.ReleaseRecords;

using Shouldly;

/// <summary>Supplies real ephemeral keys and immutable record fixtures.</summary>
internal sealed class RecordTestContext : IDisposable
{
    private readonly Dictionary<string, ECDsa> _keys = new(StringComparer.Ordinal);

    /// <summary>Initializes a new instance of the <see cref="RecordTestContext"/> class.</summary>
    internal RecordTestContext()
    {
        string[] kinds = ["release", "attempt", "qualification", "production-promoted", "release-available", "latest-working", "promotion-stop", "administrator"];
        List<RecordWriterKey> anchors = [];
        foreach ((string writer, string role, string[] environments) in new (string, string, string[])[]
        {
            ("publication", "publication", []), ("staging", "executor", ["staging"]), ("production", "executor", ["production"]),
            ("other", "executor", ["production"]), ("recovery", "recovery-executor", ["production"]),
            ("monitor", "monitor", ["staging", "production"]), ("admin", "administrator", ["staging", "production"]), ("deputy", "deputy", ["staging", "production"]),
        })
        {
            ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _keys.Add(writer, key);
            anchors.Add(new(writer, writer + "-key", key.ExportSubjectPublicKeyInfoPem(), ImmutableHashSet.Create(StringComparer.Ordinal, role), kinds.ToImmutableHashSet(StringComparer.Ordinal), environments.ToImmutableHashSet(StringComparer.Ordinal), role == "administrator", false));
        }

        Anchors = [.. anchors];
        Policy = new(Anchors);
    }

    /// <summary>Gets a fixed valid content digest.</summary>
    internal static string Digest => "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>Gets the fixture timestamp.</summary>
    internal static DateTimeOffset Now => new(2026, 10, 22, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Gets independent public enrollment.</summary>
    internal ImmutableArray<RecordWriterKey> Anchors { get; }

    /// <summary>Gets the fixture trust policy.</summary>
    internal RecordTrustPolicy Policy { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (ECDsa key in _keys.Values)
        {
            key.Dispose();
        }
    }

    /// <summary>Builds immutable release inputs.</summary>
    /// <returns>The release payload.</returns>
    internal static ReleaseRecord Release() => new("release-1", Digest, Map(("module", Digest)), Digest, Digest, Digest, Digest, "1.18.3", "realm-v1", "recovery-v1", Map(("smoke", Digest)), ["postgres-18"], ["kubernetes-1.35"], Map(("module", "compatible")), Map(("attestation", Digest)));

    /// <summary>Builds an immutable named dictionary.</summary>
    /// <param name="pairs">The dictionary entries.</param>
    /// <returns>The immutable dictionary.</returns>
    internal static ImmutableDictionary<string, string> Map(params (string Key, string Value)[] pairs) => pairs.ToImmutableDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

    /// <summary>Builds a value-free environment snapshot.</summary>
    /// <returns>The environment bindings.</returns>
    internal static EnvironmentCurrent Current() => new(Map(("database", "secret-ref")), Map(("database", "read-write")), 1, 1, Digest, Digest, "realm-v1", Digest, ["app.example"], Map(("deploy", "credential-ref")));

    /// <summary>Builds a finite attempt.</summary>
    /// <param name="release">The release reference.</param>
    /// <param name="epoch">The lock epoch.</param>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The attempt payload.</returns>
    internal static AttemptRecord Attempt(RecordReference release, long epoch = 1, string outcome = "in-progress") => new(release, "release", Current(), "normal", epoch, Now, null, null, Now.AddMinutes(5), null, null, Now.AddMinutes(5), 30, 30, 1, 0, null, "open", outcome, null, null);

    /// <summary>Builds an untrusted envelope.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="payload">The payload.</param>
    /// <param name="writer">The claimed writer.</param>
    /// <param name="environment">The target environment.</param>
    /// <param name="id">The record identifier.</param>
    /// <param name="subject">The subject.</param>
    /// <returns>The envelope.</returns>
    internal static SignedRecord<T> Envelope<T>(T payload, string writer, string? environment, string id, string subject = "release-1")
        where T : class => new(ReleaseRecordCodec.SchemaFor<T>(), ReleaseRecordCodec.KindFor<T>(), id, writer, writer + "-key", subject, environment, Now, Now, payload, "ES256", string.Empty);

    /// <summary>Signs an envelope with a real caller-held ephemeral key.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="record">The envelope.</param>
    /// <param name="signer">An alternate actual signer, if requested.</param>
    /// <returns>The signed envelope.</returns>
    internal SignedRecord<T> Sign<T>(SignedRecord<T> record, string? signer = null)
        where T : class => ReleaseRecordCodec.Sign(record, _keys[signer ?? record.WriterId]);

    /// <summary>Signs and verifies fixture content.</summary>
    /// <typeparam name="T">The payload type.</typeparam>
    /// <param name="record">The envelope.</param>
    /// <returns>The independently verified content.</returns>
    internal VerifiedRecord<T> Verify<T>(SignedRecord<T> record)
        where T : class
    {
        RecordVerificationResult<T> result = ReleaseRecordCodec.Verify<T>(ReleaseRecordCodec.Encode(Sign(record)), Policy);
        result.FailureCode.ShouldBeNull();
        return result.Verified.ShouldNotBeNull();
    }
}