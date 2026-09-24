// <copyright file="CompositionProcessResult.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The bounded result of one runner-owned helper process.
/// </summary>
/// <param name="Started">A value indicating whether the process started.</param>
/// <param name="ExitCode">The exit code, or -1 when the process did not complete.</param>
/// <param name="Output">The bounded standard output text.</param>
/// <param name="TimedOut">A value indicating whether the bound elapsed.</param>
public sealed record CompositionProcessResult(bool Started, int ExitCode, string Output, bool TimedOut);