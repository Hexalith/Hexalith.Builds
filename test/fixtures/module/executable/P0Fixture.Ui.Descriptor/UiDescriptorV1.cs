// <copyright file="UiDescriptorV1.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith;

/// <summary>
/// Exports the approved <c>hexalith.ui-descriptor.v1</c> entrypoint for the P0 executable fixture UI.
/// </summary>
public static class UiDescriptorV1
{
    /// <summary>
    /// Describes the UI marker bindings with repository-relative, credential-free metadata only.
    /// </summary>
    /// <returns>The strict descriptor JSON document.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3400:Methods should not return constants", Justification = "The approved descriptor ABI requires a parameterless Describe method.")]
    public static string Describe() =>
        """
        {"schema":"hexalith.ui-descriptor.v1","uiProject":"test/fixtures/module/executable/P0Fixture.Ui/P0Fixture.Ui.csproj","moduleUiMarkers":[{"moduleId":"p0-orders","uiAssembly":"artifacts/g4-fixture/bin/P0Fixture.Ui/P0Fixture.Ui.dll","uiMarkerType":"Hexalith.Builds.P0Fixture.Ui.OrdersUiMarker"},{"moduleId":"p0-inventory","uiAssembly":"artifacts/g4-fixture/bin/P0Fixture.Ui/P0Fixture.Ui.dll","uiMarkerType":"Hexalith.Builds.P0Fixture.Ui.InventoryUiMarker"}]}
        """;
}