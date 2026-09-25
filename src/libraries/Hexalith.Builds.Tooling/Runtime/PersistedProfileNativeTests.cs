// <copyright file="PersistedProfileNativeTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Declares the product-owned native test project a persisted profile runs against the composed runtime.
/// </summary>
/// <param name="Project">The repository-relative test project path.</param>
/// <param name="Platform">The native test platform: <c>vstest</c>, or <c>mtp</c> for xUnit v3 on Microsoft Testing Platform (it uses <c>--report-xunit-trx</c>).</param>
public sealed record PersistedProfileNativeTests(string Project, string Platform)
{
    /// <summary>The VSTest platform identity.</summary>
    public const string VsTest = "vstest";

    /// <summary>The xUnit v3 Microsoft Testing Platform identity; other MTP frameworks lack <c>--report-xunit-trx</c>.</summary>
    public const string MicrosoftTestingPlatform = "mtp";
}