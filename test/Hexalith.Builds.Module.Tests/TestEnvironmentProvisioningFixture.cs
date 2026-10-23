// <copyright file="TestEnvironmentProvisioningFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Xunit;

/// <summary>Provides explicit offline observations and a self-consistent catalog for process-boundary tests.</summary>
internal static class TestEnvironmentProvisioningFixture
{
    /// <summary>Prepares model observations; these fixtures never represent live-service evidence.</summary>
    /// <param name="plan">The preliminary owned plan.</param>
    /// <param name="manifest">The declaring suite manifest.</param>
    /// <param name="root">The fixture repository root.</param>
    /// <returns>A plan with verified offline catalog and composition identities.</returns>
    internal static async Task<CompositionRunPlan> PrepareAsync(CompositionRunPlan plan, string manifest, string root)
    {
        await File.WriteAllTextAsync(Path.Combine(root, "Hexalith.Builds.slnx"), "<Solution />", TestContext.Current.CancellationToken).ConfigureAwait(false);
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(root);
        string owner = plan.SelectedTest!.ModuleId;
        string repository = CompositionDeclarationClosure.Repository(owner);
        mapping = mapping with
        {
            Entries = [.. mapping.Entries.Where(entry => entry.Identity != repository), new(repository, "source", root)],
            PlatformIdentity = new PlatformIdentity(new string('a', 40), new string('a', 40), new string('a', 40), root, null, new string('c', 40), null, "offline-fixture", true),
        };

        // Authoritative reference owners must use their canonical module declaration path.
        if (owner is "eventstore" or "tenants" or "memories")
        {
            string canonical = Path.Combine(root, "module", "hexalith.module-manifest.v2.json");
            _ = Directory.CreateDirectory(Path.GetDirectoryName(canonical)!);
            File.Copy(manifest, canonical, overwrite: true);
            manifest = canonical;
        }

        IReadOnlyList<PlatformModuleDeclaration> closure = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [manifest]) { ActiveModule = owner });
        plan = plan with
        {
            SourceMapping = mapping,
            PlatformDeclarationPaths = [manifest],
            PlatformActiveModule = owner,
            PlatformDeclarationHash = plan.SelectedTest.ManifestHash,
            CompositionContentIdentity = TestEnvironmentDescriptorWriter.CompositionIdentity(closure, plan.SelectedTest),
            StartupDeadlineUtc = plan.SelectedTest.RequestedAtUtc.AddSeconds(600),
            EffectiveDeadline = new CompositionEffectiveDeadline(600, false, null, null),
            StartupTasks = [],
        };
        string directory = Path.Combine(plan.Workspace, "source-mapping");
        CompositionWorkspace.CreatePrivateDirectory(directory);
        string catalogFile = Path.Combine(directory, "pinned-tree", "Props", "Directory.Packages.props");
        CompositionWorkspace.CreatePrivateDirectory(Path.GetDirectoryName(catalogFile)!);
        await File.WriteAllTextAsync(catalogFile, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(false);
        string hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(catalogFile, TestContext.Current.CancellationToken).ConfigureAwait(false)));
        Dictionary<string, string> files = new(StringComparer.Ordinal) { ["Props/Directory.Packages.props"] = hash };
        Dictionary<string, string> versions = new(StringComparer.Ordinal) { ["fixture"] = "1.0.0" };
        Dictionary<string, IReadOnlyDictionary<string, string>> projects = new(StringComparer.Ordinal);
        CatalogVersionSnapshot snapshot = new(mapping.ContentHash, mapping.PlatformIdentity.CatalogCommit!, hash, files, versions, projects, CatalogVersionSnapshot.ComputeHash(mapping.ContentHash, mapping.PlatformIdentity.CatalogCommit!, hash, files, versions, projects));
        await File.WriteAllTextAsync(Path.Combine(directory, "catalog-versions.json"), JsonSerializer.Serialize(snapshot), TestContext.Current.CancellationToken).ConfigureAwait(false);
        string mappingFile = Path.Combine(directory, "mapping.json");
        await File.WriteAllTextAsync(mappingFile, JsonSerializer.Serialize(mapping), TestContext.Current.CancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(mappingFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return plan;
    }

    /// <summary>Completes an explicitly simulated readiness snapshot for offline handoff tests.</summary>
    /// <param name="readiness">The preliminary snapshot.</param>
    /// <param name="moduleId">The observed owner.</param>
    /// <returns>A complete offline snapshot.</returns>
    internal static CompositionReadiness Observe(CompositionReadiness readiness, string moduleId) => readiness with
    {
        ObservedAt = DateTimeOffset.UtcNow,
        StartupDurationSeconds = 0,
        Resources = [.. readiness.Resources.Select(resource => resource with { ModuleId = moduleId, ObservedAt = DateTimeOffset.UtcNow, Endpoints = [new("http", "http", readiness.EventStoreEndpoint)] })],
    };
}
