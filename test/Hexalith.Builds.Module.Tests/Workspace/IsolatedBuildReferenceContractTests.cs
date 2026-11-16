// <copyright file="IsolatedBuildReferenceContractTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Workspace;

using Microsoft.Build.Utilities;

using Shouldly;

using Xunit;

using Task = System.Threading.Tasks.Task;

/// <summary>Verifies the closed, byte-bound managed build-order isolation contract.</summary>
public sealed class IsolatedBuildReferenceContractTests
{
    private static readonly string[] _projects = ["fixture/Fixture.csproj"];
    private static readonly string[] _roots = ["adapter", "fixture"];

    /// <summary>Only an authenticated nonshipping build-order edge admits exact isolated globals.</summary>
    /// <returns>The completed source-bound contract verification.</returns>
    [Fact]
    public async Task SourceBoundBuildOrderContractPreservesReleaseImportsAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (SourceMapping mapping, TaskItem reference, string owner, string parent) = await PrepareAsync(fixture).ConfigureAwait(true);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out string canonical).ShouldBeTrue();
        canonical.ShouldContain("RestoreLockedMode=true");
        canonical.Split(';').ShouldContain("WarningsNotAsErrors=");
        canonical.ShouldContain("HexalithBuildsRoot=" + mapping.EffectiveBuildsRoot);
        canonical.ShouldContain("HexalithIsolatedParentCommonTargets=" + parent);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping with { ManagedAuthority = null }, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
        reference.SetMetadata("ReferenceOutputAssembly", "true");
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
    }

    /// <summary>Catalog substitutions, audit overrides, missing source binding and shipping owners refuse.</summary>
    /// <returns>The completed refusal verification.</returns>
    [Fact]
    public async Task UnsafeMetadataOrChangedSourceRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (SourceMapping mapping, TaskItem reference, string owner, string parent) = await PrepareAsync(fixture).ConfigureAwait(true);
        string original = reference.GetMetadata("AdditionalProperties");
        foreach (string properties in new[]
        {
            original + ";NuGetAudit=false",
            original.Replace("HexalithBuildsRoot=" + mapping.EffectiveBuildsRoot, "HexalithBuildsRoot=" + fixture.Directory, StringComparison.Ordinal),
            original.Replace("WarningsNotAsErrors=;", "WarningsNotAsErrors=NU1903;", StringComparison.Ordinal),
            original.Replace("RestoreLockedMode=true", "RestoreLockedMode=false", StringComparison.Ordinal),
            original.Replace("HexalithIsolatedParentCommonTargets=" + parent, "HexalithIsolatedParentCommonTargets=", StringComparison.Ordinal),
        })
        {
            reference.SetMetadata("AdditionalProperties", properties);
            IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
        }

        reference.SetMetadata("AdditionalProperties", original);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;true;false;false", "net11.0", parent, out _).ShouldBeFalse();
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net10.0", parent, out _).ShouldBeFalse();
        await File.AppendAllTextAsync(Path.Combine(fixture.Checkout, "adapter", "Framework.props"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
    }

    /// <summary>Untracked descriptors and imports cannot supply the source-bound declaration.</summary>
    /// <returns>The completed tracked-source membership verification.</returns>
    [Fact]
    public async Task UntrackedDescriptorAndImportedSourceRefuseAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (SourceMapping mapping, TaskItem reference, string owner, string parent) = await PrepareAsync(fixture).ConfigureAwait(true);
        string descriptor = Path.Combine(fixture.Checkout, "adapter", "contract.json");
        string untracked = Path.Combine(fixture.Checkout, "adapter", "untracked.json");
        File.Copy(descriptor, untracked);
        reference.SetMetadata("HexalithIsolatedBuildContract", "adapter/untracked.json");
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
        reference.SetMetadata("HexalithIsolatedBuildContract", "adapter/contract.json");
        string imported = Path.Combine(fixture.Checkout, "untracked.props");
        await File.WriteAllTextAsync(imported, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "adapter", "Framework.props"), "<Project><Import Project=\"../untracked.props\" /></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "adapter/Framework.props").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind import outside declared roots").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        mapping = mapping with { ManagedAuthority = mapping.ManagedAuthority! with { ModuleHead = head } };
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
    }

    /// <summary>Canonical Git text conversion is admitted without executing arbitrary filters.</summary>
    /// <returns>The completed declared-EOL verification.</returns>
    [Fact]
    public async Task DeclaredGitEolConversionRemainsSourceBoundAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (SourceMapping mapping, TaskItem reference, string owner, string parent) = await PrepareAsync(fixture).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, ".gitattributes"), "adapter/Framework.props text eol=crlf\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string props = Path.Combine(fixture.Checkout, "adapter", "Framework.props");
        await File.WriteAllTextAsync(props, "<Project>\n</Project>\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".gitattributes", "adapter/Framework.props").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind explicit EOL policy").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        mapping = mapping with { ManagedAuthority = mapping.ManagedAuthority! with { ModuleHead = head } };
        await File.WriteAllTextAsync(props, "<Project>\r\n</Project>\r\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out string diagnostic).ShouldBeTrue(diagnostic);
        await File.AppendAllTextAsync(props, "<!-- changed -->", TestContext.Current.CancellationToken).ConfigureAwait(true);
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
    }

    /// <summary>A declared option namespace cannot select SDK analyzer policy properties.</summary>
    /// <returns>The completed reserved-policy refusal verification.</returns>
    [Fact]
    public async Task SdkPolicyOptionNamespaceRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        (SourceMapping mapping, TaskItem reference, string owner, string parent) = await PrepareAsync(fixture).ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, "adapter", "contract.json");
        JsonNode declaration = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        declaration["propertyPrefix"] = "EnableNET";
        declaration["properties"] = new JsonObject { ["EnableNETAnalyzers"] = "false" };
        await File.WriteAllTextAsync(path, declaration.ToJsonString(), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "adapter/contract.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind SDK policy override refusal").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        mapping = mapping with { ManagedAuthority = mapping.ManagedAuthority! with { ModuleHead = head } };
        reference.SetMetadata("AdditionalProperties", reference.GetMetadata("AdditionalProperties").Replace("FixtureOptionsMode=package", "EnableNETAnalyzers=false", StringComparison.Ordinal));
        IsolatedBuildReferenceContract.TryCanonicalize(mapping, reference, owner, "false;false;false;false", "net11.0", parent, out _).ShouldBeFalse();
    }

    private static async Task<(SourceMapping Mapping, TaskItem Reference, string Owner, string Parent)> PrepareAsync(WorkspaceGitFixture fixture)
    {
        SourceMapping original = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string adapter = Path.Combine(fixture.Checkout, "adapter");
        string child = Path.Combine(fixture.Checkout, "fixture");
        _ = Directory.CreateDirectory(adapter);
        _ = Directory.CreateDirectory(child);
        string owner = Path.Combine(adapter, "Adapter.csproj");
        string project = Path.Combine(child, "Fixture.csproj");
        string parent = Path.Combine(fixture.Directory, "Release.targets");
        await File.WriteAllTextAsync(parent, "<Project><PropertyGroup><SharedReleaseImport>true</SharedReleaseImport></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(owner, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(project, "<Project><Import Project=\"$(CustomAfterMicrosoftCommonProps)\"/><PropertyGroup><IsPackable>false</IsPackable><IsPublishable>false</IsPublishable></PropertyGroup><Import Project=\"$(CustomAfterMicrosoftCommonTargets)\"/></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(adapter, "Framework.props"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(adapter, "Fixture.targets"), "<Project><Import Project=\"$(HexalithIsolatedParentCommonTargets)\" Condition=\"'$(HexalithIsolatedParentCommonTargets)' != ''\"/></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string declaration = JsonSerializer.Serialize(new
        {
            schema = "hexalith.isolated-build.v1",
            owner = "adapter/Adapter.csproj",
            projects = _projects,
            roots = _roots,
            framework = "net11.0",
            propertyPrefix = "FixtureOptions",
            properties = new { FixtureOptionsMode = "package" },
            props = "adapter/Framework.props",
            targets = "adapter/Fixture.targets",
        });
        await File.WriteAllTextAsync(Path.Combine(adapter, "contract.json"), declaration, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "adapter", "fixture").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind isolated source graph").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        string buildsCommit = original.PlatformIdentity!.CatalogCommit!;
        SourceMapping mapping = original with
        {
            ManagedAuthority = new ManagedBuildAuthority("hexalith.managed-build.v1", "accepted-delivery", "Example/Fixture", fixture.Checkout, head, buildsCommit, "v6.1.0", buildsCommit, original.EffectiveBuildsRoot, new string('1', 40), new string('2', 64), new string('3', 64), original.PlatformIdentity.Commit, buildsCommit, "v1.0.0", "6.1.0"),
        };
        TaskItem reference = new(project);
        reference.SetMetadata("ReferenceOutputAssembly", "false");
        reference.SetMetadata("HexalithIsolatedBuildContract", "adapter/contract.json");
        reference.SetMetadata("AdditionalProperties", "Configuration=Release;TargetFramework=net11.0;UseHexalithProjectReferences=false;UseNuGetDeps=true;RestoreLockedMode=true;CustomBeforeDirectoryBuildProps=;CustomBeforeDirectoryBuildTargets=;CustomAfterDirectoryBuildTargets=;AfterMicrosoftNETSdkTargets=;CustomAfterMicrosoftCommonProps=" + Path.Combine(adapter, "Framework.props") + ";CustomAfterMicrosoftCommonTargets=" + Path.Combine(adapter, "Fixture.targets") + ";HexalithBuildsRoot=" + mapping.EffectiveBuildsRoot + ";HexalithIsolatedParentCommonTargets=" + parent + ";WarningsNotAsErrors=;ShouldUnsetParentConfigurationAndPlatform=false;FixtureOptionsMode=package");
        return (mapping, reference, owner, parent);
    }
}
