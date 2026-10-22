// <copyright file="VerifiedRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Immutable verified content; only the codec can construct this trusted context.
/// </summary>
/// <typeparam name="T">The verified payload type.</typeparam>
public sealed class VerifiedRecord<T>
    where T : class
{
    /// <summary>Initializes a new instance of the <see cref="VerifiedRecord{T}"/> class.</summary>
    /// <param name="record">The immutable verified record.</param>
    /// <param name="digest">The verified content digest.</param>
    internal VerifiedRecord(SignedRecord<T> record, string digest)
    {
        Record = record;
        Digest = digest;
    }

    /// <summary>Gets the immutable verified envelope and payload.</summary>
    public SignedRecord<T> Record { get; }

    /// <summary>Gets the SHA-256 digest of canonical signing bytes.</summary>
    public string Digest { get; }

    /// <summary>Gets an exact content reference for later verified transitions.</summary>
    public RecordReference Reference => new(Record.RecordId, Digest);
}