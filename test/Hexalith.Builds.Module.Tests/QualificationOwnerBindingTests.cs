// <copyright file="QualificationOwnerBindingTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestEnvironment;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Proves explicit owner requests cannot change scope or cross issued run boundaries.</summary>
public sealed class QualificationOwnerBindingTests
{
    /// <summary>Canonical owner sets are defensive and preserve existing default serialization.</summary>
    [Fact]
    public void CanonicalOwnersAreDefensiveAndDefaultsRemainAbsent()
    {
        string[] requested = ["tenants", "eventstore"];
        IReadOnlyList<string> canonical = CompositionQualificationOwners.Normalize(requested);
        requested[0] = "memories";
        canonical.ShouldBe(["eventstore", "tenants"]);
        CompositionQualificationOwners.Normalize(null).ShouldBeEmpty();
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            string original = JsonSerializer.Serialize(plan);
            CompositionQualificationOwners.Bind(plan, CompositionSigningKey.Create()).ShouldBeSameAs(plan);
            CompositionQualificationOwners.Verify(plan, null, required: false);
            JsonSerializer.Serialize(plan).ShouldBe(original);
            original.ShouldNotContain("QualificationOwners");
            _ = Should.Throw<InvalidDataException>(() => CompositionQualificationOwners.Verify(plan, null, required: true));
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Unknown, malformed and duplicate selections have no admissible canonical representation.</summary>
    /// <param name="first">The first requested identity.</param>
    /// <param name="second">The optional repeated identity.</param>
    [Theory]
    [InlineData("", null)]
    [InlineData("Tenants", null)]
    [InlineData("tenants ", null)]
    [InlineData("../tenants", null)]
    [InlineData("--mode=source", null)]
    [InlineData("future-owner", null)]
    [InlineData("tenants", "tenants")]
    public void InvalidOwnerSetsRefuse(string first, string? second)
    {
        IReadOnlyList<string> owners = second is null ? [first] : [first, second];
        _ = Should.Throw<InvalidDataException>(() => CompositionQualificationOwners.Normalize(owners));
    }

    /// <summary>The canonical expanded plan survives a real JSON handoff, without persisting its key.</summary>
    [Fact]
    public void ExpandedPlanRoundTripsWithPurposeBoundTag()
    {
        using PlatformManifestTestWorkspace workspace = new();
        string key = CompositionSigningKey.Create();
        CompositionRunPlan bound = CompositionQualificationOwners.Bind(CreatePlan(workspace.Root), key);
        string json = JsonSerializer.Serialize(bound);
        json.Contains(key, StringComparison.Ordinal).ShouldBeFalse("Run-plan metadata must never retain the per-run key.");
        CompositionRunPlan received = JsonSerializer.Deserialize<CompositionRunPlan>(json).ShouldNotBeNull();
        CompositionQualificationOwners.Verify(received, key, required: true);
        CompositionQualificationOwners.Verify(received, key, required: false);
        received.QualificationOwners.ShouldBe(["tenants"]);
    }

    /// <summary>Every request, attempt, source and selected-suite binding refuses alteration or replay.</summary>
    /// <param name="fault">The changed binding.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fixture fault is unknown.</exception>
    [Theory]
    [InlineData("owners")]
    [InlineData("drop-owners")]
    [InlineData("drop-binding")]
    [InlineData("drop-both")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("environment")]
    [InlineData("active-module")]
    [InlineData("source")]
    [InlineData("declaration")]
    [InlineData("declaration-path")]
    [InlineData("composition")]
    [InlineData("test")]
    [InlineData("key")]
    [InlineData("invalid-tag")]
    public void AlteredExpandedPlanRefuses(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string key = CompositionSigningKey.Create();
        CompositionRunPlan plan = CompositionQualificationOwners.Bind(CreatePlan(workspace.Root), key);
        plan = fault switch
        {
            "owners" => plan with { QualificationOwners = ["memories"] },
            "drop-owners" => plan with { QualificationOwners = null },
            "drop-binding" => plan with { QualificationOwnersBinding = null },
            "drop-both" => plan with { QualificationOwners = null, QualificationOwnersBinding = null },
            "run" => plan with { RunId = CompositionRunPlanFactory.NewRunId() },
            "attempt" => plan with { AttemptId = CompositionRunPlanFactory.NewRunId() },
            "environment" => plan with { EnvironmentId = CompositionRunPlanFactory.NewRunId() },
            "active-module" => plan with { PlatformActiveModule = "tenants" },
            "source" => plan with { SourceMapping = plan.SourceMapping! with { ActiveModule = "foreign" } },
            "declaration" => plan with { PlatformDeclarationHash = new string('b', 64) },
            "declaration-path" => plan with { PlatformDeclarationPaths = ["foreign.json"] },
            "composition" => plan with { CompositionContentIdentity = "sha256:" + new string('b', 64) },
            "test" => plan with { SelectedTest = plan.SelectedTest! with { Name = "foreign" } },
            "key" => plan,
            "invalid-tag" => plan with { QualificationOwnersBinding = new string('z', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
        string suppliedKey = fault == "key" ? CompositionSigningKey.Create() : key;
        _ = Should.Throw<InvalidDataException>(() => CompositionQualificationOwners.Verify(plan, suppliedKey, required: true));
        if (fault != "drop-both")
        {
            _ = Should.Throw<InvalidDataException>(() => CompositionQualificationOwners.Verify(plan, suppliedKey, required: false));
        }
    }

    /// <summary>A stale serialized computed hash cannot authenticate changed source content.</summary>
    /// <param name="platform">Whether to alter the Platform source identity instead of a mapping entry.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void JsonSourceMutationCannotReuseTheSerializedHash(bool platform)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string key = CompositionSigningKey.Create();
        CompositionRunPlan bound = CompositionQualificationOwners.Bind(CreatePlan(workspace.Root), key);
        JsonNode document = JsonNode.Parse(JsonSerializer.Serialize(bound))!;
        string originalHash = document["SourceMappingHash"]!.GetValue<string>();
        if (platform)
        {
            document["SourceMapping"]!["PlatformIdentity"]!["Commit"] = new string('c', 40);
        }
        else
        {
            document["SourceMapping"]!["Entries"] = new JsonArray(new JsonObject { ["Identity"] = "Hexalith.Foreign", ["Origin"] = "package", ["Path"] = null });
        }

        document["SourceMappingHash"]!.GetValue<string>().ShouldBe(originalHash);
        CompositionRunPlan received = document.Deserialize<CompositionRunPlan>().ShouldNotBeNull();
        received.SourceMappingHash.ShouldNotBe(originalHash);
        _ = Should.Throw<InvalidDataException>(() => CompositionQualificationOwners.Verify(received, key, required: true));
    }

    /// <summary>The explicit request remains part of composition identity even if its owner was already present.</summary>
    [Fact]
    public void CompositionIdentityBindsRequestWithoutChangingDefaultHash()
    {
        using PlatformManifestTestWorkspace workspace = new();
        SourceMapping mapping = CompositionTestFiles.CreatePlatformMapping(workspace.Root);
        IReadOnlyList<Hexalith.Builds.Tooling.Manifest.PlatformModuleDeclaration> declarations = CompositionDeclarationClosure.Load(new CompositionDeclarationOptions(mapping, [workspace.Save()]) { ActiveModule = "eventstore" });
        string original = TestEnvironmentDescriptorWriter.CompositionIdentity(declarations, null);
        TestEnvironmentDescriptorWriter.CompositionIdentity(declarations, null, []).ShouldBe(original);
        TestEnvironmentDescriptorWriter.CompositionIdentity(declarations, null, ["eventstore"]).ShouldNotBe(original);
        TestEnvironmentDescriptorWriter.CompositionIdentity(declarations, null, ["tenants", "eventstore"]).ShouldBe(TestEnvironmentDescriptorWriter.CompositionIdentity(declarations, null, ["eventstore", "tenants"]));
    }

    /// <summary>Creates a complete synthetic plan for binding and actual loader controls.</summary>
    /// <param name="root">The isolated fixture root.</param>
    /// <returns>The expanded metadata-only plan.</returns>
    internal static CompositionRunPlan CreatePlan(string root)
    {
        SourceMapping mapping = new(WorkspaceMode.Package, root, "Hexalith.EventStore", [], false)
        {
            PlatformIdentity = new PlatformIdentity(new string('a', 40), new string('a', 40), new string('a', 40), root, "v1.1.6", new string('b', 40), "[4.43.6,5.0.0-0)", "platform.json", false),
        };
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ModuleTestSelection selected = new("eventstore", "platform-package", "dotnet-test", "test.csproj", new string('a', 64), new string('b', 64), 120, 30, now, now.AddSeconds(120), now.AddSeconds(120), "package");
        return CompositionTestFiles.CreatePlan(root) with
        {
            SourceMapping = mapping,
            PlatformDeclarationPaths = [Path.Combine(root, "module.json")],
            PlatformDeclarationHash = new string('a', 64),
            PlatformActiveModule = "eventstore",
            CompositionContentIdentity = "sha256:" + new string('a', 64),
            QualificationOwners = ["tenants"],
            SelectedTest = selected,
            SelectedTests = [selected],
        };
    }
}
