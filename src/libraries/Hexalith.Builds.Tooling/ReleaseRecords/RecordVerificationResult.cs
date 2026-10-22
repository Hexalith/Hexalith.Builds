// <copyright file="RecordVerificationResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Returns either trusted content or a stable, metadata-only failure code.
/// </summary>
/// <typeparam name="T">The expected payload type.</typeparam>
/// <param name="Verified">The verified record, only on success.</param>
/// <param name="FailureCode">A fixed failure code, only on failure.</param>
public sealed record RecordVerificationResult<T>(VerifiedRecord<T>? Verified, string? FailureCode)
    where T : class;