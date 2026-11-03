// <copyright file="ReleasedDependencyProfileTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Separates authenticated dependency profile metadata from executable owner validation.</summary>
public sealed class ReleasedDependencyProfileTests
{
    /// <summary>Loads released test metadata without requiring dependency source projects in Platform.</summary>
    [Fact]
    public void ReleasedDependencyProfileDoesNotRequireItsSourceProject()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = ReleasedProfile(workspace.Root, "tests/Dependency.Tests.csproj");
        string snapshot = Snapshot(workspace.Root);
        File.Exists(Path.Combine(Path.GetDirectoryName(snapshot)!, "tests", "Dependency.Tests.csproj")).ShouldBeFalse();
        PlatformManifestValidator.Validate([snapshot], TestContext.Current.CancellationToken).IsValid.ShouldBeFalse();
        PlatformModuleDeclaration declaration = CompositionDeclarationClosure.Load(options).Single();
        declaration.Declaration.GetProperty("tests")[0].GetProperty("entry").GetString().ShouldBe("tests/Dependency.Tests.csproj");
    }

    /// <summary>Keeps caller-supplied manifests strict in both source and package modes.</summary>
    /// <param name="mode">The selected composition mode.</param>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public void SuppliedOwnerProfileStillRequiresItsOwnedEntry(WorkspaceMode mode)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = ReleasedProfile(workspace.Root, "tests/Dependency.Tests.csproj");
        options = options with { Mapping = options.Mapping with { Mode = mode }, DeclarationPaths = [Snapshot(workspace.Root)] };
        Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options)).Message.ShouldContain("tests[0].entry");
    }

    /// <summary>Retains canonical ownership and executor checks for authenticated metadata.</summary>
    /// <param name="entry">An ineligible declaration entry.</param>
    [Theory]
    [InlineData("references/dependency.csproj")]
    [InlineData("../escape.csproj")]
    [InlineData("tests/wrong.py")]
    [InlineData("$RUNNER_TEMP/test.csproj")]
    public void ReleasedMetadataStillRejectsIneligibleEntryPaths(string entry)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = ReleasedProfile(workspace.Root, entry);
        Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options)).Message.ShouldContain("tests[0].entry");
    }

    /// <summary>Never defers executable startup-task availability for a released dependency.</summary>
    [Fact]
    public void ReleasedStartupTaskStillRequiresItsExecutable()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = ReleasedProfile(workspace.Root, "tests/Dependency.Tests.csproj");
        JsonNode snapshot = JsonNode.Parse(File.ReadAllText(Snapshot(workspace.Root)))!;
        snapshot["modules"]![0]!["lifecycle"]!["tasks"] = workspace.Document["modules"]![0]!["lifecycle"]!["tasks"]!.DeepClone();
        File.WriteAllText(Snapshot(workspace.Root), snapshot.ToJsonString());
        options = AuthenticateFixtureSnapshot(options, workspace.Root);
        Should.Throw<InvalidDataException>(() => CompositionDeclarationClosure.Load(options)).Message.ShouldContain("lifecycle.tasks[0].executable");
    }

    /// <summary>Rechecks authenticated bytes at the validator boundary before metadata-only treatment.</summary>
    [Fact]
    public void ChangedAuthenticatedBytesCannotUseMetadataValidation()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionDeclarationOptions options = ReleasedProfile(workspace.Root, "tests/Dependency.Tests.csproj");
        string snapshot = Snapshot(workspace.Root);
        Dictionary<string, string> authenticated = new(StringComparer.Ordinal)
        {
            [Path.GetFullPath(snapshot)] = options.ReleaseSnapshotHashes["memories/1.0.0"],
        };
        File.AppendAllText(snapshot, " ");
        PlatformManifestValidationResult result = PlatformManifestValidator.Validate([snapshot], authenticated, TestContext.Current.CancellationToken);
        result.IsValid.ShouldBeFalse();
        result.Diagnostics.ShouldContain(diagnostic => diagnostic.Message.Contains("changed after its source/hash verification", StringComparison.Ordinal));
    }

    private static string Snapshot(string root) => Path.Combine(root, "src", "Hexalith.Platform.Composition", "Declarations", "memories.json");

    private static CompositionDeclarationOptions ReleasedProfile(string root, string entry)
    {
        CompositionDeclarationOptions options = DeclarationClosureTests.PackageOptions(root);
        string path = Snapshot(root);
        JsonNode snapshot = JsonNode.Parse(File.ReadAllText(path))!;
        snapshot["modules"]![0]!["tests"] = new JsonArray(new JsonObject
        {
            ["name"] = "dependency-metadata",
            ["executor"] = "mtp",
            ["entry"] = entry,
            ["reportFormat"] = "trx",
            ["executionTimeoutSeconds"] = 120,
            ["stopTimeoutSeconds"] = 90,
            ["mode"] = "package",
        });
        File.WriteAllText(path, snapshot.ToJsonString());
        return AuthenticateFixtureSnapshot(options, root);
    }

    private static CompositionDeclarationOptions AuthenticateFixtureSnapshot(CompositionDeclarationOptions options, string root)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Snapshot(root))));
        string path = Path.Combine(Path.GetDirectoryName(Snapshot(root))!, "provenance.json");
        JsonNode provenance = JsonNode.Parse(File.ReadAllText(path))!;
        provenance["memories"]!["sha256"] = hash;
        File.WriteAllText(path, provenance.ToJsonString());
        return options with { ReleaseSnapshotHashes = new Dictionary<string, string>(options.ReleaseSnapshotHashes, StringComparer.Ordinal) { ["memories/1.0.0"] = hash } };
    }
}
