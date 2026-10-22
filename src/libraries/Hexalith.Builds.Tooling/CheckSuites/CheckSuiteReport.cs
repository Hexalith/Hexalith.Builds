// <copyright file="CheckSuiteReport.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CheckSuites;

/// <summary>A deterministic, metadata-only contract validation report.</summary>
/// <param name="Valid">Whether the requested operation satisfies its contract.</param>
/// <param name="Diagnostics">Sorted stable rule identifiers; input values are never reflected.</param>
/// <param name="SuiteDigest">The immutable package identity, when available.</param>
public sealed record CheckSuiteReport(bool Valid, IReadOnlyList<string> Diagnostics, string? SuiteDigest = null);
