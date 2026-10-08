// <copyright file="PlatformManifestTestGrouping.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics.CodeAnalysis;

using Xunit;

/// <summary>
/// Runs declaration tests exclusively so a deleted-directory case cannot change the working directory under other tests.
/// </summary>
[CollectionDefinition(nameof(PlatformManifestTestGrouping), DisableParallelization = true)]
[SuppressMessage("Performance", "CA1515:Consider making public types internal", Justification = "xUnit collection definitions must be public.")]
public sealed class PlatformManifestTestGrouping;
