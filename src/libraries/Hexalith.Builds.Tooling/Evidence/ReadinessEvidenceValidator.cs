// <copyright file="ReadinessEvidenceValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Evidence;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.RunEvidence;

using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

/// <summary>
/// Loads and fail-closed validates <c>hexalith.readiness-evidence.v1</c> YAML documents.
/// </summary>
internal static class ReadinessEvidenceValidator
{
    private const string _schema = "hexalith.readiness-evidence.v1";
    private const string _moduleRunEvidenceSchema = "hexalith.module-run-evidence.v1";

    private static readonly HashSet<string> _rootFields = new(StringComparer.Ordinal)
    {
        "schema",
        "project",
        "generated",
        "generated_by",
        "source_of_truth",
        "markdown_view",
        "sources",
        "validation",
        "status_legend",
        "containment",
        "defaults",
        "rows",
    };

    private static readonly HashSet<string> _defaultFields = new(StringComparer.Ordinal)
    {
        "repository",
        "owner",
        "environment",
        "pinned_revision",
        "status",
        "release_disposition",
    };

    private static readonly HashSet<string> _validationFields = new(StringComparer.Ordinal)
    {
        "command",
        "tool_status",
        "rejects",
        "required_coverage",
    };

    private static readonly HashSet<string> _coverageFields = new(StringComparer.Ordinal)
    {
        "fr",
        "nfr",
        "finding",
        "release",
    };

    private static readonly HashSet<string> _containmentFields = new(StringComparer.Ordinal)
    {
        "overall_readiness",
        "freeze",
        "release_block",
        "open_triggers",
    };

    private static readonly HashSet<string> _rowFields = new(StringComparer.Ordinal)
    {
        "key",
        "category",
        "ids",
        "description",
        "architecture_decisions",
        "user_journeys",
        "primary_story",
        "supporting_stories",
        "entry_gates",
        "fixture",
        "verification_command",
        "evidence_artifact",
        "estimate",
        "status",
        "blocker",
        "notes",
        "owner",
        "priority",
        "binding",
        "linked_findings",
        "repository",
        "environment",
        "pinned_revision",
        "release_disposition",
        "artifact_schema",
        "artifact_sha256",
        "outcome",
        "skip_reason",
        "critical",
    };

    private static readonly HashSet<string> _supportedStatuses = new(StringComparer.Ordinal)
    {
        "pending",
        "blocked",
        "blocked-external",
        "not-verified",
        "failed",
        "passed",
        "skipped",
    };

    private static readonly HashSet<string> _supportedCategories = new(StringComparer.Ordinal)
    {
        "fr",
        "nfr",
        "finding",
        "release",
    };

    private static readonly string[] _requiredEffectiveScalars =
    [
        "repository",
        "owner",
        "environment",
        "pinned_revision",
        "release_disposition",
        "category",
        "description",
        "primary_story",
        "fixture",
        "verification_command",
        "evidence_artifact",
        "estimate",
        "status",
    ];

    /// <summary>
    /// Validates a readiness-evidence YAML file without retaining its raw contents.
    /// </summary>
    /// <param name="evidencePath">The YAML readiness-evidence path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A deterministic command result.</returns>
    public static async Task<ToolCommandResult> ValidateAsync(
        string evidencePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidencePath);

        List<ToolDiagnostic> diagnostics = [];
        ReadinessEvidenceDocument? document = await TryLoadDocumentAsync(evidencePath, diagnostics, cancellationToken).ConfigureAwait(false);
        if (document is null)
        {
            return CreateFailure(diagnostics, ToolFailureCategory.EvidenceSchema);
        }

        ValidateSchema(document, diagnostics);
        if (diagnostics.Count > 0)
        {
            return CreateFailure(diagnostics, ToolFailureCategory.EvidenceSchema);
        }

