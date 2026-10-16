// <copyright file="ImmutableArrayJsonConverter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Serializes an <see cref="ImmutableArray{T}"/> as a JSON array so a round trip keeps value equality.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
public sealed class ImmutableArrayJsonConverter<T> : JsonConverter<ImmutableArray<T>>
{
    /// <inheritdoc/>
    public override ImmutableArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected a JSON array.");
        }

        List<T> values = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            values.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);
        }

        return values.Count == 0 ? [] : [.. values];
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ImmutableArray<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        writer.WriteStartArray();
        foreach (T item in value)
        {
            JsonSerializer.Serialize(writer, item, options);
        }

        writer.WriteEndArray();
    }
}
