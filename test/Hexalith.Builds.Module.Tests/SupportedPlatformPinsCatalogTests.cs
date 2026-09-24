// <copyright file="SupportedPlatformPinsCatalogTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Xml.Linq;

using Hexalith.Builds.Tooling.Manifest;

using Shouldly;

using Xunit;

/// <summary>
/// Ties the runner platform pins to the owning package catalog.
/// </summary>
public sealed class SupportedPlatformPinsCatalogTests
{
    /// <summary>
    /// Verifies the catalog default EventStore and FrontComposer versions equal the runner pins.
    /// </summary>
    [Fact]
    public void CatalogDefaultsMatchSupportedPlatformPins()
    {
        XDocument catalog = XDocument.Load(Path.Combine(CompositionTestFiles.RepositoryRoot(), "Props", "Directory.Packages.props"));

        CatalogDefault(catalog, "HexalithEventStoreVersion").ShouldBe(SupportedPlatformPins.EventStoreVersion);
        CatalogDefault(catalog, "HexalithFrontComposerVersion").ShouldBe(SupportedPlatformPins.FrontComposerVersion);
    }

    private static string CatalogDefault(XDocument catalog, string property) =>
        catalog.Descendants()
            .Single(element => element.Name.LocalName == property)
            .Value
            .Trim();
}