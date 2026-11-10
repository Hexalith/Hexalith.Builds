// <copyright file="ExecutingToolIdentity.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestEnvironment;

using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

/// <summary>The original executor's binary-manifest identity, independent of package provenance or qualification.</summary>
/// <param name="Version">The actual CLI informational version without build metadata.</param>
/// <param name="ContentIdentity">The SHA-256 of the exact UTF-8 manifest bytes.</param>
/// <param name="Manifest">The canonical, path-free manifest retained as classified diagnostics.</param>
public sealed record ExecutingToolIdentity(string Version, string ContentIdentity, string Manifest)
{
    private static readonly string[] _manifestProperties = ["schema", "algorithm", "binaries"];
    private static readonly string[] _binaryProperties = ["name", "contentIdentity"];

    /// <summary>Gets the executing tool name.</summary>
    public static string Name => "hexalith-module";

    /// <summary>Captures the executing CLI and Tooling assemblies before allocating owner resources.</summary>
    /// <returns>The executor's immutable identity.</returns>
    /// <exception cref="IOException">Either executing binary cannot be read or hashed.</exception>
    internal static ExecutingToolIdentity Capture()
    {
        Assembly cli = Assembly.GetEntryAssembly() is { } entry && entry.GetName().Name == "Hexalith.Builds.Module.Cli"
            ? entry
            : Assembly.Load(new AssemblyName("Hexalith.Builds.Module.Cli"));
        return Capture(PlatformIdentityResolver.VersionFor(cli), cli.Location, typeof(ExecutingToolIdentity).Assembly.Location);
    }

    /// <summary>Hashes the exact two binary files with ordinal name ordering and canonical JSON serialization.</summary>
    /// <param name="version">The actual CLI version.</param>
    /// <param name="cliPath">The executing CLI binary.</param>
    /// <param name="toolingPath">The executing Tooling binary.</param>
    /// <returns>The binary-manifest identity; file paths never enter the manifest.</returns>
    /// <exception cref="IOException">A binary is missing, nonregular, empty or unreadable.</exception>
    internal static ExecutingToolIdentity Capture(string version, string cliPath, string toolingPath)
    {
        string manifest = JsonSerializer.Serialize(new
        {
            schema = "hexalith.executing-tool-manifest.v1",
            algorithm = "sha256",
            binaries = new[] { cliPath, toolingPath }.OrderBy(Path.GetFileName, StringComparer.Ordinal).Select(path =>
            {
                using FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path);
                return !stream.CanSeek || stream.Length <= 0
                    ? throw new IOException("The executing binary cannot be hashed.")
                    : new { name = Path.GetFileName(path), contentIdentity = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(stream)) };
            }).ToArray(),
        });
        return new(version, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(manifest))), manifest);
    }

    /// <summary>Projects the original executor without inventing qualification references.</summary>
    /// <returns>The existing v1 tool binding.</returns>
    internal object ToContract() => new
    {
        state = "populated",
        name = Name,
        version = Version,
        contentIdentity = ContentIdentity,
        buildsAdoption = new { state = "not-yet-populated" },
        platformAcceptance = new { state = "not-yet-populated" },
        integrationQualification = new { state = "not-yet-populated" },
    };

    /// <summary>Retains the exact original manifest bytes without rereading the current executor.</summary>
    /// <param name="directory">The classified diagnostic directory.</param>
    /// <param name="cancellationToken">The metadata allowance.</param>
    /// <returns>The internal redacted diagnostic reference.</returns>
    /// <exception cref="InvalidDataException">The recorded manifest no longer matches its identity.</exception>
    internal async Task<object> RetainAsync(string directory, CancellationToken cancellationToken)
    {
        ValidateManifest();
        byte[] bytes = Encoding.UTF8.GetBytes(Manifest);
        string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (ContentIdentity != "sha256:" + hash)
        {
            throw new InvalidDataException("The original executing binary manifest cannot be verified.");
        }

        string reference = "executing-tool-" + hash + ".json";
        await NativeTestExecutor.WriteSnapshotAsync(Path.Combine(directory, reference), directory, bytes, cancellationToken).ConfigureAwait(false);
        return new { id = "executing-tool", classification = "internal", redacted = true, reference, contentIdentity = ContentIdentity };
    }

    private void ValidateManifest()
    {
        try
        {
            if (Manifest is not { Length: > 0 and < 4096 })
            {
                throw new InvalidDataException("The original executing binary manifest cannot be verified.");
            }

            using JsonDocument document = JsonDocument.Parse(Manifest, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.EnumerateObject().Select(property => property.Name).SequenceEqual(_manifestProperties, StringComparer.Ordinal)
                || root.GetProperty("schema").ValueKind != JsonValueKind.String || root.GetProperty("schema").GetString() != "hexalith.executing-tool-manifest.v1"
                || root.GetProperty("algorithm").ValueKind != JsonValueKind.String || root.GetProperty("algorithm").GetString() != "sha256"
                || root.GetProperty("binaries").ValueKind != JsonValueKind.Array || root.GetProperty("binaries").GetArrayLength() != 2)
            {
                throw new InvalidDataException("The original executing binary manifest cannot be verified.");
            }

            string[] names = ["Hexalith.Builds.Module.Cli.dll", "Hexalith.Builds.Tooling.dll"];
            List<object> binaries = [];
            for (int index = 0; index < names.Length; index++)
            {
                JsonElement binary = root.GetProperty("binaries")[index];
                if (binary.ValueKind != JsonValueKind.Object || !binary.EnumerateObject().Select(property => property.Name).SequenceEqual(_binaryProperties, StringComparer.Ordinal)
                    || binary.GetProperty("name").ValueKind != JsonValueKind.String || binary.GetProperty("name").GetString() != names[index]
                    || binary.GetProperty("contentIdentity").ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("The original executing binary manifest cannot be verified.");
                }

                string digest = binary.GetProperty("contentIdentity").GetString()!;
                if (digest.Length != 71 || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Skip(7).Any(character => !char.IsAsciiDigit(character) && character is not (>= 'a' and <= 'f')))
                {
                    throw new InvalidDataException("The original executing binary manifest cannot be verified.");
                }

                binaries.Add(new { name = names[index], contentIdentity = digest });
            }

            string canonical = JsonSerializer.Serialize(new { schema = "hexalith.executing-tool-manifest.v1", algorithm = "sha256", binaries });
            if (!string.Equals(Manifest, canonical, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The original executing binary manifest cannot be verified.");
            }
        }
        catch (JsonException)
        {
            throw new InvalidDataException("The original executing binary manifest cannot be verified.");
        }
    }
}
