// <copyright file="CompositionDocumentStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Reads and atomically writes the metadata-only runner documents (plan, readiness, and state).
/// </summary>
public static class CompositionDocumentStore
{
    /// <summary>
    /// Gets the canonical serializer options for runner documents.
    /// </summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Serializes a document to canonical UTF-8 JSON text with a final newline.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="document">The document.</param>
    /// <returns>The canonical JSON text.</returns>
    public static string Serialize<T>(T document) =>
        JsonSerializer.Serialize(document, SerializerOptions).ReplaceLineEndings("\n") + "\n";

    /// <summary>
    /// Writes a document through a temporary file and an atomic rename.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The absolute destination path.</param>
    /// <param name="document">The document.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the document is in place.</returns>
    public static async Task WriteAsync<T>(string path, T document, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        _ = Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, Serialize(document), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>
    /// Reads a document, returning null when it is absent, unreadable, or malformed.
    /// </summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The absolute path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The document, or null.</returns>
    public static async Task<T?> TryReadAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            FileStream stream = File.OpenRead(path);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}