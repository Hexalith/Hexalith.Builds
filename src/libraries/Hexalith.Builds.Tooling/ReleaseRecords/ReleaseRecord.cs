// <copyright file="ReleaseRecord.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

using System.Collections.Immutable;

/// <summary>
/// Binds the environment-invariant release inputs and their evidence.
/// </summary>
/// <param name="ReleaseId">The immutable release identity.</param>
/// <param name="ApplicationPackageDigest">The retained OCI Helm package digest.</param>
/// <param name="ModuleArtifactDigests">The module package and image content identities.</param>
/// <param name="ComposedHostDigest">The composed host image digest.</param>
/// <param name="CatalogRouteContentDigest">The release routing content digest.</param>
/// <param name="McpCliPackageDigest">The stable McpCli package hash.</param>
/// <param name="ProfileTemplateDigest">The ratified profile template digest.</param>
/// <param name="DaprSidecarPatch">The pinned Dapr sidecar patch.</param>
/// <param name="RealmContractVersion">The realm contract version.</param>
/// <param name="RecoveryHookContractVersion">The recovery hook contract version.</param>
/// <param name="CheckSuiteDigests">The check-suite content identities.</param>
/// <param name="QualifiedEnvironmentLayer">The initially qualified environment-layer set.</param>
/// <param name="QualifiedSharedInfrastructure">The initially qualified shared-infrastructure set.</param>
/// <param name="ModuleChangeClassifications">The per-module change classifications.</param>
/// <param name="EvidenceDigests">The named evidence content identities.</param>
public sealed record ReleaseRecord(
    string ReleaseId,
    string ApplicationPackageDigest,
    ImmutableDictionary<string, string> ModuleArtifactDigests,
    string ComposedHostDigest,
    string CatalogRouteContentDigest,
    string McpCliPackageDigest,
    string ProfileTemplateDigest,
    string DaprSidecarPatch,
    string RealmContractVersion,
    string RecoveryHookContractVersion,
    ImmutableDictionary<string, string> CheckSuiteDigests,
    ImmutableArray<string> QualifiedEnvironmentLayer,
    ImmutableArray<string> QualifiedSharedInfrastructure,
    ImmutableDictionary<string, string> ModuleChangeClassifications,
    ImmutableDictionary<string, string> EvidenceDigests);