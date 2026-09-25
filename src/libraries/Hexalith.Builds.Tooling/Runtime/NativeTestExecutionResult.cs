// <copyright file="NativeTestExecutionResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.TestReports;

/// <summary>
/// The outcome of one native test step.
/// </summary>
/// <param name="Result">The fail-closed command result.</param>
/// <param name="Report">The passing native report summary, or null.</param>
/// <param name="ReportBytes">The metadata-checked native report bytes to retain, or null.</param>
internal sealed record NativeTestExecutionResult(ToolCommandResult Result, NativeTestReport? Report, byte[]? ReportBytes);