// <copyright file="SignedRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Carries an untrusted signed record until independently verified.
/// </summary>
/// <typeparam name="T">One of the eight supported payload contracts.</typeparam>
/// <param name="Schema">The distinct versioned schema identifier.</param>
/// <param name="Kind">The record kind.</param>
/// <param name="RecordId">The record identity.</param>
/// <param name="WriterId">The independently enrolled writer identity.</param>
/// <param name="KeyId">The enrolled public-key identifier.</param>
/// <param name="Subject">The release or environment subject.</param>
/// <param name="Environment">The target environment, absent only for publication records.</param>
/// <param name="CreatedUtc">The original record creation timestamp.</param>
/// <param name="RecordedUtc">The timestamp of this signed revision.</param>
/// <param name="Payload">The typed payload.</param>
/// <param name="Algorithm">The signature algorithm, ES256.</param>
/// <param name="Signature">The base64 P1363 signature over canonical signing bytes.</param>
public sealed record SignedRecord<T>(
    string Schema,
    string Kind,
    string RecordId,
    string WriterId,
    string KeyId,
    string Subject,
    string? Environment,
    DateTimeOffset CreatedUtc,
    DateTimeOffset RecordedUtc,
    T Payload,
    string Algorithm,
    string Signature)
    where T : class;