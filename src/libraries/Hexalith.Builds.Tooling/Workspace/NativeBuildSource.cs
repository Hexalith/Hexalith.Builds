// <copyright file="NativeBuildSource.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Runtime.InteropServices;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

/// <summary>Measures the prepared checkout and its independently resolved composition boundary.</summary>
internal static class NativeBuildSource
{
    /// <summary>Captures the current package mapping, checkout, catalog and executing tool identities.</summary>
    /// <param name="mapping">The production-selected mapping.</param>
    /// <param name="manifestPath">The active module declaration.</param>
    /// <param name="cancellationToken">The operation cancellation token.</param>
    /// <returns>The exact source identity fields.</returns>
    /// <exception cref="InvalidDataException">The selected package checkout or SDK identity cannot be measured.</exception>
    internal static async Task<IReadOnlyDictionary<string, string>> CaptureAsync(SourceMapping mapping, string manifestPath, CancellationToken cancellationToken)
    {
        if (mapping.Mode != WorkspaceMode.Package || !mapping.HasGit || mapping.PlatformIdentity is not { } platform
            || mapping.EffectiveCatalogCommit is not { Length: > 0 } catalog)
        {
            throw new InvalidDataException("Prepared native build requires the resolved package-mode module and Platform checkout.");
        }

        string manifest = Path.GetFullPath(manifestPath);
        if (Path.GetFullPath(ManifestPathValidator.FindRepositoryRoot(manifest)) != mapping.Root)
        {
            throw new InvalidDataException("Prepared native build requires the active module declaration.");
        }

        string model = ModelProject(mapping);
        SortedDictionary<string, string> result = new(StringComparer.Ordinal)
        {
            ["mapping"] = mapping.ContentHash,
            ["manifest"] = Path.GetRelativePath(mapping.Root, manifest),
            ["manifest-sha256"] = NativeBuildInventory.HashFile(manifest, cancellationToken),
            ["model-project-sha256"] = NativeBuildInventory.HashFile(model, cancellationToken),
            ["platform"] = NativeBuildInventory.Hash(JsonSerializer.SerializeToUtf8Bytes(platform)),
            ["catalog-commit"] = catalog,
            ["tooling-sha256"] = NativeBuildInventory.HashFile(typeof(NativeBuildSource).Assembly.Location, cancellationToken),
            ["runtime"] = RuntimeInformation.RuntimeIdentifier,
        };

        await CaptureCheckoutAsync(mapping.Root, "module").ConfigureAwait(false);
        await CaptureCheckoutAsync(platform.PlatformPath, "platform").ConfigureAwait(false);
        if (result["platform-commit"] != platform.CheckoutCommit)
        {
            throw new InvalidDataException("Prepared native build Platform checkout differs from the selected revision.");
        }

        await CaptureCheckoutAsync(mapping.EffectiveBuildsRoot, "builds").ConfigureAwait(false);
        foreach (SourceMappingEntry reference in mapping.Entries.Where(entry => entry.Origin == "source" && entry.Path is not null
            && entry.Path != mapping.Root && entry.Path != platform.PlatformPath && entry.Path != mapping.EffectiveBuildsRoot))
        {
            await CaptureCheckoutAsync(reference.Path!, reference.Identity).ConfigureAwait(false);
        }

        CompositionProcessResult sdk = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["--version"], mapping.Root, null),
            TimeSpan.FromSeconds(20),
            cancellationToken).ConfigureAwait(false);
        if (!sdk.Started || sdk.ExitCode != 0 || sdk.OutputTruncated || string.IsNullOrWhiteSpace(sdk.Output))
        {
            throw new InvalidDataException("Prepared native build could not identify the executing SDK.");
        }

        result["sdk"] = sdk.Output.Trim();
        return result;

        async Task CaptureCheckoutAsync(string directory, string identity)
        {
            NativeBuildInventory.RequireReal(directory, directory: true);
            if (await GitAsync(directory, "rev-parse", "--show-toplevel").ConfigureAwait(false) != directory
                || (await GitAsync(directory, "status", "--porcelain", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false)).Length != 0
                || (await GitAsync(directory, "diff", "--cached", "--name-only", "--ignore-submodules=none").ConfigureAwait(false)).Length != 0)
            {
                throw new InvalidDataException("Prepared native build requires clean, exact executable checkouts.");
            }

            result[identity + "-commit"] = await GitAsync(directory, "rev-parse", "HEAD").ConfigureAwait(false);
            result[identity + "-tree"] = await GitAsync(directory, "rev-parse", "HEAD^{tree}").ConfigureAwait(false);
            string tree = await GitAsync(directory, "ls-tree", "-r", "HEAD").ConfigureAwait(false);
            result[identity + "-gitlinks"] = NativeBuildInventory.Hash(System.Text.Encoding.UTF8.GetBytes(
                string.Join('\n', tree.Split('\n').Where(line => line.StartsWith("160000 commit ", StringComparison.Ordinal)))));
        }

        async Task<string> GitAsync(string directory, params string[] arguments)
        {
            CompositionProcessResult git = await GitWorkspaceProcess.RunAsync(directory, 4 * 1024 * 1024, cancellationToken, arguments).ConfigureAwait(false);
            return git.Started && git.ExitCode == 0 && !git.TimedOut && !git.OutputTruncated
                ? git.Output.TrimEnd('\r', '\n')
                : throw new InvalidDataException("Prepared native build could not measure the selected Git checkout.");
        }
    }

    /// <summary>Gets the exact model project selected by the package mapping.</summary>
    /// <param name="mapping">The validated mapping.</param>
    /// <returns>The Platform-owned model project.</returns>
    internal static string ModelProject(SourceMapping mapping) => Path.Combine(
        mapping.PlatformIdentity!.PlatformPath, "src", "Hexalith.Platform.Composition", "Hexalith.Platform.Composition.csproj");
}
