// <copyright file="PlatformManifestValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;

using Json.Schema;

/// <summary>
/// Validates local Platform enrollment without composing resources or executing lifecycle code.
/// </summary>
public static partial class PlatformManifestValidator
{
    private const int _maximumManifestBytes = 1_048_576;
    private const int _maximumFiles = 256;
    private const string _schemaResource = "Hexalith.Builds.Tooling.Manifest.hexalith.module-manifest.v2.json";
    private static readonly string _schemaText = ReadSchema();
    private static readonly JsonSchema _schema = JsonSchema.FromText(_schemaText, new BuildOptions
    {
        SchemaRegistry = new SchemaRegistry
        {
            Fetch = (_, _) => throw new InvalidOperationException("Platform validation cannot fetch remote schemas."),
        },
    });

    private static readonly JsonElement _schemaRoot = JsonSerializer.Deserialize<JsonElement>(_schemaText);

    /// <summary>
    /// Validates all files as one declaration set using the shipped, embedded schema.
    /// </summary>
    /// <param name="manifestPaths">The manifest file paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>An atomic validation result.</returns>
    public static PlatformManifestValidationResult Validate(
        IEnumerable<string> manifestPaths,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifestPaths);
        List<ToolDiagnostic> diagnostics = [];
        List<PlatformModuleDeclaration> declarations = [];
        Dictionary<string, List<(string Source, string Field)>> identities = new(StringComparer.Ordinal);
        int fileCount = 0;
        foreach (string path in manifestPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++fileCount > _maximumFiles)
            {
                Add(diagnostics, "HXP013", "manifest", "manifest", "Supply at most 256 manifest files in one validation request.");
                break;
            }

