// <copyright file="ToolDiagnosticFormatter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Renders stable, metadata-only command results.
/// </summary>
public static class ToolDiagnosticFormatter
{
    private static readonly JsonSerializerOptions _jsonSerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Writes a command result in the requested output format.
    /// </summary>
    /// <param name="writer">The destination writer.</param>
    /// <param name="result">The result to render.</param>
    /// <param name="format">The output format.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when writing finishes.</returns>
    public static Task WriteAsync(
        TextWriter writer,
        ToolCommandResult result,
        ToolOutputFormat format,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(result);

        string output = format == ToolOutputFormat.Json
            ? JsonSerializer.Serialize(result, _jsonSerializerOptions)
            : FormatHuman(result);
        return writer.WriteLineAsync(output.AsMemory(), cancellationToken);
    }

    private static string FormatHuman(ToolCommandResult result)
    {
        StringBuilder builder = new();
        _ = builder.Append(result.Status).Append(": ").Append(result.Outcome.ExitCode);
        foreach (ToolDiagnostic diagnostic in result.Diagnostics)
        {
            _ = builder.Append('\n');
            AppendDiagnostic(builder, diagnostic);
        }

        if (result.Adoption is not null)
        {
            foreach (Hexalith.Builds.Tooling.Manifest.PlatformAdoptionRow row in result.Adoption)
            {
                _ = builder.Append('\n').Append(row.Passed ? "PASS " : "FAIL ");
                AppendEscaped(builder, row.Requirement);
                AppendField(builder, "file", row.File);
                _ = builder.Append(": ");
                AppendEscaped(builder, row.Reason);
            }

            _ = builder.Append('\n');
            AppendEscaped(builder, result.AdoptionScope ?? string.Empty);
        }

        if (result.Environments is not null)
        {
            foreach (Runtime.CompositionRunListingEntry environment in result.Environments)
            {
                _ = builder.Append('\n').Append("owner=").Append(environment.OwnerRunId);
                AppendField(builder, "environment", environment.EnvironmentId ?? "unavailable");
                AppendField(builder, "attempt", environment.AttemptId ?? "unavailable");
                AppendField(builder, "age-seconds", environment.AgeSeconds?.ToString("F0", CultureInfo.InvariantCulture) ?? "unavailable");
                AppendField(builder, "retention", environment.Retention);
                AppendField(builder, "cleanup", environment.Cleanup);
                AppendField(builder, "failed-runs", string.Join(",", environment.FailureRunIds));
                AppendField(builder, "attachments", environment.AttachmentInformation);
                AppendField(builder, "startup-deadline", environment.StartupDeadlineUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable");
                AppendField(builder, "execution-deadline", environment.ExecutionDeadlineUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "unavailable");
                AppendField(builder, "hold-deadline", "unavailable");
                if (environment.RemainingResources is { } leftovers)
                {
                    AppendField(builder, "leftovers", string.Join(",", leftovers.ResourceIds));
                    if (leftovers.Unverified)
                    {
                        AppendField(builder, "discovery", "unverified");
                        AppendField(builder, "discovery-failures", string.Join(",", leftovers.DiscoveryFailures));
                    }
                }
            }
        }

        if (result.RemainingResources is { } remaining)
        {
            foreach (string id in remaining.ResourceIds)
            {
                _ = builder.Append('\n').Append("leftover=");
                AppendEscaped(builder, id);
            }

            if (remaining.Unverified)
            {
                _ = builder.Append('\n').Append("discovery=unverified");
                AppendField(builder, "failures", string.Join(",", remaining.DiscoveryFailures));
            }
        }

        if (result.BuildPreparation is { } preparation)
        {
            AppendField(builder, "workspace", preparation.Workspace);
            AppendField(builder, "mode", preparation.Mode);
            AppendField(builder, "mapping-hash", preparation.MappingHash);
            foreach ((string name, string value) in preparation.Environment)
            {
                _ = builder.Append('\n');
                AppendField(builder, name, value);
            }
        }

        AppendField(builder, "test-result", result.TestResultPath);
        return builder.ToString();
    }

    private static void AppendDiagnostic(StringBuilder builder, ToolDiagnostic diagnostic)
    {
        AppendEscaped(builder, diagnostic.RuleId);
        _ = builder
            .Append(' ').Append(diagnostic.Phase)
            .Append(' ').Append(diagnostic.Category);
        AppendField(builder, "source", diagnostic.Source);
        AppendField(builder, "row", diagnostic.Row);
        AppendField(builder, "field", diagnostic.Field);
        AppendField(builder, "location", diagnostic.Location);
        _ = builder.Append(": ");
        AppendEscaped(builder, diagnostic.Message);
        if (!string.IsNullOrEmpty(diagnostic.Hint))
        {
            _ = builder.Append(" (");
            AppendEscaped(builder, diagnostic.Hint);
            _ = builder.Append(')');
        }
    }

    private static void AppendEscaped(StringBuilder builder, string value)
    {
        foreach (char character in value)
        {
            _ = character switch
            {
                '\r' => builder.Append("\\r"),
                '\n' => builder.Append("\\n"),
                '\t' => builder.Append("\\t"),
                _ when char.IsControl(character) => builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture)),
                _ => builder.Append(character),
            };
        }
    }

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = builder.Append(' ').Append(name).Append('=');
            AppendEscaped(builder, value);
        }
    }
}
