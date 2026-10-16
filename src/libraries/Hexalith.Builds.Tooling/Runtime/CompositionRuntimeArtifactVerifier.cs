// <copyright file="CompositionRuntimeArtifactVerifier.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

/// <summary>Links an executed native runtime to its approved OCI image without contacting a registry or starting a container.</summary>
public static class CompositionRuntimeArtifactVerifier
{
    /// <summary>Verifies every retained layer and the final daprd executable against the immutable image and native binary.</summary>
    /// <param name="archive">A retained OCI transport archive.</param>
    /// <param name="imageDigest">The approved OCI index digest without its prefix.</param>
    /// <param name="manifestDigest">The approved platform manifest digest without its prefix.</param>
    /// <param name="binary">The native runtime that the sidecar will execute.</param>
    /// <returns>False for missing, substituted, incomplete or unsupported artifacts.</returns>
    public static bool Verify(string archive, string imageDigest, string manifestDigest, string binary)
    {
        try
        {
            const int maximumBytes = 512 * 1024 * 1024;
            FileInfo file = new(archive);
            if (!file.Exists || file.LinkTarget is not null || file.Length > maximumBytes || !File.Exists(binary))
            {
                return false;
            }

            Dictionary<string, byte[]> blobs = new(StringComparer.Ordinal);
            long total = 0;
            using FileStream transport = File.OpenRead(archive);
            using TarReader tar = new(transport);
            while (tar.GetNextEntry() is { } entry)
            {
                if (!entry.Name.StartsWith("blobs/sha256/", StringComparison.Ordinal) || entry.DataStream is null)
                {
                    continue;
                }

                string digest = entry.Name["blobs/sha256/".Length..];
                if (digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) || entry.Length < 0 || (total += entry.Length) > maximumBytes || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                {
                    return false;
                }

                using MemoryStream data = new();
                entry.DataStream.CopyTo(data);
                byte[] bytes = data.ToArray();
                if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest || !blobs.TryAdd(digest, bytes))
                {
                    return false;
                }
            }

            if (!blobs.TryGetValue(imageDigest, out byte[]? indexBytes) || !blobs.TryGetValue(manifestDigest, out byte[]? manifestBytes))
            {
                return false;
            }

            using JsonDocument index = JsonDocument.Parse(indexBytes);
            if (!index.RootElement.GetProperty("manifests").EnumerateArray().Any(manifest => manifest.GetProperty("digest").GetString() == "sha256:" + manifestDigest
                && manifest.GetProperty("platform").GetProperty("os").GetString() == "linux" && manifest.GetProperty("platform").GetProperty("architecture").GetString() == "amd64"))
            {
                return false;
            }

            using JsonDocument image = JsonDocument.Parse(manifestBytes);
            string? executableHash = null;
            foreach (JsonElement layer in image.RootElement.GetProperty("layers").EnumerateArray())
            {
                string digest = layer.GetProperty("digest").GetString()!["sha256:".Length..];
                if (!blobs.TryGetValue(digest, out byte[]? layerBytes))
                {
                    return false;
                }

                using MemoryStream compressed = new(layerBytes, writable: false);
                using Stream payload = layerBytes is [0x1f, 0x8b, ..] ? new GZipStream(compressed, CompressionMode.Decompress) : compressed;
                using TarReader contents = new(payload);
                long expanded = 0;
                while (contents.GetNextEntry() is { } entry)
                {
                    if (entry.Length < 0 || (expanded += entry.Length) > maximumBytes)
                    {
                        return false;
                    }

                    string name = entry.Name.StartsWith("./", StringComparison.Ordinal) ? entry.Name[2..] : entry.Name.TrimStart('/');
                    if (name == ".wh.daprd")
                    {
                        executableHash = null;
                    }
                    else if (name == "daprd")
                    {
                        if (entry.DataStream is null || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        {
                            return false;
                        }

                        executableHash = Convert.ToHexStringLower(SHA256.HashData(entry.DataStream));
                    }
                }
            }

            using FileStream native = File.OpenRead(binary);
            return executableHash is not null && executableHash == Convert.ToHexStringLower(SHA256.HashData(native));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }
}