        await ValidatePolicyAsync(document, diagnostics, cancellationToken).ConfigureAwait(false);
        return diagnostics.Count == 0
            ? new ToolCommandResult("passed", ToolOutcome.Passed(), [])
            : CreateFailure(diagnostics, ToolFailureCategory.EvidencePolicy);
    }

    private static void AddDiagnostic(
        List<ToolDiagnostic> diagnostics,
        ReadinessEvidenceDocument document,
        string ruleId,
        ToolFailureCategory category,
        string field,
        YamlNode? node = null,
        string? row = null)
    {
        diagnostics.Add(new ToolDiagnostic(
            ruleId,
            ToolPhase.Evidence,
            category,
            MessageFor(ruleId),
            field,
            HintFor(ruleId),
            document.Source,
            node is null ? null : ToLocation(node),
            row));
    }

    private static bool ContainsPlaceholder(string value) =>
        value.Contains("{{", StringComparison.Ordinal)
        || value.Contains("}}", StringComparison.Ordinal)
        || value.Contains("${", StringComparison.Ordinal)
        || string.Equals(value, "TBD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "TODO", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "CHANGEME", StringComparison.OrdinalIgnoreCase);

    private static ToolCommandResult CreateFailure(
        IEnumerable<ToolDiagnostic> diagnostics,
        ToolFailureCategory category)
    {
        IReadOnlyList<ToolDiagnostic> orderedDiagnostics = [.. diagnostics
            .OrderBy(diagnostic => diagnostic.Source, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Row, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.RuleId, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Field, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Location, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Hint, StringComparer.Ordinal)];
        ToolDiagnostic firstDiagnostic = orderedDiagnostics.Count == 0
            ? new ToolDiagnostic(
                "HXE999",
                ToolPhase.Evidence,
                category,
                "The readiness evidence could not be validated.",
                "evidence")
            : orderedDiagnostics[0];
        ToolOutcome outcome = ToolOutcome.Passed().Fail(
            ToolPhase.Evidence,
            category,
            firstDiagnostic.RuleId,
            ToolExitCode.EvidenceSchemaOrPolicy);
        return new ToolCommandResult("failed", outcome, orderedDiagnostics);
    }

    private static string? GetEffectiveScalar(
        YamlMappingNode row,
        YamlMappingNode defaults,
        string field) =>
        GetScalar(row, field) ?? GetScalar(defaults, field);

    private static string? GetScalar(YamlMappingNode mapping, string key) =>
        TryGetNode(mapping, key, out YamlNode? node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static bool HasNonEmptySequence(YamlMappingNode row, string field) =>
        TryGetNode(row, field, out YamlNode? node)
        && node is YamlSequenceNode { Children.Count: > 0 };

    private static bool IsCanonicalIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value[0] == '-'
            || value[^1] == '-')
        {
            return false;
        }

        bool previousHyphen = false;
        foreach (char character in value)
        {
            bool isLowercaseLetter = character is >= 'a' and <= 'z';
            bool isDigit = character is >= '0' and <= '9';
            if (!isLowercaseLetter && !isDigit && character != '-')
            {
                return false;
            }

            if (character == '-' && previousHyphen)
            {
                return false;
            }

            previousHyphen = character == '-';
        }

        return true;
    }

    private static bool IsCanonicalRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || Path.IsPathRooted(value)
            || value.Contains('\\', StringComparison.Ordinal)
            || value.StartsWith('/'))
        {
            return false;
        }

        string[] segments = value.Split('/', StringSplitOptions.None);
        return segments.All(segment => !string.IsNullOrWhiteSpace(segment)
            && !string.Equals(segment, ".", StringComparison.Ordinal)
            && !string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXE001" => "The YAML document contains a duplicate mapping key.",
        "HXE002" => "The YAML document is syntactically invalid.",
        "HXE003" => "The YAML document contains an unknown field.",
        "HXE004" => "The readiness-evidence schema is unsupported.",
        "HXE005" => "The YAML document is missing a required schema field.",
        "HXE006" => "The YAML document contains an invalid schema field shape.",
        "HXE007" => "The readiness-evidence path does not resolve to a readable file.",
        "HXE100" => "A required effective row value is missing.",
        "HXE101" => "A row key is missing, invalid, or duplicated.",
        "HXE102" => "A row category is unsupported.",
        "HXE103" => "A row identity or requirement identifier is invalid.",
        "HXE104" => "A required row collection is missing or empty.",
        "HXE108" => "The evidence contains an unresolved placeholder.",
        "HXE109" => "A repository path is not canonical and repository-relative.",
        "HXE123" => "The status vocabulary contains an unsupported status.",
        "HXE124" => "A row status is not declared by the status legend.",
        "HXE130" => "Required readiness coverage is incomplete.",
        "HXE131" => "A required coverage declaration is invalid.",
        "HXE140" => "Passing evidence cannot report an unavailable outcome.",
        "HXE141" => "Critical evidence reports a failed outcome.",
        "HXE142" => "Critical skipped evidence requires an explanation.",
        "HXE143" => "Passing evidence must report a passing outcome.",
        "HXE144" => "Failed evidence must report a failed outcome.",
        "HXE145" => "Executed evidence does not resolve to a readable artifact.",
        "HXE146" => "Executed evidence is missing its schema or SHA-256 metadata.",
        "HXE147" => "The evidence artifact SHA-256 does not match its declared value.",
        "HXE148" => "The evidence artifact schema does not match its declared value.",
        "HXE149" => "Passed readiness must reference a successful module-run artifact.",
        "HXE150" => "The Markdown view does not resolve to a readable file.",
        "HXE151" => "The Markdown view row identities drift from the YAML source of truth.",
        _ => "The readiness evidence is invalid.",
    };

    private static string HintFor(string ruleId) => ruleId switch
    {
        "HXE001" => "Remove duplicate YAML mapping keys before validation.",
        "HXE002" => "Supply syntactically valid YAML.",
        "HXE003" => "Remove fields not declared by hexalith.readiness-evidence.v1.",
        "HXE004" => "Use hexalith.readiness-evidence.v1.",
        "HXE005" => "Supply every required schema field.",
        "HXE006" => "Use the field type required by the readiness-evidence schema.",
        "HXE007" => "Supply an existing readable YAML file.",
        "HXE100" => "Resolve row defaults or provide the required row value.",
        "HXE101" => "Use unique lowercase kebab-case stable row keys.",
        "HXE102" => "Use fr, nfr, finding, or release as the row category.",
        "HXE103" => "Align the stable row key with its canonical requirement identifier.",
        "HXE104" => "Supply the required non-empty row collection.",
        "HXE108" => "Resolve placeholders before recording readiness evidence.",
        "HXE109" => "Use a canonical repository-relative path without traversal.",
        "HXE123" => "Use a supported status with defined fail-closed semantics.",
        "HXE124" => "Declare the row status in status_legend before using it.",
        "HXE130" => "Add rows covering every required requirement and critical category.",
        "HXE131" => "Use a valid coverage range or required row count.",
        "HXE140" => "Record unavailable work as not-verified, never passed.",
        "HXE141" => "Resolve failed critical evidence before release qualification.",
        "HXE142" => "Provide a specific critical skip explanation or execute the evidence.",
        "HXE143" => "Record a passed outcome only for successful execution.",
        "HXE144" => "Record a failed outcome only for failed execution.",
        "HXE145" => "Retain an actual readable artifact for executed evidence.",
        "HXE146" => "Record the artifact schema and SHA-256 for executed evidence.",
        "HXE147" => "Regenerate the artifact hash after producing the evidence.",
        "HXE148" => "Use an artifact whose schema matches the declared evidence schema.",
        "HXE149" => "Reference a completed run-evidence artifact with a successful outcome.",
        "HXE150" => "Supply the configured Markdown view beside the YAML source.",
        "HXE151" => "Regenerate the Markdown view from the YAML row identities.",
        _ => "Correct the readiness evidence and run validation again.",
    };

    private static string? ReadRowKey(YamlMappingNode row) =>
        IsCanonicalIdentifier(GetScalar(row, "key")) ? GetScalar(row, "key") : null;

    private static IReadOnlyList<string> ReadSequence(YamlMappingNode mapping, string field) =>
        TryGetNode(mapping, field, out YamlNode? node) && node is YamlSequenceNode sequence
            ? [.. sequence.Children
                .OfType<YamlScalarNode>()
                .Select(value => value.Value)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!)]
            : [];

    private static bool TryGetNode(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            if (entry.Key is YamlScalarNode { Value: string candidate }
                && string.Equals(candidate, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ToLocation(YamlNode node) => $"{node.Start.Line + 1}:{node.Start.Column + 1}";

    private static async Task<ReadinessEvidenceDocument?> TryLoadDocumentAsync(
        string evidencePath,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(evidencePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXE007",
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor("HXE007"),
                "path",
                HintFor("HXE007")));
            return null;
        }

        if (!File.Exists(fullPath))
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXE007",
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor("HXE007"),
                "path",
                HintFor("HXE007")));
            return null;
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXE007",
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor("HXE007"),
                "path",
                HintFor("HXE007")));
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXE007",
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor("HXE007"),
                "path",
                HintFor("HXE007")));
            return null;
        }

        string repositoryRoot = FindRepositoryRoot(fullPath);
        string source = ToRepositoryRelativePath(fullPath, repositoryRoot);
        try
        {
            IDeserializer duplicateKeyCheckingDeserializer = new DeserializerBuilder()
                .WithDuplicateKeyChecking()
                .Build();
            _ = duplicateKeyCheckingDeserializer.Deserialize<object>(content);
        }
        catch (YamlException exception)
        {
            string ruleId = exception.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase)
                ? "HXE001"
                : "HXE002";
            diagnostics.Add(new ToolDiagnostic(
                ruleId,
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor(ruleId),
                "yaml",
                HintFor(ruleId),
                source,
                $"{exception.Start.Line + 1}:{exception.Start.Column + 1}"));
            return null;
        }

        try
        {
            using StringReader reader = new(content);
            YamlStream stream = [];
            stream.Load(reader);
            if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode root)
            {
                diagnostics.Add(new ToolDiagnostic(
                    "HXE006",
                    ToolPhase.Evidence,
                    ToolFailureCategory.EvidenceSchema,
                    MessageFor("HXE006"),
                    "root",
                    HintFor("HXE006"),
                    source));
                return null;
            }

            return new ReadinessEvidenceDocument(fullPath, repositoryRoot, source, root);
        }
        catch (YamlException exception)
        {
            diagnostics.Add(new ToolDiagnostic(
                "HXE002",
                ToolPhase.Evidence,
                ToolFailureCategory.EvidenceSchema,
                MessageFor("HXE002"),
                "yaml",
                HintFor("HXE002"),
                source,
                $"{exception.Start.Line + 1}:{exception.Start.Column + 1}"));
            return null;
        }
    }

    private static void ValidateMappingFields(
        ReadinessEvidenceDocument document,
        YamlMappingNode mapping,
        IReadOnlySet<string> allowedFields,
        string field,
        List<ToolDiagnostic> diagnostics,
        string? row = null)
    {
        foreach (YamlNode keyNode in mapping.Children.Select(static entry => entry.Key))
        {
            if (keyNode is not YamlScalarNode { Value: string key }
                || !allowedFields.Contains(key))
            {
                AddDiagnostic(
                    diagnostics,
                    document,
                    "HXE003",
                    ToolFailureCategory.EvidenceSchema,
                    field,
                    keyNode,
                    row);
            }
        }
    }

    private static void ValidateNestedMapping(
        ReadinessEvidenceDocument document,
        YamlMappingNode root,
        string field,
        IReadOnlySet<string> allowedFields,
        List<ToolDiagnostic> diagnostics)
    {
        if (!TryGetNode(root, field, out YamlNode? node) || node is not YamlMappingNode mapping)
        {
            AddDiagnostic(diagnostics, document, "HXE005", ToolFailureCategory.EvidenceSchema, field, node);
            return;
        }

        ValidateMappingFields(document, mapping, allowedFields, field, diagnostics);
    }

    private static void ValidatePolicyRow(
        ReadinessEvidenceDocument document,
        YamlMappingNode row,
        YamlMappingNode defaults,
        HashSet<string> declaredStatuses,
        HashSet<string> seenRowKeys,
        List<ToolDiagnostic> diagnostics)
    {
        string? rowKey = ReadRowKey(row);
        if (rowKey is null || !seenRowKeys.Add(rowKey))
        {
            AddDiagnostic(diagnostics, document, "HXE101", ToolFailureCategory.EvidencePolicy, "key", row, rowKey);
        }

        foreach (string field in _requiredEffectiveScalars)
        {
            string? value = GetEffectiveScalar(row, defaults, field);
            if (string.IsNullOrWhiteSpace(value))
            {
                AddDiagnostic(diagnostics, document, "HXE100", ToolFailureCategory.EvidencePolicy, field, row, rowKey);
                continue;
            }

            if (ContainsPlaceholder(value))
            {
                AddDiagnostic(diagnostics, document, "HXE108", ToolFailureCategory.EvidencePolicy, field, row, rowKey);
            }
        }

        if (!HasNonEmptySequence(row, "ids"))
        {
            AddDiagnostic(diagnostics, document, "HXE104", ToolFailureCategory.EvidencePolicy, "ids", row, rowKey);
        }

        if (!TryGetNode(row, "supporting_stories", out YamlNode? supportingStories)
            || supportingStories is not YamlSequenceNode)
        {
            AddDiagnostic(diagnostics, document, "HXE104", ToolFailureCategory.EvidencePolicy, "supporting_stories", row, rowKey);
        }

        if (!HasNonEmptySequence(row, "entry_gates"))
        {
            AddDiagnostic(diagnostics, document, "HXE104", ToolFailureCategory.EvidencePolicy, "entry_gates", row, rowKey);
        }

        string? category = GetScalar(row, "category");
        if (!string.IsNullOrWhiteSpace(category) && !_supportedCategories.Contains(category))
        {
            AddDiagnostic(diagnostics, document, "HXE102", ToolFailureCategory.EvidencePolicy, "category", row, rowKey);
        }

        ValidateRowIdentifiers(document, row, category, rowKey, diagnostics);
        ValidateRowStatus(document, row, defaults, declaredStatuses, rowKey, diagnostics);

        foreach (string pathField in new[] { "fixture", "evidence_artifact" })
        {
            string? path = GetEffectiveScalar(row, defaults, pathField);
            if (!string.IsNullOrWhiteSpace(path) && !IsCanonicalRelativePath(path))
            {
                AddDiagnostic(diagnostics, document, "HXE109", ToolFailureCategory.EvidencePolicy, pathField, row, rowKey);
            }
        }
    }

    private static void ValidateRowIdentifiers(
        ReadinessEvidenceDocument document,
        YamlMappingNode row,
        string? category,
        string? rowKey,
        List<ToolDiagnostic> diagnostics)
    {
        IReadOnlyList<string> identifiers = ReadSequence(row, "ids");
        HashSet<string> uniqueIdentifiers = new(StringComparer.Ordinal);
        if (identifiers.Any(identifier => !uniqueIdentifiers.Add(identifier) || ContainsPlaceholder(identifier)))
        {
            AddDiagnostic(diagnostics, document, "HXE103", ToolFailureCategory.EvidencePolicy, "ids", row, rowKey);
        }

        if (rowKey is null || category is not "fr" and not "nfr")
        {
            return;
        }

        string prefix = category == "fr" ? "fr-" : "nfr-";
        if (!rowKey.StartsWith(prefix, StringComparison.Ordinal)
            || !int.TryParse(rowKey[prefix.Length..], out int number)
            || number <= 0)
        {
            AddDiagnostic(diagnostics, document, "HXE103", ToolFailureCategory.EvidencePolicy, "key", row, rowKey);
            return;
        }

        string requiredIdentifier = category == "fr" ? $"FR-{number}" : $"NFR-{number}";
        if (!identifiers.Contains(requiredIdentifier, StringComparer.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE103", ToolFailureCategory.EvidencePolicy, "ids", row, rowKey);
        }
    }

    private static void ValidateRowStatus(
        ReadinessEvidenceDocument document,
        YamlMappingNode row,
        YamlMappingNode defaults,
        HashSet<string> declaredStatuses,
        string? rowKey,
        List<ToolDiagnostic> diagnostics)
    {
        string? status = GetEffectiveScalar(row, defaults, "status");
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        if (!declaredStatuses.Contains(status))
        {
            AddDiagnostic(diagnostics, document, "HXE124", ToolFailureCategory.EvidencePolicy, "status", row, rowKey);
        }
        else if (!_supportedStatuses.Contains(status))
        {
            AddDiagnostic(diagnostics, document, "HXE123", ToolFailureCategory.EvidencePolicy, "status", row, rowKey);
        }
    }

    private static void ValidateSchema(ReadinessEvidenceDocument document, List<ToolDiagnostic> diagnostics)
    {
        YamlMappingNode root = document.Root;
        ValidateMappingFields(document, root, _rootFields, "root", diagnostics);

        string? schema = GetScalar(root, "schema");
        if (!string.Equals(schema, _schema, StringComparison.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE004", ToolFailureCategory.EvidenceSchema, "schema", root);
        }

        if (string.IsNullOrWhiteSpace(GetScalar(root, "project")))
        {
            AddDiagnostic(diagnostics, document, "HXE005", ToolFailureCategory.EvidenceSchema, "project", root);
        }

        ValidateNestedMapping(document, root, "defaults", _defaultFields, diagnostics);
        ValidateNestedMapping(document, root, "validation", _validationFields, diagnostics);

        if (!TryGetNode(root, "status_legend", out YamlNode? statusLegend)
            || statusLegend is not YamlMappingNode)
        {
            AddDiagnostic(diagnostics, document, "HXE005", ToolFailureCategory.EvidenceSchema, "status_legend", statusLegend);
        }

        if (TryGetNode(root, "containment", out YamlNode? containment)
            && containment is YamlMappingNode containmentMapping)
        {
            ValidateMappingFields(document, containmentMapping, _containmentFields, "containment", diagnostics);
        }
        else if (containment is not null)
        {
            AddDiagnostic(diagnostics, document, "HXE006", ToolFailureCategory.EvidenceSchema, "containment", containment);
        }

        if (TryGetNode(root, "validation", out YamlNode? validationNode)
            && validationNode is YamlMappingNode validation
            && TryGetNode(validation, "required_coverage", out YamlNode? coverageNode))
        {
            if (coverageNode is YamlMappingNode coverage)
            {
                ValidateMappingFields(document, coverage, _coverageFields, "validation.required_coverage", diagnostics);
            }
            else
            {
                AddDiagnostic(diagnostics, document, "HXE006", ToolFailureCategory.EvidenceSchema, "validation.required_coverage", coverageNode);
            }
        }
        else
        {
            AddDiagnostic(diagnostics, document, "HXE005", ToolFailureCategory.EvidenceSchema, "validation.required_coverage", validationNode);
        }

        if (!TryGetNode(root, "rows", out YamlNode? rowsNode) || rowsNode is not YamlSequenceNode rows)
        {
            AddDiagnostic(diagnostics, document, "HXE005", ToolFailureCategory.EvidenceSchema, "rows", rowsNode);
            return;
        }

        foreach (YamlNode rowNode in rows.Children)
        {
            if (rowNode is not YamlMappingNode row)
            {
                AddDiagnostic(diagnostics, document, "HXE006", ToolFailureCategory.EvidenceSchema, "rows", rowNode);
                continue;
            }

            ValidateMappingFields(document, row, _rowFields, "row", diagnostics, ReadRowKey(row));
        }
    }

    private static async Task ValidatePolicyAsync(
        ReadinessEvidenceDocument document,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        YamlMappingNode defaults = (YamlMappingNode)document.Root.Children.First(entry => entry.Key is YamlScalarNode { Value: "defaults" }).Value;
        YamlMappingNode validation = (YamlMappingNode)document.Root.Children.First(entry => entry.Key is YamlScalarNode { Value: "validation" }).Value;
        YamlMappingNode statusLegend = (YamlMappingNode)document.Root.Children.First(entry => entry.Key is YamlScalarNode { Value: "status_legend" }).Value;
        YamlSequenceNode rows = (YamlSequenceNode)document.Root.Children.First(entry => entry.Key is YamlScalarNode { Value: "rows" }).Value;
        YamlMappingNode coverage = (YamlMappingNode)validation.Children.First(entry => entry.Key is YamlScalarNode { Value: "required_coverage" }).Value;

        HashSet<string> declaredStatuses = [];
        foreach (KeyValuePair<YamlNode, YamlNode> entry in statusLegend.Children)
        {
            if (entry.Key is YamlScalarNode { Value: string status }
                && entry.Value is YamlScalarNode { Value: not null })
            {
                _ = declaredStatuses.Add(status);
                if (!_supportedStatuses.Contains(status))
                {
                    AddDiagnostic(diagnostics, document, "HXE123", ToolFailureCategory.EvidencePolicy, "status_legend", entry.Key);
                }
            }
            else
            {
                AddDiagnostic(diagnostics, document, "HXE006", ToolFailureCategory.EvidenceSchema, "status_legend", entry.Key);
            }
        }

        HashSet<string> seenRowKeys = new(StringComparer.Ordinal);
        List<YamlMappingNode> rowMappings = [];
        foreach (YamlNode rowNode in rows.Children)
        {
            if (rowNode is not YamlMappingNode row)
            {
                continue;
            }

            rowMappings.Add(row);
            ValidatePolicyRow(document, row, defaults, declaredStatuses, seenRowKeys, diagnostics);
            await ValidateOutcomeAndArtifactAsync(document, row, defaults, diagnostics, cancellationToken).ConfigureAwait(false);
        }

        ValidateCoverage(document, coverage, rowMappings, diagnostics);
        await ValidateMarkdownViewAsync(document, rowMappings, diagnostics, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ValidateMarkdownViewAsync(
        ReadinessEvidenceDocument document,
        IEnumerable<YamlMappingNode> rows,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string? markdownView = GetScalar(document.Root, "markdown_view");
        if (string.IsNullOrWhiteSpace(markdownView))
        {
            return;
        }

        if (!IsCanonicalRelativePath(markdownView))
        {
            AddDiagnostic(diagnostics, document, "HXE109", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root);
            return;
        }

        string markdownRoot = Path.GetDirectoryName(document.FullPath)!;
        string lexicalMarkdownPath = Path.GetFullPath(Path.Combine(markdownRoot, markdownView));
        if (!File.Exists(lexicalMarkdownPath))
        {
            AddDiagnostic(diagnostics, document, "HXE150", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root);
            return;
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(markdownRoot, markdownView, out string markdownPath))
        {
            AddDiagnostic(diagnostics, document, "HXE109", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root);
            return;
        }

        string[] lines;
        try
        {
            lines = await File.ReadAllLinesAsync(markdownPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            AddDiagnostic(diagnostics, document, "HXE150", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            AddDiagnostic(diagnostics, document, "HXE150", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root);
            return;
        }

        HashSet<string> yamlKeys = rows
            .Select(ReadRowKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> markdownKeys = new(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (!trimmed.StartsWith('|') || !trimmed.EndsWith('|'))
            {
                continue;
            }

            string[] cells = trimmed.Split('|', StringSplitOptions.None);
            if (cells.Length < 3)
            {
                continue;
            }

            string firstCell = cells[1].Trim();
            if (firstCell.Length > 2
                && firstCell[0] == '`'
                && firstCell[^1] == '`'
                && IsCanonicalIdentifier(firstCell[1..^1]))
            {
                _ = markdownKeys.Add(firstCell[1..^1]);
            }
        }

        foreach (string key in yamlKeys.Except(markdownKeys, StringComparer.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE151", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root, key);
        }

        foreach (string key in markdownKeys.Except(yamlKeys, StringComparer.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE151", ToolFailureCategory.EvidencePolicy, "markdown_view", document.Root, key);
        }
    }

    private static async Task ValidateOutcomeAndArtifactAsync(
        ReadinessEvidenceDocument document,
        YamlMappingNode row,
        YamlMappingNode defaults,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        string? rowKey = ReadRowKey(row);
        string? category = GetScalar(row, "category");
        string? status = GetEffectiveScalar(row, defaults, "status");
        string? outcome = GetScalar(row, "outcome");
        bool critical = string.Equals(category, "release", StringComparison.Ordinal)
            || string.Equals(GetScalar(row, "critical"), "true", StringComparison.OrdinalIgnoreCase);

        if (string.Equals(status, "passed", StringComparison.Ordinal))
        {
            if (string.Equals(outcome, "unavailable", StringComparison.Ordinal))
            {
                AddDiagnostic(diagnostics, document, "HXE140", ToolFailureCategory.EvidencePolicy, "outcome", row, rowKey);
            }
            else if (!string.Equals(outcome, "passed", StringComparison.Ordinal))
            {
                AddDiagnostic(diagnostics, document, "HXE143", ToolFailureCategory.EvidencePolicy, "outcome", row, rowKey);
            }
        }
        else if (string.Equals(status, "failed", StringComparison.Ordinal)
            && !string.Equals(outcome, "failed", StringComparison.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE144", ToolFailureCategory.EvidencePolicy, "outcome", row, rowKey);
        }
        else if (string.Equals(outcome, "passed", StringComparison.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE143", ToolFailureCategory.EvidencePolicy, "outcome", row, rowKey);
        }

        if (critical
            && string.Equals(status, "failed", StringComparison.Ordinal))
        {
            AddDiagnostic(diagnostics, document, "HXE141", ToolFailureCategory.EvidencePolicy, "status", row, rowKey);
        }

        if (critical
            && string.Equals(status, "skipped", StringComparison.Ordinal)
            && string.IsNullOrWhiteSpace(GetScalar(row, "skip_reason")))
        {
            AddDiagnostic(diagnostics, document, "HXE142", ToolFailureCategory.EvidencePolicy, "skip_reason", row, rowKey);
        }

        bool executionClaim = string.Equals(outcome, "passed", StringComparison.Ordinal)
            || string.Equals(outcome, "failed", StringComparison.Ordinal);
        if (!executionClaim
            && !string.Equals(status, "passed", StringComparison.Ordinal)
            && !string.Equals(status, "failed", StringComparison.Ordinal))
        {
            return;
        }

        string? artifact = GetEffectiveScalar(row, defaults, "evidence_artifact");
        if (string.IsNullOrWhiteSpace(artifact) || !IsCanonicalRelativePath(artifact))
        {
            return;
        }

        if (!RepositoryPathResolver.TryResolveExistingFile(document.RepositoryRoot, artifact, out string artifactPath))
        {
            AddDiagnostic(diagnostics, document, "HXE145", ToolFailureCategory.EvidencePolicy, "evidence_artifact", row, rowKey);
            return;
        }

        byte[] artifactBytes;
        try
        {
            artifactBytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            AddDiagnostic(diagnostics, document, "HXE145", ToolFailureCategory.EvidencePolicy, "evidence_artifact", row, rowKey);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            AddDiagnostic(diagnostics, document, "HXE145", ToolFailureCategory.EvidencePolicy, "evidence_artifact", row, rowKey);
            return;
        }

        string? artifactSchema = GetScalar(row, "artifact_schema");
        string? artifactHash = GetScalar(row, "artifact_sha256");
        if (string.IsNullOrWhiteSpace(artifactSchema) || string.IsNullOrWhiteSpace(artifactHash))
        {
            AddDiagnostic(diagnostics, document, "HXE146", ToolFailureCategory.EvidencePolicy, "artifact_schema", row, rowKey);
            return;
        }

        string actualHash = Convert.ToHexString(SHA256.HashData(artifactBytes));
        if (!string.Equals(actualHash, artifactHash, StringComparison.OrdinalIgnoreCase))
        {
            AddDiagnostic(diagnostics, document, "HXE147", ToolFailureCategory.EvidencePolicy, "artifact_sha256", row, rowKey);
        }

        if (!string.Equals(artifactSchema, _moduleRunEvidenceSchema, StringComparison.Ordinal)
            || !ModuleRunEvidenceArtifactValidator.TryValidate(artifactBytes, out ModuleRunEvidenceArtifactSummary? artifactSummary))
        {
            AddDiagnostic(diagnostics, document, "HXE148", ToolFailureCategory.EvidencePolicy, "artifact_schema", row, rowKey);
        }
        else if (string.Equals(status, "passed", StringComparison.Ordinal)
            && (!string.Equals(artifactSummary.FinalStatus, "completed", StringComparison.Ordinal)
                || artifactSummary.ExitCode != (int)ToolExitCode.Success))
        {
            AddDiagnostic(diagnostics, document, "HXE149", ToolFailureCategory.EvidencePolicy, "evidence_artifact", row, rowKey);
        }
    }

    private static void ValidateCoverage(
        ReadinessEvidenceDocument document,
        YamlMappingNode coverage,
        IEnumerable<YamlMappingNode> rows,
        List<ToolDiagnostic> diagnostics)
    {
        List<YamlMappingNode> rowList = [.. rows];
        ValidateRangeCoverage(document, coverage, rowList, "fr", "FR", diagnostics);
        ValidateRangeCoverage(document, coverage, rowList, "nfr", "NFR", diagnostics);
        ValidateCountCoverage(document, coverage, rowList, "finding", diagnostics);
        ValidateCountCoverage(document, coverage, rowList, "release", diagnostics);
    }

    private static void ValidateCountCoverage(
        ReadinessEvidenceDocument document,
        YamlMappingNode coverage,
        IEnumerable<YamlMappingNode> rows,
        string category,
        List<ToolDiagnostic> diagnostics)
    {
        string? declaration = GetScalar(coverage, category);
        if (!TryReadFirstPositiveInteger(declaration, out int expected))
        {
            AddDiagnostic(diagnostics, document, "HXE131", ToolFailureCategory.EvidencePolicy, $"validation.required_coverage.{category}", coverage);
            return;
        }

        int actual = rows.Count(row => string.Equals(GetScalar(row, "category"), category, StringComparison.Ordinal));
        if (actual < expected)
        {
            AddDiagnostic(diagnostics, document, "HXE130", ToolFailureCategory.EvidencePolicy, $"validation.required_coverage.{category}", coverage);
        }
    }

    private static void ValidateRangeCoverage(
        ReadinessEvidenceDocument document,
        YamlMappingNode coverage,
        IEnumerable<YamlMappingNode> rows,
        string category,
        string identifierPrefix,
        List<ToolDiagnostic> diagnostics)
    {
        string? declaration = GetScalar(coverage, category);
        if (!TryReadRange(declaration, identifierPrefix, out int first, out int last))
        {
            AddDiagnostic(diagnostics, document, "HXE131", ToolFailureCategory.EvidencePolicy, $"validation.required_coverage.{category}", coverage);
            return;
        }

        HashSet<string> actualIdentifiers = rows
            .Where(row => string.Equals(GetScalar(row, "category"), category, StringComparison.Ordinal))
            .SelectMany(row => ReadSequence(row, "ids"))
            .ToHashSet(StringComparer.Ordinal);
        for (int number = first; number <= last; number++)
        {
            if (!actualIdentifiers.Contains($"{identifierPrefix}-{number}"))
            {
                AddDiagnostic(diagnostics, document, "HXE130", ToolFailureCategory.EvidencePolicy, $"validation.required_coverage.{category}", coverage);
                return;
            }
        }
    }

    private static bool TryReadFirstPositiveInteger(string? value, out int result)
    {
        result = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        int index = 0;
        while (index < value.Length && !char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        int start = index;
        while (index < value.Length && char.IsAsciiDigit(value[index]))
        {
            index++;
        }

        return start < index
            && int.TryParse(value[start..index], out result)
            && result > 0;
    }

    private static bool TryReadRange(string? value, string prefix, out int first, out int last)
    {
        first = 0;
        last = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string expectedPrefix = $"{prefix}-";
        int separatorIndex = value.IndexOf("..", StringComparison.Ordinal);
        if (separatorIndex <= expectedPrefix.Length
            || !value.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !value[(separatorIndex + 2)..].StartsWith(expectedPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string firstPart = value[expectedPrefix.Length..separatorIndex];
        string secondPart = value[(separatorIndex + 2 + expectedPrefix.Length)..];
        return int.TryParse(firstPart, out first)
            && int.TryParse(secondPart, out last)
            && first > 0
            && last >= first;
    }

    private static string FindRepositoryRoot(string fullPath)
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(fullPath)!);
        while (directory is not null)
        {
            string gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetDirectoryName(fullPath)!;
    }

    private static string ToRepositoryRelativePath(string fullPath, string repositoryRoot) =>
        Path.GetRelativePath(repositoryRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');
}
