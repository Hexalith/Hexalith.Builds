// <copyright file="SelectedMetadataSafety.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>Shares the existing credential-free selected metadata policy across held snapshots.</summary>
internal static class SelectedMetadataSafety
{
    /// <summary>Checks every object key and string value before public diagnostic retention.</summary>
    /// <param name="value">The parsed metadata.</param>
    /// <returns>Whether the full metadata tree excludes prohibited credential material.</returns>
    internal static bool IsSafe(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => !ManifestSecretDetector.ContainsSecret(value.GetString()!),
        JsonValueKind.Array => value.EnumerateArray().All(IsSafe),
        JsonValueKind.Object => value.EnumerateObject().All(property => !ManifestSecretDetector.ContainsSecret(property.Name) && IsSafe(property.Value)),
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined => true,
        _ => false,
    };
}
