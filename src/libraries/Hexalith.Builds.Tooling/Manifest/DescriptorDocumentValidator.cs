// <copyright file="DescriptorDocumentValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Parses descriptor JSON without accepting aliases, duplicate keys, or unbound fields.
/// </summary>
internal static class DescriptorDocumentValidator
{
    private static readonly HashSet<string> _moduleFields =
        ["schema", "moduleId", "domainServiceProject", "apiProject", "uiAssembly", "uiMarkerType"];

    private static readonly HashSet<string> _uiFields = ["schema", "uiProject", "moduleUiMarkers"];

    private static readonly HashSet<string> _markerFields = ["moduleId", "uiAssembly", "uiMarkerType"];

    /// <summary>
    /// Validates one module descriptor document.
    /// </summary>
    /// <param name="json">The child result JSON.</param>
    /// <param name="repositoryRoot">The validated checkout root.</param>
    /// <param name="expectedModuleId">The manifest module ID.</param>
    /// <param name="field">The diagnostic field.</param>
    /// <param name="diagnostics">The destination diagnostics.</param>
    /// <returns>A validated module binding, or null.</returns>
    internal static ExecutableModuleDescriptor? ReadModule(
        string json,
        string repositoryRoot,
        string expectedModuleId,
        string field,
        ICollection<ToolDiagnostic> diagnostics)
    {
        using JsonDocument? document = Parse(json, field, diagnostics);
        if (document is null || !HasExactShape(document.RootElement, _moduleFields, ["schema", "moduleId", "domainServiceProject"], field, diagnostics))
        {
            return null;
        }

        JsonElement root = document.RootElement;
        if (ReadString(root, "schema") != "hexalith.module-descriptor.v1"
            || ReadString(root, "moduleId") is not string moduleId)
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Use the supported module descriptor schema and required string fields."));
            return null;
        }

        if (!string.Equals(moduleId, expectedModuleId, StringComparison.Ordinal))
        {
            diagnostics.Add(Diagnostic("HXD004", field, "Return the module ID declared by the manifest."));
            return null;
        }

