// <copyright file="FixtureProducerBindingTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Checks that metadata alone cannot create a signed Platform fixture producer.</summary>
public sealed class FixtureProducerBindingTests
{
    private const string _module = "platformtestextension";
    private const string _profile = "eventstore-extension-package";
    private const string _manifest = "tests/fixtures/Hexalith.Platform.TestExtension/hexalith.module-manifest.v2.json";
    private const string _entry = "tests/Hexalith.Platform.TestExtension.Qualification.Tests/Hexalith.Platform.TestExtension.Qualification.Tests.csproj";

    /// <summary>The proof leaves existing plans unchanged and never aliases the module to Platform.</summary>
    [Fact]
    public void ExistingPlansAndRepositoryResolutionStayUnchanged()
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionRunPlan plan = QualificationOwnerBindingTests.CreatePlan(workspace.Root);
        CompositionFixtureProducer.Bind(plan, CompositionSigningKey.Create()).ShouldBeSameAs(plan);
        CompositionFixtureProducer.Verify(plan, null).ShouldBeNull();
        JsonSerializer.Serialize(plan).ShouldNotContain("FixtureProducerBinding");
        CompositionDeclarationClosure.Repository(_module).ShouldBe("Hexalith.Platformtestextension");
    }

    /// <summary>Missing, supplied and wrong-purpose tags cannot admit a package fixture.</summary>
    /// <param name="fault">The untrusted producer input.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fixture mutation is unknown.</exception>
    [Theory]
    [InlineData("missing")]
    [InlineData("raw-hash")]
    [InlineData("malformed-tag")]
    [InlineData("wrong-mode")]
    [InlineData("other-purpose")]
    public void UntrustedPlansCannotProduceFixtureAuthority(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionRunPlan plan = FixturePlan(workspace.Root);
        string key = CompositionSigningKey.Create();
        plan = fault switch
        {
            "missing" => plan,
            "raw-hash" => plan with { FixtureProducerBinding = new string('a', 64) },
            "malformed-tag" => plan with { FixtureProducerBinding = new string('!', 64) },
            "wrong-mode" => plan with { SourceMapping = plan.SourceMapping! with { Mode = WorkspaceMode.Source }, FixtureProducerBinding = new string('a', 64) },
            "other-purpose" => plan with { FixtureProducerBinding = CompositionQualificationOwners.Bind(QualificationOwnerBindingTests.CreatePlan(workspace.Root), key).QualificationOwnersBinding },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        _ = Should.Throw<InvalidDataException>(() => CompositionFixtureProducer.Verify(plan, key, required: true));
    }

    /// <summary>A matching module label does not admit a detached root, entry, or closure.</summary>
    /// <param name="fault">The wrong ownership or selection reference.</param>
    /// <exception cref="ArgumentOutOfRangeException">The fixture mutation is unknown.</exception>
    /// <returns>A task that verifies the refusal.</returns>
    [Theory]
    [InlineData("foreign-root")]
    [InlineData("repository-alias")]
    [InlineData("dependency-path")]
    [InlineData("entry")]
    [InlineData("wrong-profile")]
    [InlineData("batch")]
    [InlineData("incomplete-closure")]
    public async Task UnapprovedSourceAndClosureRefuseBeforeStartupAsync(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        CompositionRunPlan plan = FixturePlan(workspace.Root);
        SourceMapping mapping = plan.SourceMapping!;
        ModuleTestSelection selected = plan.SelectedTest!;
        plan = fault switch
        {
            "foreign-root" => plan with { SourceMapping = mapping with { PlatformIdentity = mapping.PlatformIdentity! with { IsPlatformRepository = false } } },
            "repository-alias" => plan with { SourceMapping = mapping with { Entries = [new SourceMappingEntry("Hexalith.Platformtestextension", "source", workspace.Root)] } },
            "dependency-path" => plan with { PlatformDeclarationPaths = [Path.Combine(workspace.Root, "references", _manifest)] },
            "entry" => plan with { SelectedTest = selected with { Entry = "foreign.csproj" } },
            "wrong-profile" => plan with { SelectedTest = selected with { Name = "supplied-extension" } },
            "batch" => plan with { SelectedTests = [selected, selected] },
            "incomplete-closure" => plan,
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        _ = await Should.ThrowAsync<InvalidDataException>(() => CompositionFixtureProducer.ValidateBeforeStartupAsync(
            plan.SourceMapping,
            plan.PlatformActiveModule,
            plan.SelectedTest,
            plan.SelectedTests,
            plan.PlatformDeclarationPaths[0],
            [],
            TestContext.Current.CancellationToken)).ConfigureAwait(true);
    }

    private static CompositionRunPlan FixturePlan(string root)
    {
        CompositionRunPlan plan = QualificationOwnerBindingTests.CreatePlan(root);
        ModuleTestSelection selected = plan.SelectedTest! with { ModuleId = _module, Name = _profile, Executor = "mtp", Entry = _entry };
        SourceMapping mapping = plan.SourceMapping! with
        {
            HasGit = true,
            ActiveModule = "PlatformCheckout",
            Entries = [new SourceMappingEntry("PlatformCheckout", "source", root)],
            PlatformIdentity = plan.PlatformIdentity! with { IsPlatformRepository = true },
        };
        return plan with
        {
            SourceMapping = mapping,
            PlatformDeclarationPaths = [Path.Combine(root, _manifest)],
            PlatformActiveModule = _module,
            QualificationOwners = null,
            SelectedTest = selected,
            SelectedTests = [selected],
        };
    }
}
