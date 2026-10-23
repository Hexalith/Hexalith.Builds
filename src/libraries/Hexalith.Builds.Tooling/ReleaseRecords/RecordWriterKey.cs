// <copyright file="RecordWriterKey.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Declares an independently supplied public trust anchor and explicit writer scopes.
/// </summary>
/// <param name="WriterId">The enrolled writer identity.</param>
/// <param name="KeyId">The enrolled key identifier.</param>
/// <param name="PublicKeyPem">The P-256 public key in SubjectPublicKeyInfo PEM format.</param>
/// <param name="Roles">The explicit publication, executor, recovery-executor, monitor, administrator or deputy roles.</param>
/// <param name="Kinds">The explicitly authorized record kinds.</param>
/// <param name="Environments">The explicit environment scopes; publication uses an empty set.</param>
/// <param name="ExclusiveAdministratorCustody">The enrollment assertion that no non-Administrator holds this identity or key.</param>
/// <param name="Revoked">Whether the anchor has been revoked.</param>
public sealed record RecordWriterKey(
    string WriterId,
    string KeyId,
    string PublicKeyPem,
    ImmutableHashSet<string> Roles,
    ImmutableHashSet<string> Kinds,
    ImmutableHashSet<string> Environments,
    bool ExclusiveAdministratorCustody,
    bool Revoked);