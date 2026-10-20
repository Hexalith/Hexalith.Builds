// <copyright file="PlatformAdoptionTestWorkspace.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Manifest;

using Xunit;

/// <summary>A complete local Git enrollment workspace with no external network access.</summary>
internal sealed class PlatformAdoptionTestWorkspace : IDisposable
{
    private readonly WorkspaceGitFixture _git;

    private PlatformAdoptionTestWorkspace(WorkspaceGitFixture git) => _git = git;

    /// <summary>Gets the active module root.</summary>
    public string Root => _git.Checkout;

    /// <summary>Gets the active declaration.</summary>
    public string Manifest => _git.Manifest;

    /// <summary>Creates a complete enrollment workspace.</summary>
    /// <param name="nested">Whether a direct dependency declares an uninitialized nested submodule.</param>
    /// <returns>The complete workspace.</returns>
    public static async Task<PlatformAdoptionTestWorkspace> CreateAsync(bool nested = false)
    {
        WorkspaceGitFixture git = await WorkspaceGitFixture.CreateAsync(withNested: nested).ConfigureAwait(true);
        PlatformAdoptionTestWorkspace? workspace = null;
        try
        {
            workspace = new(git);
            _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "update", "--init").ConfigureAwait(true);
            foreach (string name in new[] { "EventStore", "Tenants", "Memories", "McpCli" })
            {
                _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(Path.Combine(git.Directory, "dependency")).AbsoluteUri, "references/Hexalith." + name).ConfigureAwait(true);
            }

            using PlatformManifestTestWorkspace sample = new();
            sample.Change("modules.0.identity.dependencies", "[\"eventstore\",\"tenants\",\"memories\"]");
            workspace.Write("module.json", sample.Document.ToJsonString());
            workspace.Write("README.md", "No validation command may execute this file.");
            workspace.Write("module/hosting-inventory.json", "{\"schemaVersion\":1,\"projects\":[]}");
            const string pin = "{\"version\":1,\"isRoot\":true,\"tools\":{\"hexalith.builds.module.cli\":{\"version\":\"4.34.0\",\"commands\":[\"hexalith-module\"],\"rollForward\":false}}}";
            workspace.Write(".config/dotnet-tools.json", pin);
            workspace.Write("references/Hexalith.Platform/.config/dotnet-tools.json", pin);
            string platform = Path.Combine(workspace.Root, "references/Hexalith.Platform");
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", ".config").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(platform, "-c", "user.name=Enrollment Test", "-c", "user.email=enrollment@example.invalid", "commit", "-m", "test fixture pin").ConfigureAwait(true);
            await workspace.TrackAsync().ConfigureAwait(true);
            PlatformAdoptionTestWorkspace result = workspace;
            workspace = null;
            return result;
        }
        finally
        {
            workspace?.Dispose();
        }
    }

    /// <summary>Writes one fixture file.</summary>
    /// <param name="path">The relative file path.</param>
    /// <param name="content">The fixture contents.</param>
    public void Write(string path, string content)
    {
        string file = Path.Combine(Root, path);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    /// <summary>Tracks fixture inputs without committing.</summary>
    /// <returns>The tracking task.</returns>
    public async Task TrackAsync() => _ = await WorkspaceGitFixture.RunGitAsync(Root, "add", ".").ConfigureAwait(true);

    /// <summary>Inspects without mutating or initializing the fixture.</summary>
    /// <returns>The complete report.</returns>
    public Task<PlatformAdoptionValidationResult> ValidateAsync() => PlatformAdoptionValidator.ValidateAsync([Manifest], Root, TestContext.Current.CancellationToken);

    /// <inheritdoc />
    public void Dispose() => _git.Dispose();
}
