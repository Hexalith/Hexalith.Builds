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
    public static Task WriteAsync<T>(string path, T document, CancellationToken cancellationToken)
        => WriteBytesAsync(path, new UTF8Encoding(false).GetBytes(Serialize(document)), cancellationToken);

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

            FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path);
            await using (stream.ConfigureAwait(false))
            {
                return !stream.CanSeek || stream.Length > 4 * 1024 * 1024
                    ? null
                    : await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Reads durable metadata without treating an unreadable existing document as absent.</summary>
    /// <typeparam name="T">The document type.</typeparam>
    /// <param name="path">The absolute document path.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The document, or null only when no document exists.</returns>
    /// <exception cref="IOException">The existing document cannot be verified.</exception>
    internal static async Task<T?> ReadIfExistsAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                throw new IOException("Runner metadata cannot be verified.");
            }

            FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path, respectSharing: true);
            await using (stream.ConfigureAwait(false))
            {
                return !stream.CanSeek || stream.Length > 4 * 1024 * 1024
                    ? throw new IOException("Runner metadata cannot be verified.")
                    : (await JsonSerializer.DeserializeAsync<T>(stream, SerializerOptions, cancellationToken).ConfigureAwait(false)
                        ?? throw new IOException("Runner metadata cannot be verified."));
            }
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new IOException("Runner metadata cannot be verified.", exception);
        }
    }

    /// <summary>Publishes complete artifact bytes using a private temporary file and atomic rename.</summary>
    /// <param name="path">The destination.</param>
    /// <param name="bytes">The inspected bytes.</param>
    /// <param name="cancellationToken">The existing publication allowance.</param>
    /// <returns>The atomic publication task.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1849:Call async methods when in an async method", Justification = "Durable publication requires Flush(true); FlushAsync does not flush the device cache.")]
    internal static async Task WriteBytesAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        CompositionWorkspace.CreatePrivateDirectory(directory);
        string temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }

            FileStream stream = new(temporary, options);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                CompositionDurableFlush.Flush(stream, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
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
}