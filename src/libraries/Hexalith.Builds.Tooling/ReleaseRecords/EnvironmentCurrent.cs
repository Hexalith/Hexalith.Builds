// <copyright file="EnvironmentCurrent.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Captures value-free environment bindings used by one attempt.
/// </summary>
/// <param name="SecretReferences">The secret references, never secret values.</param>
/// <param name="SecretScopes">The secret access scopes.</param>
/// <param name="KeyGeneration">The current key generation.</param>
/// <param name="CatalogGeneration">The current catalog generation.</param>
/// <param name="CatalogRootDigest">The current catalog root digest.</param>
/// <param name="SecretContractDigest">The secret-contract digest.</param>
/// <param name="RealmContractVersion">The applied realm-contract version.</param>
/// <param name="ActiveProfileDigest">The active environment profile digest.</param>
/// <param name="Hostnames">The environment hostnames.</param>
/// <param name="CredentialReferences">The credential references, never credentials.</param>
public sealed record EnvironmentCurrent(
    ImmutableDictionary<string, string> SecretReferences,
    ImmutableDictionary<string, string> SecretScopes,
    long KeyGeneration,
    long CatalogGeneration,
    string CatalogRootDigest,
    string SecretContractDigest,
    string RealmContractVersion,
    string ActiveProfileDigest,
    ImmutableArray<string> Hostnames,
    ImmutableDictionary<string, string> CredentialReferences);