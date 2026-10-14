// <copyright file="AppHostRunnerTestGrouping.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Diagnostics.CodeAnalysis;

using Xunit;

/// <summary>Isolates runner tests that capture process-wide error output.</summary>
[CollectionDefinition("AppHost validation", DisableParallelization = true)]
[SuppressMessage("Performance", "CA1515:Consider making public types internal", Justification = "xUnit collection definitions must be public.")]
public sealed class AppHostRunnerTestGrouping;
