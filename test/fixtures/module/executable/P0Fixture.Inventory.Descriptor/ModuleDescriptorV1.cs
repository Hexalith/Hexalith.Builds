// <copyright file="ModuleDescriptorV1.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith;

/// <summary>
/// Exports the approved <c>hexalith.module-descriptor.v1</c> entrypoint for the p0-inventory fixture module.
/// </summary>
public static class ModuleDescriptorV1
{
    /// <summary>
    /// Describes the module with repository-relative, credential-free metadata only.
    /// </summary>
    /// <returns>The strict descriptor JSON document.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S3400:Methods should not return constants", Justification = "The approved descriptor ABI requires a parameterless Describe method.")]
    public static string Describe() =>
        """
        {"schema":"hexalith.module-descriptor.v1","moduleId":"p0-inventory","domainServiceProject":"test/fixtures/module/executable/P0Fixture.Inventory/P0Fixture.Inventory.csproj","uiAssembly":"artifacts/g4-fixture/bin/P0Fixture.Ui/P0Fixture.Ui.dll","uiMarkerType":"Hexalith.Builds.P0Fixture.Ui.InventoryUiMarker"}
        """;
}