// <copyright file="PlatformManifestCommand.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

using Hexalith.Builds.Tooling.Workspace;

/// <summary>Identifies declaration commands and builds their pinned Platform model.</summary>
public static class PlatformManifestCommand
{
    private const int _maximumManifestBytes = 1_048_576;

    /// <summary>Tests the document schema without converting v2 into a legacy manifest.</summary>
    /// <param name="path">The declaration path.</param>
    /// <returns>Whether the document declares v2.</returns>
    public static bool IsV2(string path)
    {
        try
        {
            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!stream.CanSeek || stream.Length > _maximumManifestBytes)
            {
                return false;
            }

            byte[] buffer = new byte[_maximumManifestBytes + 1];
            int count = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (count > _maximumManifestBytes)
            {
                return false;
            }

            int offset = count >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(offset, count - offset));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("schema", out JsonElement schema)
                && schema.ValueKind == JsonValueKind.String
                && schema.GetString() == "hexalith.module-manifest.v2";
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Builds the pinned model without importing nested owner source trees.</summary>
    /// <param name="mapping">The command-resolved mapping.</param>
    /// <param name="workspace">The run-owned output workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="startupDeadlineUtc">The absolute startup deadline, or null for the default bound.</param>
    /// <returns>The built model assembly path.</returns>
    /// <exception cref="InvalidOperationException">The model cannot be built.</exception>
    public static async Task<string> BuildModelAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken, DateTimeOffset? startupDeadlineUtc = null)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspace);
        string root = mapping.PlatformIdentity?.PlatformPath
            ?? mapping.Entries.SingleOrDefault(entry => entry.Identity == "Hexalith.Platform")?.Path
            ?? throw new InvalidOperationException("Platform identity has no composition checkout path.");
        string project = Path.Combine(root, "src", "Hexalith.Platform.Composition", "Hexalith.Platform.Composition.csproj");
        if (!File.Exists(project))
        {
            throw new InvalidOperationException($"Platform model project '{project}' is unavailable.");
        }

        string pinnedCatalog = Path.Combine(workspace, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props");
        if (!File.Exists(pinnedCatalog))
        {
            throw new InvalidOperationException($"Platform model '{project}' cannot use the selected Builds catalog because '{pinnedCatalog}' was not materialized from the Platform identity.");
        }

        string output = Path.Combine(workspace, "hosts", "platform-model");
        List<string> arguments =
        [
            "build", project, "--configuration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release", "--output", output,
            "-p:BuildsToolingAssemblyPath=" + typeof(PlatformManifestCommand).Assembly.Location,
            "-p:NuGetAudit=false", "-p:MinVerVersionOverride=1.0.0", "-nodeReuse:false", "-p:UseSharedCompilation=false",
        ];
        arguments.Add("-p:DirectoryPackagesPropsPath=" + pinnedCatalog);

        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", arguments, root, null),
            CompositionStartupClock.Remaining(startupDeadlineUtc, DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        string assembly = Path.Combine(output, "Hexalith.Platform.Composition.dll");
        return result.ExitCode == 0 && File.Exists(assembly)
            ? assembly
            : throw new InvalidOperationException($"Platform model '{project}' could not build: {BoundedOutput(result.Output, result.ErrorOutput)}");
    }

    /// <summary>Formats process diagnostics without passing log dumps to command output.</summary>
    /// <param name="output">The process standard output.</param>
    /// <param name="errorOutput">The process standard error.</param>
    /// <returns>Bounded process diagnostics.</returns>
    internal static string BoundedOutput(string output, string errorOutput)
    {
        string combined = string.Join("; ", new[] { output, errorOutput }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value =>
            {
                string safe = new([.. value.Select(character => char.IsControl(character) ? ' ' : character)]);
                return (safe.Length > 512 ? safe[^512..] : safe).Trim();
            }));
        return combined.Length > 1024 ? combined[..1024] : combined;
    }

    /// <summary>Uses reference composition when a valid document has no unique active declaration.</summary>
    /// <param name="declarations">The validated module set.</param>
    /// <returns>The unique active identity, or the reference default.</returns>
    internal static string? ActiveModule(IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformModuleDeclaration>? declarations) =>
        declarations is { Count: 1 } ? declarations[0].Declaration.GetProperty("identity").GetProperty("moduleId").GetString() : null;
}
