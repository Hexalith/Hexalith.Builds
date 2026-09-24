// <copyright file="CompositionDownResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// The outcome of tearing down one run.
/// </summary>
/// <param name="Result">The stable status, outcome, and diagnostics.</param>
/// <param name="Remaining">The resources still tagged with the run after the bound.</param>
public sealed record CompositionDownResult(ToolCommandResult Result, CompositionRunResources Remaining);