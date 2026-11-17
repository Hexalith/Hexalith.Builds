// <copyright file="PlatformIdentityTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies local Platform identity resolution and mode decisions.</summary>
public sealed class PlatformIdentityTests
{
    /// <summary>Verifies successive owned producer selections materialize their catalog without changing Platform.</summary>
    /// <returns>A task for the mapping and catalog assertions.</returns>
    [Fact]
    public async Task OwnedProducerBumpsMaterializeActualSelectedCatalogAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(toolVersionRange: "[6.0.5,7.0.0-0)").ConfigureAwait(true);
        SourceMapping baseline = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        string platformSource = (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        string nested = (await WorkspaceGitFixture.RunGitAsync(platform, "ls-tree", "HEAD", "--", "references/Hexalith.Builds").ConfigureAwait(true)).Output;
        string config = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        foreach (string version in new[] { "6.0.7", "6.0.8" })
        {
            await File.WriteAllTextAsync(Path.Combine(builds, "Props", "Directory.Packages.props"), $"<Project><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"{version}\" /></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(builds, "add", "Props/Directory.Packages.props").ConfigureAwait(true);
            string source = await CommitAsync(builds, "fix: next synthetic producer").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(builds, "tag", "v" + version).ConfigureAwait(true);
            string document = """{"schema":"hexalith.ci.v1","buildsTag":"vVERSION","solution":"Fixture.slnx","dapr":false,"browsers":false,"registry":false,"codeql":[],"tests":{"integration":[],"exclude":{}},"hooks":[],"jobs":{},"submodules":[]}""".Replace("VERSION", version, StringComparison.Ordinal);
            await File.WriteAllTextAsync(config, document, TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json", "references/Hexalith.Builds").ConfigureAwait(true);
            _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "ci: select exact producer").ConfigureAwait(true);
            Dictionary<string, string> recorded = new(StringComparer.Ordinal)
            {
                ["references/Hexalith.Builds"] = source,
                ["references/Hexalith.Platform"] = platformSource,
            };
            var (identity, findings) = await PlatformIdentityResolver.ResolveAsync(fixture.Checkout, ["references/Hexalith.Builds", "references/Hexalith.Platform"], recorded, TestContext.Current.CancellationToken, version + "+" + source).ConfigureAwait(true);
            findings.ShouldBeEmpty();
            identity.ShouldNotBeNull().CatalogCommit.ShouldBe(source);
            identity.ToolVersionRange.ShouldBe("[6.0.5,7.0.0-0)");
            string workspace = Path.Combine(fixture.Directory, "owned-" + version);
            await SourceMappingMaterializer.WriteAsync(baseline with { PlatformIdentity = identity, IdentityFindings = findings }, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            using JsonDocument snapshot = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(workspace, "source-mapping", "catalog-versions.json"), TestContext.Current.CancellationToken).ConfigureAwait(true));
            snapshot.RootElement.GetProperty("CatalogCommit").GetString().ShouldBe(source);
            snapshot.RootElement.GetProperty("Versions").GetProperty("Hexalith.Dep").GetString().ShouldBe(version);
            (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(platformSource);
            (await WorkspaceGitFixture.RunGitAsync(platform, "ls-tree", "HEAD", "--", "references/Hexalith.Builds").ConfigureAwait(true)).Output.ShouldBe(nested);
        }
    }

    /// <summary>Verifies invalid present owned configuration cannot select the historical nested producer.</summary>
    /// <param name="content">The invalid committed configuration.</param>
    /// <returns>A task for the refusal assertion.</returns>
    [Theory]
    [InlineData("{")]
    [InlineData("{}")]
    [InlineData("{\"schema\":\"hexalith.ci.v1\",\"schema\":\"hexalith.ci.v1\"}")]
    public async Task InvalidOwnedConfigurationNeverFallsBackToNestedProducerAsync(string content)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "ci: invalid configuration fixture").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
    }

    /// <summary>Verifies staged configuration cannot be hidden by canceling its worktree bytes.</summary>
    /// <returns>A task for the independent index refusal.</returns>
    [Fact]
    public async Task OwnedConfigurationStagedCancellationRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string original = "{}";
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json").ConfigureAwait(true);
        _ = await CommitAsync(fixture.Checkout, "ci: committed configuration fixture").ConfigureAwait(true);
        await File.WriteAllTextAsync(path, "{ }", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json").ConfigureAwait(true);
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "diff", "--name-only", "HEAD", "--", ".github/hexalith-ci.json").ConfigureAwait(true)).Output.ShouldBeEmpty();
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => ConsumerBuildsIdentity.ResolveAsync(fixture.Checkout, new Dictionary<string, string>(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Diagnostics.ShouldContain(item => item.Message.Contains("differs from HEAD or its index", StringComparison.Ordinal));
    }

    /// <summary>Verifies non-file owned path entries cannot activate the historical fallback.</summary>
    /// <param name="symlink">Whether the otherwise absent path is a dangling symlink.</param>
    /// <returns>A task for the present-path refusal.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnedConfigurationDirectoryOrDanglingLinkRefusesAsync(bool symlink)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (symlink)
        {
            _ = File.CreateSymbolicLink(path, Path.Combine(fixture.Directory, "absent.json"));
        }
        else
        {
            _ = Directory.CreateDirectory(path);
        }

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => ConsumerBuildsIdentity.ResolveAsync(fixture.Checkout, new Dictionary<string, string>(), TestContext.Current.CancellationToken)).ConfigureAwait(true);
        refusal.Diagnostics.ShouldContain(item => item.Message.Contains("committed regular blob", StringComparison.Ordinal));
    }

    /// <summary>Verifies actual owned bytes and file kinds despite hidden index entries.</summary>
    /// <param name="flag">The index flag concealing actual worktree state.</param>
    /// <returns>A task for actual Git and regular-file admission controls.</returns>
    [Theory]
    [InlineData("assume-unchanged")]
    [InlineData("skip-worktree")]
    public async Task ActualOwnedConfigurationBytesAndKindsRefuseAsync(string flag)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        string source = (await WorkspaceGitFixture.RunGitAsync(builds, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "tag", "v6.0.7").ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        const string content = """{"schema":"hexalith.ci.v1","buildsTag":"v6.0.7","solution":"Fixture.slnx","dapr":false,"browsers":false,"registry":false,"codeql":[],"tests":{"integration":[],"exclude":{}},"hooks":[],"jobs":{},"submodules":[]}""" + "\n";
        await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, ".gitattributes"), ".github/hexalith-ci.json text eol=crlf\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".gitattributes", ".github/hexalith-ci.json").ConfigureAwait(true);
        _ = await CommitAsync(fixture.Checkout, "ci: actual owned byte fixture").ConfigureAwait(true);
        Dictionary<string, string> recorded = new(StringComparer.Ordinal) { ["references/Hexalith.Builds"] = source };
        async Task<string?> ResolveAsync() => await ConsumerBuildsIdentity.ResolveAsync(fixture.Checkout, recorded, TestContext.Current.CancellationToken, "6.0.7+" + source).ConfigureAwait(true);
        (await ResolveAsync().ConfigureAwait(true)).ShouldBe(source);
        await File.WriteAllTextAsync(path, content.Replace("\n", "\r\n", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        (await ResolveAsync().ConfigureAwait(true)).ShouldBe(source);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--" + flag, ".github/hexalith-ci.json").ConfigureAwait(true);
        foreach (string kind in new[] { "drift", "missing", "symlink", "directory", "fifo" })
        {
            if (kind == "fifo" && OperatingSystem.IsWindows())
            {
                continue;
            }

            File.Delete(path);
            if (kind == "drift")
            {
                await File.WriteAllTextAsync(path, content.Replace("Fixture", "Changed", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
            }
            else if (kind == "symlink")
            {
                string target = Path.Combine(fixture.Directory, "unchanged-config.json");
                await File.WriteAllTextAsync(target, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
                _ = File.CreateSymbolicLink(path, target);
            }
            else if (kind == "directory")
            {
                _ = Directory.CreateDirectory(path);
            }
            else if (kind == "fifo")
            {
                CompositionProcessResult created = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("mkfifo", [path], fixture.Checkout, null), TimeSpan.FromSeconds(5), 1024, TestContext.Current.CancellationToken).ConfigureAwait(true);
                created.ExitCode.ShouldBe(0);
            }

            WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(ResolveAsync).ConfigureAwait(true);
            refusal.Diagnostics.ShouldContain(item => item.Message.Contains("actual owned CI configuration", StringComparison.Ordinal));
            if (kind == "directory")
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }

            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        (await ResolveAsync().ConfigureAwait(true)).ShouldBe(source);
    }

    /// <summary>Verifies a valid owned catalog refuses a Tooling assembly built by another producer.</summary>
    /// <returns>A task for the executing-source refusal assertion.</returns>
    [Fact]
    public async Task OwnedProducerRefusesDifferentExecutingToolingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "tag", "v6.0.7").ConfigureAwait(true);
        string path = Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, """{"schema":"hexalith.ci.v1","buildsTag":"v6.0.7","solution":"Fixture.slnx","dapr":false,"browsers":false,"registry":false,"codeql":[],"tests":{"integration":[],"exclude":{}},"hooks":[],"jobs":{},"submodules":[]}""", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", "ci: select synthetic owned producer").ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("executing Builds Tooling version/source", StringComparison.Ordinal));
    }

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
        string cliIntermediate = Path.Combine(fixture.Directory, "divergent-cli-obj") + Path.DirectorySeparatorChar;
        _ = Directory.CreateDirectory(cliOutput);
        File.Copy(typeof(PlatformIdentityResolver).Assembly.Location, Path.Combine(cliOutput, "Hexalith.Builds.Tooling.dll"));
        _ = Directory.CreateDirectory(Path.Combine(cliIntermediate, "ref"));
        File.Copy(typeof(PlatformIdentityResolver).Assembly.Location, Path.Combine(cliIntermediate, "ref", "Hexalith.Builds.Tooling.dll"));
        string repository = CompositionTestFiles.RepositoryRoot();
        string project = Path.Combine(repository, "src", "libraries", "Hexalith.Builds.Module.Cli", "Hexalith.Builds.Module.Cli.csproj");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        CompositionProcessResult build = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", ["build", project, "-c", configuration, "--no-restore", "--no-dependencies", "-p:Version=9.9.9", "-p:OutputPath=" + cliOutput, "-p:IntermediateOutputPath=" + cliIntermediate, "-v:q"], repository, null),
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

        ToolFailureCategory expectedCategory = mode == WorkspaceMode.Source ? ToolFailureCategory.None : ToolFailureCategory.PrerequisiteUnavailable;
        refusal.Diagnostic.RuleId.ShouldBe("HXW007");
        refusal.Diagnostic.Message.ShouldContain("no direct");
        refusal.Diagnostic.Category.ShouldBe(ToolFailureCategory.PrerequisiteUnavailable);
        refusal.Diagnostics.Count(diagnostic => diagnostic.RuleId == "HXW007" && diagnostic.Message.Contains("no direct", StringComparison.Ordinal)).ShouldBe(1);
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("dirty", StringComparison.Ordinal)
            && diagnostic.Category == expectedCategory);
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

    /// <summary>Verifies a Platform checkout moved without staging warns or refuses against the staged gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MovedUnstagedPlatformCheckoutWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        string next = await CommitAsync(platform, "next Platform").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW008" && finding.Message.Contains(next, StringComparison.Ordinal)
            && finding.Message.Contains("staged composition gitlink", StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW008" && diagnostic.Message.Contains("staged composition gitlink", StringComparison.Ordinal));
        (await WorkspaceGitFixture.RunGitAsync(platform, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(next);
    }

    /// <summary>Verifies a Builds checkout moved without staging warns or refuses against the staged gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MovedUnstagedBuildsCheckoutWarnsOrRefusesAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        string next = await CommitAsync(builds, "next catalog").ConfigureAwait(true);

        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009" && finding.Message.Contains("checked out " + next, StringComparison.Ordinal));
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009" && diagnostic.Message.Contains("checked out " + next, StringComparison.Ordinal));
        (await WorkspaceGitFixture.RunGitAsync(builds, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(next);
    }

    /// <summary>Verifies a source mapping carrying identity findings reloads with the command-resolved hash.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SourceMappingWithFindingsLoadsVerifiedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(toolVersionRange: "[9999.0.0,)").ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        mapping.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW010");
        string workspace = Path.Combine(fixture.Directory, "run");

        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping loaded = SourceMapping.LoadVerified(Path.Combine(workspace, "source-mapping", "mapping.json"), mapping.ContentHash);

        loaded.ContentHash.ShouldBe(mapping.ContentHash);
        loaded.PlatformIdentity.ShouldBe(mapping.PlatformIdentity);
        loaded.IdentityFindings.ShouldBe(mapping.IdentityFindings);
    }

    /// <summary>Verifies records whose schema version is not one are unreadable identities.</summary>
    /// <param name="schemaVersion">A JSON value other than one.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("2")]
    [InlineData("10")]
    [InlineData("0.1")]
    [InlineData("11e-1")]
    [InlineData("1e1")]
    [InlineData("-1")]
    [InlineData("\"1\"")]
    public async Task OtherSchemaVersionIsUnreadableIdentityAsync(string schemaVersion)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string platform = Path.Combine(fixture.Directory, "platform");
        string record = Path.Combine(platform, "hexalith-platform.json");
        await File.WriteAllTextAsync(record, $"{{\"schemaVersion\":{schemaVersion},\"toolVersionRange\":\"[0.0.0,)\"}}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "add", "hexalith-platform.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "commit", "-m", "other schema version").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(record, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW007" && finding.Message.Contains("expected schemaVersion 1", StringComparison.Ordinal));
    }

    /// <summary>Verifies build metadata containing a hyphen does not make a release tag invalid.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task BuildMetadataWithHyphenIsReleaseTagAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "-d", "v1.0.0").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(platform, "tag", "v1.0.0+build-01").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.PlatformIdentity.ShouldNotBeNull().ReleaseTag.ShouldBe("v1.0.0+build-01");
        mapping.IdentityFindings.ShouldBeEmpty();
    }

    /// <summary>Verifies source-mode resolver failures keep identity findings as warnings.</summary>
    /// <param name="failingReference">The direct reference whose checkout cannot be initialized.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData("Hexalith.Builds")]
    [InlineData("Hexalith.Dep")]
    public async Task SourceResolverFailureKeepsIdentityWarningsAsync(string failingReference)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Platform").ConfigureAwait(true);
        string platform = Path.Combine(fixture.Checkout, "references", "Hexalith.Platform");
        await File.WriteAllTextAsync(Path.Combine(platform, "dirty.txt"), "changed", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", $"submodule.references/{failingReference}.url", "file:///nonexistent/" + failingReference).ConfigureAwait(true);

        WorkspaceMappingException refusal = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        refusal.Diagnostic.RuleId.ShouldBe("HXW004");
        refusal.Diagnostic.Message.ShouldContain(failingReference);
        refusal.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW011" && diagnostic.Message.Contains("changes", StringComparison.Ordinal)
            && diagnostic.Category == ToolFailureCategory.None);
    }

    private static async Task<string> CommitAsync(string checkout, string message)
    {
        await File.WriteAllTextAsync(Path.Combine(checkout, "next.txt"), message, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(checkout, "add", "next.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(checkout, "-c", "user.name=Workspace Test", "-c", "user.email=workspace@example.invalid", "commit", "-m", message).ConfigureAwait(true);
        return (await WorkspaceGitFixture.RunGitAsync(checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
    }
}
