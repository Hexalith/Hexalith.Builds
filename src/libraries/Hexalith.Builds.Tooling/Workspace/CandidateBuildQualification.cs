// <copyright file="CandidateBuildQualification.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Local-only exact-input qualification; never production acceptance.</summary>
/// <param name="BaselineHead">The freshly observed remote main revision.</param>
/// <param name="OwnedBaselineDigest">Canonical owned byte/mode/gitlink inventory digest.</param>
/// <param name="OwnedProductDigest">The equal product inventory digest.</param>
/// <param name="ProductDiffDigest">The explicitly checked product-only difference inventory.</param>
/// <param name="CiCdRoot">The actual local CiCd candidate checkout.</param>
/// <param name="CiCdCandidate">The committed candidate revision.</param>
/// <param name="CandidateTree">The exact candidate tree.</param>
/// <param name="RenderedConfigPath">The committed rendered configuration path.</param>
/// <param name="RenderedConfigBlob">The exact rendered blob.</param>
/// <param name="ProducerSourceCommit">The released package's actual source provenance.</param>
public sealed record CandidateBuildQualification(
    string BaselineHead,
    string OwnedBaselineDigest,
    string OwnedProductDigest,
    string ProductDiffDigest,
    string CiCdRoot,
    string CiCdCandidate,
    string CandidateTree,
    string RenderedConfigPath,
    string RenderedConfigBlob,
    string ProducerSourceCommit);
