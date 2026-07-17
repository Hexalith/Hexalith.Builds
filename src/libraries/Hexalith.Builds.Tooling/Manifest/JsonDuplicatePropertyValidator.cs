// <copyright file="JsonDuplicatePropertyValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;

/// <summary>
/// Detects duplicate JSON property names before model binding can discard them.
/// </summary>
public static class JsonDuplicatePropertyValidator
{
    /// <summary>
    /// Finds the first duplicate property name in document order.
    /// </summary>
    /// <param name="element">The JSON element to inspect.</param>
    /// <returns>The duplicate property name, or <see langword="null"/> when none exists.</returns>
    public static string? FindDuplicateProperty(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => FindDuplicatePropertyInObject(element),
        JsonValueKind.Array => FindDuplicatePropertyInArray(element),
        JsonValueKind.String => null,
        JsonValueKind.Number => null,
        JsonValueKind.True => null,
        JsonValueKind.False => null,
        JsonValueKind.Null => null,
        JsonValueKind.Undefined => null,
        _ => null,
    };

    private static string? FindDuplicatePropertyInArray(JsonElement element)
    {
        foreach (JsonElement item in element.EnumerateArray())
        {
            string? duplicate = FindDuplicateProperty(item);
            if (duplicate is not null)
            {
                return duplicate;
            }
        }

        return null;
    }

    private static string? FindDuplicatePropertyInObject(JsonElement element)
    {
        HashSet<string> names = new(StringComparer.Ordinal);

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                return property.Name;
            }

            string? duplicate = FindDuplicateProperty(property.Value);
            if (duplicate is not null)
            {
                return duplicate;
            }
        }

        return null;
    }
}