        string? domainProject = ReadPath(root, "domainServiceProject", ".csproj", repositoryRoot, field, diagnostics);
        string? apiProject = root.TryGetProperty("apiProject", out _)
            ? ReadPath(root, "apiProject", ".csproj", repositoryRoot, field, diagnostics)
            : null;
        string? uiAssembly = root.TryGetProperty("uiAssembly", out _)
            ? ReadPath(root, "uiAssembly", ".dll", repositoryRoot, field, diagnostics)
            : null;
        string? uiMarkerType = root.TryGetProperty("uiMarkerType", out _)
            ? ReadMarkerName(root, "uiMarkerType", field, diagnostics)
            : null;
        if (root.TryGetProperty("uiAssembly", out _) != root.TryGetProperty("uiMarkerType", out _))
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Declare both UI assembly and marker type, or neither."));
        }

        return domainProject is not null && diagnostics.Count == 0
            ? new ExecutableModuleDescriptor(moduleId, domainProject, apiProject, uiAssembly, uiMarkerType)
            : null;
    }

    /// <summary>
    /// Validates one UI descriptor document against the module bindings.
    /// </summary>
    /// <param name="json">The child result JSON.</param>
    /// <param name="repositoryRoot">The validated checkout root.</param>
    /// <param name="modules">The validated module bindings.</param>
    /// <param name="field">The diagnostic field.</param>
    /// <param name="diagnostics">The destination diagnostics.</param>
    /// <returns>A validated UI binding, or null.</returns>
    internal static ExecutableUiDescriptor? ReadUi(
        string json,
        string repositoryRoot,
        IReadOnlyList<ExecutableModuleDescriptor> modules,
        string field,
        ICollection<ToolDiagnostic> diagnostics)
    {
        using JsonDocument? document = Parse(json, field, diagnostics);
        if (document is null || !HasExactShape(document.RootElement, _uiFields, _uiFields, field, diagnostics))
        {
            return null;
        }

        JsonElement root = document.RootElement;
        if (ReadString(root, "schema") != "hexalith.ui-descriptor.v1"
            || root.GetProperty("moduleUiMarkers").ValueKind != JsonValueKind.Array)
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Use the supported UI descriptor schema and marker array."));
            return null;
        }

        string? uiProject = ReadPath(root, "uiProject", ".csproj", repositoryRoot, field, diagnostics);
        Dictionary<string, ExecutableModuleDescriptor> modulesById = modules.ToDictionary(module => module.ModuleId, StringComparer.Ordinal);
        List<ExecutableUiMarker> markers = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonElement marker in root.GetProperty("moduleUiMarkers").EnumerateArray())
        {
            if (!HasExactShape(marker, _markerFields, _markerFields, field, diagnostics))
            {
                continue;
            }

            string? moduleId = ReadString(marker, "moduleId");
            string? assembly = ReadPath(marker, "uiAssembly", ".dll", repositoryRoot, field, diagnostics);
            string? markerType = ReadMarkerName(marker, "uiMarkerType", field, diagnostics);
            if (moduleId is null || !modulesById.TryGetValue(moduleId, out ExecutableModuleDescriptor? declared)
                || !seen.Add(moduleId)
                || assembly is null || markerType is null
                || !string.Equals(assembly, declared.UiAssembly, StringComparison.Ordinal)
                || !string.Equals(markerType, declared.UiMarkerType, StringComparison.Ordinal))
            {
                diagnostics.Add(Diagnostic("HXD005", field, "Bind each declared module UI marker exactly once and match its module descriptor."));
                continue;
            }

            markers.Add(new ExecutableUiMarker(moduleId, assembly, markerType));
        }

        if (markers.Count == 0 || modules.Any(module => module.UiAssembly is not null && !seen.Contains(module.ModuleId)))
        {
            diagnostics.Add(Diagnostic("HXD005", field, "Declare every module UI marker in the UI descriptor."));
        }

        return uiProject is not null && diagnostics.Count == 0
            ? new ExecutableUiDescriptor(uiProject, markers)
            : null;
    }

    private static ToolDiagnostic Diagnostic(string ruleId, string field, string hint)
    {
        string message = ruleId switch
        {
            "HXD004" => "The executable descriptor module identity differs from the manifest.",
            "HXD005" => "The executable UI descriptor marker binding is invalid.",
            _ => "The executable descriptor ABI is invalid.",
        };
        return new ToolDiagnostic(ruleId, ToolPhase.Manifest, ToolFailureCategory.Manifest, message, field, hint);
    }

    private static bool HasExactShape(
        JsonElement element,
        HashSet<string> allowed,
        HashSet<string> required,
        string field,
        ICollection<ToolDiagnostic> diagnostics)
    {
        if (element.ValueKind != JsonValueKind.Object
            || element.EnumerateObject().Any(property => !allowed.Contains(property.Name))
            || required.Any(name => !element.TryGetProperty(name, out _)))
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Remove unknown fields and supply all required descriptor fields."));
            return false;
        }

        return true;
    }

    private static JsonDocument? Parse(string json, string field, ICollection<ToolDiagnostic> diagnostics)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (JsonDuplicatePropertyValidator.FindDuplicateProperty(document.RootElement) is not null)
            {
                document.Dispose();
                diagnostics.Add(Diagnostic("HXD002", field, "Remove duplicate descriptor JSON properties."));
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Return one strict JSON descriptor result."));
            return null;
        }
    }

    private static string? ReadMarkerName(JsonElement element, string name, string field, ICollection<ToolDiagnostic> diagnostics)
    {
        string? value = ReadString(element, name);
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 512
            || ManifestPathValidator.ContainsPlaceholder(value)
            || ManifestSecretDetector.ContainsSecret(value)
            || !value.Contains('.', StringComparison.Ordinal)
            || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '.' or '+')))
        {
            diagnostics.Add(Diagnostic("HXD002", field, "Declare a fully qualified, non-generic CLR marker type."));
            return null;
        }

        return value;
    }

    private static string? ReadPath(
        JsonElement element,
        string name,
        string extension,
        string repositoryRoot,
        string field,
        ICollection<ToolDiagnostic> diagnostics)
    {
        string? value = ReadString(element, name);
        if (value?.EndsWith(extension, StringComparison.OrdinalIgnoreCase) != true)
        {
            diagnostics.Add(Diagnostic("HXD003", field, "Return an existing canonical repository-relative path with the required extension."));
            return null;
        }

        return ManifestPathValidator.ValidateExistingFile(value, repositoryRoot, field, diagnostics);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
