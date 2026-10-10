// <copyright file="PlatformIdentityTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies local Platform identity resolution and mode decisions.</summary>
public sealed class PlatformIdentityTests
{
    /// <summary>Verifies a tagged and aligned workspace carries one hashed identity.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task MatchingWorkspaceCarriesIdentityInHashAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);

        _ = mapping.PlatformIdentity.ShouldNotBeNull();
        mapping.PlatformIdentity.ReleaseTag.ShouldBe("v1.0.0");
        mapping.PlatformIdentity.CatalogCommit.ShouldNotBeNullOrWhiteSpace();
        mapping.PlatformIdentity.ToolVersionRange.ShouldBe("[0.0.0,)");
        mapping.IdentityFindings.ShouldBeEmpty();
        (mapping with { PlatformIdentity = mapping.PlatformIdentity with { ReleaseTag = "v2.0.0" } }).ContentHash.ShouldNotBe(mapping.ContentHash);
    }

    /// <summary>Verifies several valid tags produce a stable release tag independent of Git's tag sort setting.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MultipleReleaseTagsChooseStableTagAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "v2.0.0").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "v0.9.0").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "config", "tag.sort", "-version:refname").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.PlatformIdentity.ShouldNotBeNull().ReleaseTag.ShouldBe("v0.9.0");
    }

    /// <summary>Verifies the Platform repository uses HEAD without a tag or clean-root requirement.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PlatformRepositoryUsesHeadAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        await File.WriteAllTextAsync(Path.Combine(platform, "local-change.txt"), "dirty", TestContext.Current.CancellationToken).ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(Path.Combine(platform, "hexalith-platform.json"), WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        _ = mapping.PlatformIdentity.ShouldNotBeNull();
        mapping.PlatformIdentity.IsPlatformRepository.ShouldBeTrue();
        mapping.PlatformIdentity.ReleaseTag.ShouldBeNull();
        mapping.PlatformIdentity.Commit.ShouldBe(mapping.PlatformIdentity.CompositionCommit);
        mapping.IdentityFindings.ShouldBeEmpty();
    }

    /// <summary>Verifies staged composition differs from the committed identity without moving the checkout.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task StagedPlatformBumpWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        await File.WriteAllTextAsync(Path.Combine(platform, "next.txt"), "next", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "next.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "next Platform").ConfigureAwait(true);
        string next = (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Platform").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW008" && finding.Message.Contains(next, StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW008");
        (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(next);
    }

    /// <summary>Verifies dirty Builds and Platform, and a missing tag, are all reported together.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SeveralConditionsAreCollectedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "-d", "v1.0.0").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "config", "status.showUntrackedFiles", "no").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "config", "status.showUntrackedFiles", "no").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(platform, "dirty.txt"), "dirty", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(builds, "dirty.txt"), "dirty", TestContext.Current.CancellationToken).ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.Count.ShouldBeGreaterThanOrEqualTo(3);
        refusal.Diagnostics.Count.ShouldBeGreaterThanOrEqualTo(3);
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW011" && diagnostic.Hint!.Contains("Fetch Platform tags", StringComparison.Ordinal));
    }

    /// <summary>Verifies the staged Builds gitlink must equal the Platform catalog pin.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task StagedBuildsBumpWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        await File.WriteAllTextAsync(Path.Combine(builds, "next.txt"), "next", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "add", "next.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "next catalog").ConfigureAwait(true);
        string next = (await WorkspaceGitFixture.RunGitAsync(builds, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Builds").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains(next, StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
    }

    /// <summary>Verifies an absent direct Builds checkout is reported against the Platform catalog pin.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AbsentBuildsWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "--file", gitmodules, "--remove-section", "submodule.references/Hexalith.Builds").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--force-remove", "references/Hexalith.Builds").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-am", "omit Builds reference").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains("absent", StringComparison.Ordinal)
            && finding.Message.Contains(source.PlatformIdentity!.CatalogCommit!, StringComparison.Ordinal) && finding.Path == builds);
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("absent", StringComparison.Ordinal));
        File.Exists(Path.Combine(builds, ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a valid range excluding the running tool warns or refuses by mode.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task OutOfRangeToolWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string record = Path.Combine(platform, "hexalith-platform.json");
        await File.WriteAllTextAsync(record, "{\"schemaVersion\":1,\"toolVersionRange\":\"[9999.0.0,)\"}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "exclude running tool").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW010" && finding.Message.Contains(PlatformIdentityResolver.ToolVersion, StringComparison.Ordinal)
            && finding.Message.Contains("[9999.0.0,)", StringComparison.Ordinal) && finding.Message.Contains(record, StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW010" && diagnostic.Message.Contains("[9999.0.0,)", StringComparison.Ordinal));
    }

    /// <summary>Verifies the running CLI assembly, with a version different from Tooling, controls HXW010.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ExecutableCliVersionControlsToolRangeAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withDirectManifest: true, toolVersionRange: "[0.0.0,2.0.0)").ConfigureAwait(true);
        string manifest = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "module.json");
        _ = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string cliOutput = Path.Combine(fixture.Directory, "divergent-cli");
        _ = Directory.CreateDirectory(cliOutput);
        File.Copy(typeof(PlatformIdentityResolver).Assembly.Location, Path.Combine(cliOutput, "Hexalith.Builds.Tooling.dll"));
        string repository = CompositionTestFiles.RepositoryRoot();
        string project = Path.Combine(repository, "src", "libraries", "Hexalith.Builds.Module.Cli", "Hexalith.Builds.Module.Cli.csproj");
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "-c", "Debug", "--no-restore", "--no-dependencies", "-p:Version=9.9.9", "-p:OutputPath=" + cliOutput, "-v:q"], repository, null),
            TimeSpan.FromMinutes(2),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        build.ExitCode.ShouldBe(0, build.Output + build.ErrorOutput);

        CompositionProcessResult run = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", [Path.Combine(cliOutput, "Hexalith.Builds.Module.Cli.dll"), "run", "--manifest", manifest, "--mode", "package", "--output", "json"], fixture.Checkout, null),
            TimeSpan.FromMinutes(1),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        run.ExitCode.ShouldNotBe(0);
        (run.Output + run.ErrorOutput).ShouldContain("HXW010");
        run.Output.ShouldContain("9.9.9");
        PlatformIdentityResolver.ToolVersion.ShouldNotBe("9.9.9");
    }

    /// <summary>Verifies a missing tool range warns in source mode and refuses package mode.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingToolRangeWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string record = Path.Combine(platform, "hexalith-platform.json");
        await File.WriteAllTextAsync(record, "{\"schemaVersion\":1}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "defer tool range").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW010" && finding.Message.Contains("unavailable", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW010");
    }

    /// <summary>Verifies a missing direct Platform declaration fails before initializing any checkout.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task MissingPlatformReferenceFailsBeforeInitializationAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "--file", gitmodules, "--remove-section", "submodule.references/Hexalith.Platform").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostic.RuleId.ShouldBe("HXW007");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a missing Platform declaration retains an independent dirty Builds finding.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task MissingPlatformReferenceKeepsDirtyBuildsAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        await File.WriteAllTextAsync(Path.Combine(builds, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "--file", gitmodules, "--remove-section", "submodule.references/Hexalith.Platform").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW007" && diagnostic.Message.Contains("no direct", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("dirty", StringComparison.Ordinal));
    }

    /// <summary>Verifies the tool version seam and strict record fields.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ToolRangeAndUnknownRecordFieldsAreRejectedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        IReadOnlyList<string> references = await WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken).ConfigureAwait(true);
        IReadOnlyDictionary<string, string> recorded = await SubmoduleInitializer.ValidateRecordedGitlinksAsync(fixture.Checkout, references, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (_, IReadOnlyList<PlatformIdentityFinding> outOfRange) = await PlatformIdentityResolver.ResolveAsync(fixture.Checkout, references, recorded, TestContext.Current.CancellationToken, "unavailable").ConfigureAwait(true);
        outOfRange.ShouldContain(finding => finding.RuleId == "HXW010");

        string platform = Path.Combine(fixture.Directory, "platform");
        await File.WriteAllTextAsync(Path.Combine(platform, "hexalith-platform.json"), "{\"schemaVersion\":1,\"unknown\":true}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "invalid record").ConfigureAwait(true);
        SourceMapping invalid = await WorkspaceRootResolver.ResolveAsync(Path.Combine(platform, "hexalith-platform.json"), WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        invalid.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW007");
        mapping.ContentHash.ShouldNotBe(invalid.ContentHash);
    }

    /// <summary>Verifies equivalent JSON numbers are accepted as schema version one.</summary>
    /// <param name="schemaVersion">A numeric representation of one.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("10e-1")]
    [InlineData("0.100e1")]
    public async Task NumericSchemaVersionOneIsAcceptedAsync(string schemaVersion)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string record = Path.Combine(platform, "hexalith-platform.json");
        await File.WriteAllTextAsync(record, $"{{\"schemaVersion\":{schemaVersion},\"toolVersionRange\":\"[0.0.0,)\"}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "numeric schema version").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.IdentityFindings.ShouldBeEmpty();
    }

    /// <summary>Verifies a root marker cannot bypass a declared direct Platform reference.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MarkerWithPlatformReferenceUsesGitlinkIdentityAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "hexalith-platform.json"), "{\"schemaVersion\":1}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "add misleading marker").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.PlatformIdentity.ShouldNotBeNull().IsPlatformRepository.ShouldBeFalse();
        mapping.PlatformIdentity.ReleaseTag.ShouldBe("v1.0.0");
    }

    /// <summary>Verifies a declared Builds reference absent from the index joins other identity findings.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingStagedBuildsGitlinkIsCollectedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--force-remove", "references/Hexalith.Builds").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains("staged absent", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("staged absent", StringComparison.Ordinal));
    }

    /// <summary>Verifies a numeric prerelease identifier with a leading zero is not a release tag.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InvalidSemVerTagIsRejectedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "-d", "v1.0.0").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "v1.0.0-01").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW011" && finding.Message.Contains("release tag", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW011");
    }

    /// <summary>Verifies a symlink marker does not turn a module into the Platform repository.</summary>
    /// <param name="mode">The selected mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task SymlinkMarkerDoesNotReplacePlatformReferenceAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "--file", gitmodules, "--remove-section", "submodule.references/Hexalith.Platform").ConfigureAwait(true);
        string marker = Path.Combine(fixture.Checkout, "hexalith-platform.json");
        _ = File.CreateSymbolicLink(marker, "module.json");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "add symlink marker").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostic.RuleId.ShouldBe("HXW007");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Builds", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies unreadable identity objects retain independent catalog and checkout findings.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingIdentityObjectKeepsIndependentFindingsAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        await File.WriteAllTextAsync(Path.Combine(platform, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(builds, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string missingCommit = new('d', 40);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--cacheinfo", "160000," + missingCommit + ",references/Hexalith.Platform").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "point at missing Platform object").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW007");
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains("dirty", StringComparison.Ordinal));
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW011" && finding.Message.Contains("changes", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW011");
    }

    /// <summary>Verifies malformed NuGet range syntax is rejected by the committed record reader.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InvalidToolRangeIsUnreadableIdentityAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string record = Path.Combine(platform, "hexalith-platform.json");
        await File.WriteAllTextAsync(record, "{\"schemaVersion\":1,\"toolVersionRange\":\"[bad,14)\"}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "invalid range").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW007" && finding.Message.Contains("toolVersionRange", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW007");
    }

    /// <summary>Verifies a missing committed gitlink does not hide an independently dirty Builds checkout.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingCommittedPlatformGitlinkKeepsCatalogFindingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        string platformHead = (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--force-remove", "references/Hexalith.Platform").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "remove committed Platform gitlink").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--add", "--cacheinfo", "160000," + platformHead + ",references/Hexalith.Platform").ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        await File.WriteAllTextAsync(Path.Combine(builds, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW007" && finding.Message.Contains("missing from HEAD", StringComparison.Ordinal));
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains("dirty", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
    }

    /// <summary>Verifies one failed pinned catalog Git read produces one HXW007 finding.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnreadablePinnedCatalogHasOneGitFindingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string missing = new('f', 40);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "update-index", "--add", "--cacheinfo", "160000," + missing + ",references/Hexalith.Builds").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "point at unavailable Builds commit").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(Path.Combine(platform, "hexalith-platform.json"), WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        source.IdentityFindings.Count(finding => finding.RuleId == "HXW007" && finding.Message.Contains("show", StringComparison.Ordinal)).ShouldBe(1);
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains(missing, StringComparison.Ordinal));
    }

    /// <summary>Verifies a failed direct initialization does not hide independently observable identity findings.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task FailedBuildsInitializationKeepsOtherIdentityFindingsAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Platform").ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        await File.WriteAllTextAsync(Path.Combine(platform, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "submodule.references/Hexalith.Builds.url", "file:///nonexistent/hexalith-builds").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW011" && diagnostic.Message.Contains("changes", StringComparison.Ordinal));
        refusal.Diagnostics.Count.ShouldBeGreaterThanOrEqualTo(2);
    }
}
