// <copyright file="BuildPreparationService.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Prepares producer-owned MSBuild imports without composing or launching resources.</summary>
public static class BuildPreparationService
{
    /// <summary>Validates a declaration, resolves its workspace and materializes build imports.</summary>
    /// <param name="manifestPath">The declaration path.</param>
    /// <param name="mode">The explicit tool-selected mode.</param>
    /// <param name="workspace">A nonexistent output directory beneath an existing real parent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The structured preparation outcome and verified environment on success.</returns>
    public static async Task<ToolCommandResult> PrepareAsync(string manifestPath, WorkspaceMode mode, string workspace, CancellationToken cancellationToken)
    {
        string? owned = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(manifestPath) || string.IsNullOrWhiteSpace(workspace))
            {
                return Failure("HXC001", "A manifest and a fresh workspace directory are required.", ToolExitCode.UsageOrManifest);
            }

            string output = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
            ValidateOutput(output);
            PlatformManifestValidationResult validation = PlatformManifestValidator.Validate([manifestPath], cancellationToken);
            if (!validation.IsValid)
            {
                ToolDiagnostic first = validation.Diagnostics[0];
                return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(first.Phase, first.Category, first.RuleId, ToolExitCode.UsageOrManifest), validation.Diagnostics);
            }

            SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifestPath, mode, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ValidateOutput(output);
            string temporary = Path.Combine(Path.GetDirectoryName(output)!, ".hexalith-build-" + Guid.NewGuid().ToString("N"));
            CompositionWorkspace.CreatePrivateDirectory(temporary);
            try
            {
                // Rename claims a nonexistent destination atomically, including against concurrent preparers.
                Directory.Move(temporary, output);
                owned = output;
            }
            finally
            {
                if (Directory.Exists(temporary))
                {
                    Directory.Delete(temporary);
                }
            }

            await SourceMappingMaterializer.WriteAsync(mapping, output, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            ToolCommandResult result = new("prepared", ToolOutcome.Passed(), PlatformIdentityResolver.Diagnostics(mapping.IdentityFindings, warning: true))
            {
                BuildPreparation = new BuildPreparation(output, mode == WorkspaceMode.Package ? "package" : "source", mapping.ContentHash, SourceMappingMaterializer.Environment(mapping, output)),
            };
            owned = null;
            return result;
        }
        catch (OperationCanceledException)
        {
            return Failure("HXC130", "Build preparation was cancelled.", ToolExitCode.Cancelled);
        }
        catch (WorkspaceMappingException exception)
        {
            ToolDiagnostic first = exception.Diagnostics[0];
            return new ToolCommandResult("failed", ToolOutcome.Passed().Fail(first.Phase, first.Category, first.RuleId, ToolExitCode.PrerequisiteUnavailable), exception.Diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Failure("HXW007", "Build output must be fresh, private and free of symbolic-link traversal: " + exception.Message, ToolExitCode.PrerequisiteUnavailable);
        }
        finally
        {
            if (owned is not null)
            {
                _ = await CompositionWorkspace.TryDeleteAsync(owned, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static void ValidateOutput(string output)
    {
        if (Path.Exists(output) || new DirectoryInfo(output).LinkTarget is not null)
        {
            throw new IOException("The output directory is already occupied.");
        }

        DirectoryInfo? parent = Directory.GetParent(output);
        if (parent?.Exists != true)
        {
            throw new IOException("The output parent directory must already exist.");
        }

        for (DirectoryInfo? current = parent; current is not null; current = current.Parent)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new IOException("The output path traverses a symbolic link.");
            }
        }
    }

    private static ToolCommandResult Failure(string rule, string message, ToolExitCode exit)
    {
        ToolPhase phase = exit == ToolExitCode.UsageOrManifest ? ToolPhase.Usage : ToolPhase.Prerequisite;
        ToolFailureCategory category = ToolFailureCategory.PrerequisiteUnavailable;
        if (exit == ToolExitCode.UsageOrManifest)
        {
            category = ToolFailureCategory.Usage;
        }

        if (exit == ToolExitCode.Cancelled)
        {
            category = ToolFailureCategory.Cancelled;
        }

        ToolDiagnostic diagnostic = new(rule, phase, category, message, "workspace");
        return new ToolCommandResult(exit == ToolExitCode.Cancelled ? "cancelled" : "failed", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, rule, exit), [diagnostic]);
    }
}