            ValidateFile(path, declarations, identities, diagnostics, cancellationToken);
        }

        if (fileCount == 0)
        {
            Add(diagnostics, "HXP015", "manifest", "manifest", "Supply at least one manifest file.");
        }

        foreach (List<(string Source, string Field)> occurrences in identities.Values.Where(value => value.Count > 1))
        {
            foreach ((string source, string field) in occurrences)
            {
                Add(diagnostics, "HXP003", source, field, "The identity is duplicated within the supplied declaration set; use a unique identity.");
            }
        }

        ToolDiagnostic[] ordered = [.. diagnostics.Distinct()
            .OrderBy(diagnostic => diagnostic.Source, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Field, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.RuleId, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)];
        return new PlatformManifestValidationResult(
            ordered.Length == 0 ? declarations.AsReadOnly() : null,
            Array.AsReadOnly(ordered));
    }

    private static void Add(List<ToolDiagnostic> diagnostics, string rule, string source, string field, string reason) =>
        diagnostics.Add(new ToolDiagnostic(rule, ToolPhase.Manifest, ToolFailureCategory.Manifest, reason, field.Length == 0 ? "$" : field, Source: source));

    private static string ReadSchema()
    {
        using Stream stream = typeof(PlatformManifestValidator).Assembly.GetManifestResourceStream(_schemaResource)
            ?? throw new InvalidOperationException("The packaged Platform declaration schema is missing.");
        using StreamReader reader = new(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void ValidateFile(
        string path,
        List<PlatformModuleDeclaration> declarations,
        Dictionary<string, List<(string Source, string Field)>> identities,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string fullPath;
        string source = "manifest";
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            fullPath = Path.GetFullPath(path);
            source = Path.GetRelativePath(Directory.GetCurrentDirectory(), fullPath).Replace('\\', '/');
            if (ContainsProhibitedSecret(source))
            {
                source = "[redacted manifest path]";
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            Add(diagnostics, "HXP004", source, "manifest", "Supply a valid manifest file path.");
            return;
        }

        try
        {
            using FileStream stream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            int initialDiagnosticCount = diagnostics.Count;
            if (stream.Length > _maximumManifestBytes)
            {
                Add(diagnostics, "HXP013", source, "$", "Keep the UTF-8 manifest at or below 1 MiB.");
                return;
            }

            byte[] buffer = new byte[_maximumManifestBytes + 1];
            int count = 0;
            int read;
            while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) > 0)
            {
                count += read;
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (count > _maximumManifestBytes)
            {
                Add(diagnostics, "HXP013", source, "$", "Keep the UTF-8 manifest at or below 1 MiB.");
                return;
            }

            // Reject malformed UTF-8 before JSON parsing; a UTF-8 BOM is permitted.
            int offset = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            string content = new UTF8Encoding(false, true).GetString(buffer, offset, count - offset);
            using JsonDocument document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
            JsonElement root = document.RootElement;
            if (!InspectValues(root, string.Empty, source, diagnostics, cancellationToken))
            {
                return;
            }

            string? schema = StringValue(root, "schema");
            if (!SupportedPlatformManifestSchemas.Eligible.Contains(schema, StringComparer.Ordinal))
            {
                Add(
                    diagnostics,
                    "HXP001",
                    source,
                    "schema",
                    $"Platform enrollment requires major {SupportedPlatformManifestSchemas.CurrentMajor}; eligible schemas: {string.Join(", ", SupportedPlatformManifestSchemas.Eligible)}. Legacy v1 is qualification-only.");
                return;
            }

            EvaluationResults evaluation = _schema.Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.Hierarchical });
            if (!evaluation.IsValid)
            {
                int schemaDiagnosticCount = diagnostics.Count;
                CollectSchemaDiagnostics(evaluation, root, source, diagnostics);
                if (schemaDiagnosticCount == diagnostics.Count)
                {
                    Add(diagnostics, "HXP002", source, "$", "The document violates the Platform declaration schema.");
                }
            }

            int moduleIndex = 0;
            foreach (JsonElement module in ArrayValue(root, "modules"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string prefix = $"modules[{moduleIndex++}]";
                ValidateModule(module, prefix, source, fullPath, identities, diagnostics);
                if (module.ValueKind == JsonValueKind.Object && initialDiagnosticCount == diagnostics.Count)
                {
                    declarations.Add(new PlatformModuleDeclaration(source, ApplyDefaults(module)));
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Add(diagnostics, "HXP005", source, "manifest", "Supply an existing readable manifest file.");
        }
        catch (DecoderFallbackException)
        {
            Add(diagnostics, "HXP011", source, "$", "Supply a valid UTF-8 JSON document.");
        }
        catch (JsonException exception)
        {
            Add(diagnostics, "HXP011", source, exception.Path ?? "$", "Supply valid JSON without comments or trailing commas and with nesting at most 64 levels.");
        }
    }

    private static bool InspectValues(JsonElement value, string path, string source, List<ToolDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool validUnicode = true;
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            int propertyIndex = 0;
            foreach (JsonProperty property in value.EnumerateObject())
            {
                string name;
                try
                {
                    name = property.Name;
                }
                catch (InvalidOperationException)
                {
                    string invalidField = $"{(path.Length == 0 ? "$" : path)}[\"<invalid Unicode property name at index {propertyIndex++}>\"]";
                    Add(diagnostics, "HXP011", source, invalidField, "Supply a JSON property name with valid Unicode surrogate pairs.");
                    validUnicode = false;
                    continue;
                }

                propertyIndex++;
                string field = AppendField(path, name);
                if (!names.Add(name))
                {
                    Add(diagnostics, "HXP012", source, field, "Remove the duplicate JSON property.");
                }

                if (!InspectValues(property.Value, field, source, diagnostics, cancellationToken))
                {
                    validUnicode = false;
                }
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in value.EnumerateArray())
            {
                validUnicode &= InspectValues(item, $"{path}[{index++}]", source, diagnostics, cancellationToken);
            }
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string text;
            try
            {
                text = value.GetString()!;
            }
            catch (InvalidOperationException)
            {
                Add(diagnostics, "HXP011", source, path.Length == 0 ? "$" : path, "Supply a JSON string with valid Unicode surrogate pairs.");
                return false;
            }

            if (ContainsProhibitedSecret(text))
            {
                Add(diagnostics, "HXM007", source, path, "Remove credential material; declare a logical secret reference instead.");
            }
            else if (ManifestPathValidator.ContainsPlaceholder(text))
            {
                Add(diagnostics, "HXM006", source, path, "Resolve the placeholder before enrollment.");
            }
        }

        return validUnicode;
    }

    private static void CollectSchemaDiagnostics(EvaluationResults result, JsonElement root, string source, List<ToolDiagnostic> diagnostics)
    {
        string pointer = result.InstanceLocation.ToString();
        string path = PointerToPath(root, pointer);
        JsonElement instance = ResolvePointer(root, pointer);
        JsonElement schema = ResolveSchemaNode(Uri.UnescapeDataString(result.SchemaLocation.Fragment.TrimStart('#')));
        if (result.Errors is not null)
        {
            foreach (string keyword in result.Errors.Keys)
            {
                if (keyword == "required" && instance.ValueKind == JsonValueKind.Object && schema.ValueKind == JsonValueKind.Object
                    && schema.TryGetProperty("required", out JsonElement required))
                {
                    foreach (JsonElement name in required.EnumerateArray())
                    {
                        if (!instance.TryGetProperty(name.GetString()!, out _))
                        {
                            Add(diagnostics, "HXP015", source, AppendField(path == "$" ? string.Empty : path, name.GetString()!), "Supply the required field.");
                        }
                    }
                }
                else if (keyword == "additionalProperties" && instance.ValueKind == JsonValueKind.Object
                    && schema.ValueKind == JsonValueKind.Object && schema.TryGetProperty("properties", out JsonElement properties))
                {
                    foreach (string property in instance.EnumerateObject().Select(property => property.Name))
                    {
                        if (!properties.TryGetProperty(property, out _))
                        {
                            Add(diagnostics, "HXP002", source, AppendField(path == "$" ? string.Empty : path, property), "Remove the unknown field; JSON property names use strict camelCase.");
                        }
                    }
                }
                else if (keyword is not "$ref" and not "properties" and not "items" and not "allOf" and not "then" and not "if" and not "additionalProperties")
                {
                    string reason = keyword switch
                    {
                        "type" => "Use the JSON type declared by the schema.",
                        "required" => "Supply the required field.",
                        "enum" => "Use one of the values declared by the schema.",
                        "const" => "Use the fixed value declared by the schema.",
                        "pattern" or "minLength" or "maxLength" => "Use a nonblank value matching the schema's declared format and length.",
                        "minimum" or "exclusiveMinimum" or "maximum" => "Use a positive finite value within the schema's declared range.",
                        "uniqueItems" => "Remove duplicate collection entries.",
                        "minItems" or "maxItems" => "Use the collection size declared by the schema.",
                        _ => "The field violates the Platform declaration schema.",
                    };
                    Add(diagnostics, "HXP002", source, path, reason);
                }
            }
        }

        if (result.Details is not null)
        {
            foreach (EvaluationResults detail in result.Details.Where(detail => !detail.IsValid))
            {
                CollectSchemaDiagnostics(detail, root, source, diagnostics);
            }
        }
    }

    private static JsonElement ResolveSchemaNode(string pointer)
    {
        // Conditional applicators can append a synthetic branch index to their
        // schema location. Resolve the nearest real schema object for metadata.
        while (pointer.Length > 0)
        {
            JsonElement schema = ResolvePointer(_schemaRoot, pointer);
            if (schema.ValueKind == JsonValueKind.Object)
            {
                return schema;
            }

            int separator = pointer.LastIndexOf('/');
            pointer = separator > 0 ? pointer[..separator] : string.Empty;
        }

        return _schemaRoot;
    }

    private static JsonElement ResolvePointer(JsonElement root, string pointer)
    {
        JsonElement current = root;
        foreach (string escaped in pointer.Split('/').Skip(1))
        {
            string segment = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(segment, out JsonElement property))
            {
                current = property;
            }
            else if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < current.GetArrayLength())
            {
                current = current[index];
            }
            else
            {
                return default;
            }
        }

        return current;
    }

    private static string PointerToPath(JsonElement root, string pointer)
    {
        string path = string.Empty;
        JsonElement current = root;
        foreach (string escaped in pointer.Split('/').Skip(1))
        {
            string segment = escaped.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, CultureInfo.InvariantCulture, out int index) && index >= 0 && index < current.GetArrayLength())
            {
                path = $"{path}[{index}]";
                current = current[index];
            }
            else
            {
                path = AppendField(path, segment);
                current = ObjectValue(current, segment);
            }
        }

        return path.Length == 0 ? "$" : path;
    }

    private static string AppendField(string path, string name)
    {
        if (name.Length == 0)
        {
            return $"{(path.Length == 0 ? "$" : path)}[\"\"]";
        }

        string safeName = ContainsProhibitedSecret(name) ? "[redacted field]" : name;
        bool simpleName = (char.IsAsciiLetter(safeName[0]) || safeName[0] == '_') && safeName.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
        string dottedPath = path.Length == 0 ? safeName : $"{path}.{safeName}";
        string parentPath = path.Length == 0 ? "$" : path;
        return simpleName ? dottedPath : $"{parentPath}[{JsonSerializer.Serialize(safeName)}]";
    }

    private static bool ContainsProhibitedSecret(string value) =>
        ManifestSecretDetector.ContainsSecret(value)
        || CredentialUriAnywhereRegex().IsMatch(value)
        || (value.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.UserInfo.Length > 0);

    [GeneratedRegex(@"[a-z][a-z0-9+.-]*://[^\s/?#@]+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex CredentialUriAnywhereRegex();

    private static JsonElement ObjectValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out JsonElement value) ? value : default;

    private static string? StringValue(JsonElement element, string property)
    {
        JsonElement value = ObjectValue(element, property);
        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static JsonElement[] ArrayValue(JsonElement element, string property)
    {
        JsonElement value = ObjectValue(element, property);
        return value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray()] : [];
    }

    private static JsonElement ApplyDefaults(JsonElement module)
    {
        JsonObject normalized = JsonNode.Parse(module.GetRawText())!.AsObject();
        if (normalized["runtime"] is JsonObject runtime && runtime["resources"] is JsonArray resources)
        {
            foreach (JsonObject resource in resources.OfType<JsonObject>().Where(resource => !resource.ContainsKey("replicas")))
            {
                resource["replicas"] = 1;
            }
        }

        if (normalized["lifecycle"] is JsonObject lifecycle && lifecycle["startup"] is JsonObject startup && !startup.ContainsKey("defaultTimeoutSeconds"))
        {
            startup["defaultTimeoutSeconds"] = SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds;
        }

        return JsonSerializer.SerializeToElement(normalized);
    }

    private static void RegisterIdentity(string? value, string kind, string source, string field, Dictionary<string, List<(string Source, string Field)>> identities)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        string key = $"{kind}:{value}";
        if (!identities.TryGetValue(key, out List<(string Source, string Field)>? occurrences))
        {
            occurrences = [];
            identities.Add(key, occurrences);
        }

        occurrences.Add((source, field));
    }

    private static void ValidateModule(
        JsonElement module,
        string prefix,
        string source,
        string fullPath,
        Dictionary<string, List<(string Source, string Field)>> identities,
        List<ToolDiagnostic> diagnostics)
    {
        JsonElement identity = ObjectValue(module, "identity");
        RegisterIdentity(StringValue(identity, "moduleId"), "module", source, $"{prefix}.identity.moduleId", identities);
        JsonElement lifecycle = ObjectValue(module, "lifecycle");
        JsonElement[] readiness = [.. ArrayValue(lifecycle, "readiness")];
        HashSet<string> serverIds = new(StringComparer.Ordinal);
        int serverIndex = 0;
        foreach (JsonElement server in ArrayValue(identity, "servers"))
        {
            string serverPath = $"{prefix}.identity.servers[{serverIndex++}]";
            RegisterIdentity(StringValue(server, "appId"), "app", source, $"{serverPath}.appId", identities);
            RegisterIdentity(StringValue(server, "resourceId"), "resource", source, $"{serverPath}.resourceId", identities);
            string? id = StringValue(server, "id");
            if (id is not null && !serverIds.Add(id))
            {
                Add(diagnostics, "HXP003", source, $"{serverPath}.id", "Use a unique server identity within the module.");
            }

            if (ObjectValue(server, "required").ValueKind == JsonValueKind.True)
            {
                if (ObjectValue(server, "enabled").ValueKind == JsonValueKind.False)
                {
                    Add(diagnostics, "HXP020", source, $"{serverPath}.enabled", "A required server must be enabled.");
                }

                if (id is not null && !readiness.Any(probe => StringValue(probe, "server") == id && IsUsableReadiness(probe)))
                {
                    Add(diagnostics, "HXP020", source, $"{prefix}.lifecycle.readiness", $"Declare usable readiness for required server at {serverPath}.id.");
                }
            }
        }

        int interfaceIndex = 0;
        foreach (JsonElement surface in ArrayValue(ObjectValue(module, "surfaces"), "interfaces"))
        {
            if (string.Equals(StringValue(surface, "protocol"), "mcp", StringComparison.OrdinalIgnoreCase))
            {
                Add(diagnostics, "HXP023", source, $"{prefix}.surfaces.interfaces[{interfaceIndex}].protocol", "AD-11 rejects MCP endpoints mapped by an enrolled host, regardless of exposure; MVP MCP uses McpCli stdio.");
            }

            interfaceIndex++;
        }

        JsonElement startupOverride = ObjectValue(ObjectValue(lifecycle, "startup"), "override");
        if (startupOverride.ValueKind == JsonValueKind.Object)
        {
            JsonElement timeout = ObjectValue(startupOverride, "timeoutSeconds");
            if (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetDouble(out double seconds) || !double.IsFinite(seconds) || seconds <= 0)
            {
                Add(diagnostics, "HXP021", source, $"{prefix}.lifecycle.startup.override.timeoutSeconds", "Use a positive finite startup override in seconds.");
            }
        }

        ValidateExecutablePaths(lifecycle, prefix, source, fullPath, diagnostics);
    }

    private static bool IsUsableReadiness(JsonElement probe) => StringValue(probe, "kind") switch
    {
        "http" => StringValue(probe, "endpoint") is { Length: > 0 } endpoint && endpoint.StartsWith('/') && !string.IsNullOrWhiteSpace(endpoint),
        "grpc" => !string.IsNullOrWhiteSpace(StringValue(probe, "service")),
        "command" => !string.IsNullOrWhiteSpace(StringValue(probe, "executable")),
        _ => false,
    };

    private static void ValidateExecutablePaths(JsonElement lifecycle, string prefix, string source, string fullPath, List<ToolDiagnostic> diagnostics)
    {
        foreach (string collection in new[] { "tasks", "readiness" })
        {
            int index = 0;
            foreach (JsonElement item in ArrayValue(lifecycle, collection))
            {
                string field = $"{prefix}.lifecycle.{collection}[{index++}].executable";
                string? executable = StringValue(item, "executable");
                if (executable is not null)
                {
                    List<ToolDiagnostic> pathDiagnostics = [];
                    _ = ManifestPathValidator.ValidateExistingFile(executable, ManifestPathValidator.FindRepositoryRoot(fullPath), field, pathDiagnostics);
                    diagnostics.AddRange(pathDiagnostics.Select(diagnostic => diagnostic with { Source = source }));
                }
            }
        }
    }
}