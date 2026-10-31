// <copyright file="CompositionCiOwnerReceipt.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

/// <summary>Durable, metadata-only ownership recovery for a single disposable CI job.</summary>
/// <param name="Schema">The receipt version.</param>
/// <param name="RunId">The exact owner run.</param>
/// <param name="AttemptId">The owner attempt.</param>
/// <param name="EnvironmentId">The environment identity.</param>
/// <param name="ExecutionContext">The actual CI repository, run, attempt and job.</param>
/// <param name="StateDirectory">The durable owner metadata root.</param>
/// <param name="CreatedAtUtc">The original allocation request time.</param>
public sealed record CompositionCiOwnerReceipt(string Schema, string RunId, string AttemptId, string EnvironmentId, JsonElement ExecutionContext, string StateDirectory, DateTimeOffset CreatedAtUtc)
{
    /// <summary>Gets the supported receipt identifier.</summary>
    public const string SupportedSchema = "hexalith.module.ci-owner-receipt.v1";

    /// <summary>Persists the exact recovery identities before any run workspace or service is allocated.</summary>
    /// <param name="path">An external receipt file, outside deleted run workspaces.</param>
    /// <param name="owner">The newly issued owner state.</param>
    /// <param name="stateDirectory">The runner metadata root.</param>
    /// <param name="workspaceRoot">The run workspace root forbidden for this receipt.</param>
    /// <param name="cancellationToken">The preallocation allowance.</param>
    /// <returns>The durable publication task.</returns>
    /// <exception cref="IOException">The receipt is unwritable or already bound to another allocation.</exception>
    /// <exception cref="InvalidDataException">The context or receipt path is ineligible.</exception>
    internal static async Task PersistAsync(string path, CompositionRunState owner, string stateDirectory, string workspaceRoot, CancellationToken cancellationToken)
    {
        string fullPath = Path.GetFullPath(path);
        string workspace = Path.GetFullPath(workspaceRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        JsonElement context = owner.ExecutionContext ?? throw new InvalidDataException("CI recovery requires an actual execution context.");
        if (owner.SelectedTest?.Mode != "package" || context.GetProperty("kind").GetString() != "ci"
            || context.GetProperty("runnerKind").GetString() != "github-hosted" || context.GetProperty("operatingSystem").GetString() != "Linux"
            || !context.GetProperty("disposable").GetBoolean()
            || fullPath.StartsWith(workspace, StringComparison.Ordinal) || fullPath == workspace.TrimEnd(Path.DirectorySeparatorChar))
        {
            throw new InvalidDataException("CI recovery requires disposable Linux GitHub-hosted package integration and a receipt outside run workspaces.");
        }

        if (!SelectedNativeTestReport.IsOwnedParent(fullPath, Path.GetPathRoot(fullPath)!)
            || (Path.Exists(fullPath) && !SelectedNativeTestReport.IsOwnedRegularPath(fullPath, Path.GetDirectoryName(fullPath)!)))
        {
            throw new IOException("The CI ownership receipt must be a regular file without link traversal.");
        }

        string directory = Path.GetDirectoryName(fullPath)!;
        CompositionWorkspace.CreatePrivateDirectory(directory);
        string lockPath = fullPath + ".lock";
        if (Path.Exists(lockPath) && !SelectedNativeTestReport.IsOwnedRegularPath(lockPath, directory))
        {
            throw new IOException("The CI receipt lock cannot be verified.");
        }

        FileStream held = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        await using (held.ConfigureAwait(false))
        {
            CompositionCiOwnerReceipt? existing = await CompositionDocumentStore.ReadIfExistsAsync<CompositionCiOwnerReceipt>(fullPath, cancellationToken).ConfigureAwait(false);
            CompositionCiOwnerReceipt receipt = new(SupportedSchema, owner.RunId, owner.AttemptId!, owner.EnvironmentId!, context, Path.GetFullPath(stateDirectory), owner.CreatedAt!.Value);
            if (existing is not null && (existing.Schema != SupportedSchema || existing.RunId != receipt.RunId || existing.AttemptId != receipt.AttemptId
                || existing.EnvironmentId != receipt.EnvironmentId || !JsonElement.DeepEquals(existing.ExecutionContext, context) || existing.StateDirectory != receipt.StateDirectory))
            {
                throw new IOException("An ownership receipt cannot be rebound to another allocation.");
            }

            await CompositionDocumentStore.WriteAsync(fullPath, receipt, cancellationToken).ConfigureAwait(false);
        }
    }
}
