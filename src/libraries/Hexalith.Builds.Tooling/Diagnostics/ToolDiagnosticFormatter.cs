// <copyright file="ToolDiagnosticFormatter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Diagnostics;

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
    public static async Task WriteAsync(
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
        await writer.WriteLineAsync(output.AsMemory(), cancellationToken).ConfigureAwait(false);
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

        return builder.ToString();
    }

    private static void AppendDiagnostic(StringBuilder builder, ToolDiagnostic diagnostic)
    {
        _ = builder.Append(diagnostic.RuleId)
            .Append(' ').Append(diagnostic.Phase)
            .Append(' ').Append(diagnostic.Category);
        AppendField(builder, "source", diagnostic.Source);
        AppendField(builder, "row", diagnostic.Row);
        AppendField(builder, "field", diagnostic.Field);
        AppendField(builder, "location", diagnostic.Location);
        _ = builder.Append(": ").Append(diagnostic.Message);
        if (!string.IsNullOrEmpty(diagnostic.Hint))
        {
            _ = builder.Append(" (").Append(diagnostic.Hint).Append(')');
        }
    }

    private static void AppendField(StringBuilder builder, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            _ = builder.Append(' ').Append(name).Append('=').Append(value);
        }
    }
}