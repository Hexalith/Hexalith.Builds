// <copyright file="NativeBuildReceipt.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Records a package-mode native producer's immutable inputs and output inventory.</summary>
/// <param name="Schema">The receipt format.</param>
/// <param name="Configuration">The audited build configuration.</param>
/// <param name="Source">Source, platform, SDK and tool identities required at reuse.</param>
/// <param name="Profiles">The covered package profiles and their project/executor identities.</param>
/// <param name="Components">All prepared runnable outputs.</param>
internal sealed record NativeBuildReceipt(
    string Schema,
    string Configuration,
    IReadOnlyDictionary<string, string> Source,
    IReadOnlyDictionary<string, string> Profiles,
    IReadOnlyDictionary<string, NativeBuildComponent> Components);
