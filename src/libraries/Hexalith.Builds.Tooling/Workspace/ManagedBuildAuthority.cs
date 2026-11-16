// <copyright file="ManagedBuildAuthority.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Text.Json.Serialization;

/// <summary>Explicit runtime-free catalog authority, separate from Platform provenance.</summary>
/// <param name="Schema">The closed authority contract version.</param>
/// <param name="Kind">Accepted delivery or local candidate qualification.</param>
/// <param name="Repository">The exact public repository identity.</param>
/// <param name="ModuleRoot">The standalone module Git root.</param>
/// <param name="ModuleHead">The frozen committed product revision.</param>
/// <param name="OriginalBuildsGitlink">The module's truthful recorded Builds gitlink.</param>
/// <param name="BuildsTag">The exact active released Builds tag.</param>
/// <param name="BuildsCommit">The active released catalog commit.</param>
/// <param name="BuildsRoot">The actual active Builds checkout.</param>
/// <param name="DeliveredSha">The accepted CiCd revision; absent for candidate qualification.</param>
/// <param name="ConfigurationDigest">The exact prospective or accepted configuration digest.</param>
/// <param name="PerimeterDigest">The accepted perimeter document digest.</param>
/// <param name="PlatformCommit">The actual committed Platform identity.</param>
/// <param name="PlatformCatalogCommit">Platform's unchanged historical catalog pin.</param>
/// <param name="PlatformReleaseTag">The real Platform release tag.</param>
/// <param name="ProducerPin">The exact executing producer version.</param>
/// <param name="Qualification">Additional local-only candidate bindings.</param>
public sealed record ManagedBuildAuthority(
    string Schema,
    string Kind,
    string Repository,
    string ModuleRoot,
    string ModuleHead,
    string OriginalBuildsGitlink,
    string BuildsTag,
    string BuildsCommit,
    string BuildsRoot,
    string? DeliveredSha,
    string ConfigurationDigest,
    string? PerimeterDigest,
    string PlatformCommit,
    string PlatformCatalogCommit,
    string PlatformReleaseTag,
    string ProducerPin,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] CandidateBuildQualification? Qualification = null);
