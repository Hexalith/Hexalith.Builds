// <copyright file="CompositionEndpointCredentials.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text;
using System.Text.Json;

/// <summary>Inspects the fixed private channel before selected execution without retaining its values.</summary>
internal static class CompositionEndpointCredentials
{
    /// <summary>Captures exact endpoint secrets for the existing native-output inspection policy.</summary>
    /// <param name="plan">The owning run and isolation bindings.</param>
    /// <param name="readiness">The actual prepared endpoint observations.</param>
    /// <param name="token">The original execution bound.</param>
    /// <returns>The exact handed-off values, or null on any unsafe or inconsistent channel.</returns>
    internal static async Task<IReadOnlyList<string>?> ReadSecretsAsync(CompositionRunPlan plan, CompositionReadiness readiness, CancellationToken token)
    {
        if (readiness.NativeEndpointCredentialsPath is not { } path)
        {
            return readiness.NativeHttpEndpoints.Count == 0 ? [] : null;
        }

        try
        {
            string expected = Path.Combine(Path.GetFullPath(plan.Workspace), NativeTestHandoff.EndpointCredentialsFileName);
            if (path != expected || !SelectedNativeTestReport.IsOwnedRegularPath(path, plan.Workspace))
            {
                return null;
            }

            FileStream stream = SelectedNativeTestReport.OpenWithoutBlocking(path, respectSharing: true);
            await using (stream.ConfigureAwait(false))
            {
                if (!stream.CanSeek || stream.Length is <= 0 or > 65536)
                {
                    return null;
                }

                if (!OperatingSystem.IsWindows())
                {
                    UnixFileMode mode = File.GetUnixFileMode(stream.SafeFileHandle);
                    if ((mode & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0 || (mode & UnixFileMode.UserRead) == 0)
                    {
                        return null;
                    }
                }

                MemoryStream bytes = new();
                await using (bytes.ConfigureAwait(false))
                {
                    byte[] buffer = new byte[8192];
                    int count;
                    while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                    {
                        if (bytes.Length + count > 65536)
                        {
                            return null;
                        }

                        await bytes.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                    }

                    using JsonDocument document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(bytes.ToArray()), new JsonDocumentOptions { MaxDepth = 8 });
                    JsonElement root = document.RootElement;
                    Properties(root, "schema", "runId", "syntheticTenantId", "dataIsolationId", "endpoints");
                    if (root.GetProperty("schema").GetString() != NativeTestHandoff.EndpointCredentialsSchema
                        || root.GetProperty("runId").GetString() != plan.RunId
                        || root.GetProperty("syntheticTenantId").GetString() != plan.TenantNamespace
                        || root.GetProperty("dataIsolationId").GetString() != plan.ResourceNamespace)
                    {
                        return null;
                    }

                    JsonElement entries = root.GetProperty("endpoints");
                    if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 1 or > 32
                        || entries.GetArrayLength() != readiness.NativeHttpEndpoints.Count)
                    {
                        return null;
                    }

                    HashSet<(string Resource, string Module, string Endpoint)> bindings = [];
                    List<string> secrets = [];
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        Properties(entry, "resourceId", "moduleId", "endpointName", "uri", "headerName", "value");
                        string resource = entry.GetProperty("resourceId").GetString()!;
                        string module = entry.GetProperty("moduleId").GetString()!;
                        string endpoint = entry.GetProperty("endpointName").GetString()!;
                        CompositionResourceReadiness? owner = readiness.Resources.SingleOrDefault(candidate => candidate.Name == resource && candidate.ModuleId == module);
                        CompositionResourceEndpoint? binding = owner?.Endpoints.SingleOrDefault(candidate => candidate.Name == endpoint);
                        CompositionNativeHttpEndpoint? declared = readiness.NativeHttpEndpoints.SingleOrDefault(candidate => candidate.ResourceId == resource && candidate.ModuleId == module && candidate.EndpointName == endpoint);
                        string? value = entry.GetProperty("value").GetString();
                        if (binding is null || declared is null || declared.Uri != binding.Uri || binding.Uri.AbsoluteUri != entry.GetProperty("uri").GetString()
                            || !CompositionNativeHttpEndpoints.IsInvocationUri(binding.Uri)
                            || entry.GetProperty("headerName").GetString() != "dapr-api-token"
                            || string.IsNullOrWhiteSpace(value) || value.Length > 8192
                            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.' or '_' or '~' or '+' or '/' or '='))
                            || !bindings.Add((resource, module, endpoint)))
                        {
                            return null;
                        }

                        secrets.Add(value);
                    }

                    return secrets;
                }
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException
            or InvalidOperationException or NotSupportedException or JsonException or DecoderFallbackException or KeyNotFoundException)
        {
            return null;
        }
    }

    private static void Properties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException();
        }

        string[] actual = [.. value.EnumerateObject().Select(property => property.Name)];
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length
            || actual.Any(name => !names.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException();
        }
    }
}
