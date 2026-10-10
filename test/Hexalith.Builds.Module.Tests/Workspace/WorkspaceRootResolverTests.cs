// <copyright file="WorkspaceRootResolverTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Globalization;
using System.Text;

using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies active-root mapping and safe direct-submodule initialization.</summary>
public sealed class WorkspaceRootResolverTests
{
    /// <summary>Verifies direct declaration casing follows the containing filesystem.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DirectReferencesComponentUsesFilesystemCasingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(
            gitmodules,
            declarations.Replace("path = references/Hexalith.Dep", "path = References/Hexalith.Dep", StringComparison.Ordinal),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        IReadOnlyList<string> references = await WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken).ConfigureAwait(true);

        if (Directory.Exists(Path.Combine(fixture.Checkout, "References")))
        {
            references.ShouldBe(["References/Hexalith.Dep"]);
        }
        else
        {
            references.ShouldBeEmpty();
        }
    }

    /// <summary>Verifies a missing parent directory does not hide a missing root manifest from classification.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingRootManifestParentStillClassifiesAsRootAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string manifest = Path.Combine(fixture.Checkout, "missing", "child", "module.json");

        bool isRoot = await WorkspaceRootResolver.IsRootManifestAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true);

        isRoot.ShouldBeTrue();
    }

    /// <summary>Verifies missing Git falls back only when no Git marker claims the workspace.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UnavailableGitUsesOnlyStandaloneFallbackAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            string manifest = Path.Combine(directory, "module.json");
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult unavailable = new(false, -1, string.Empty, false);

            SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, unavailable, TestContext.Current.CancellationToken).ConfigureAwait(true);

            mapping.HasGit.ShouldBeFalse();
            mapping.Root.ShouldBe(directory);

            await File.WriteAllTextAsync(Path.Combine(directory, ".git"), "gitdir: missing", TestContext.Current.CancellationToken).ConfigureAwait(true);
            WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, unavailable, TestContext.Current.CancellationToken)).ConfigureAwait(true);
            error.Diagnostic.RuleId.ShouldBe("HXW001");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies a bounded Git config listing cannot hide later declarations.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task TruncatedGitmodulesOutputFailsClosedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        StringBuilder declarations = new(await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true));
        for (int index = 0; index < 1800; index++)
        {
            _ = declarations.Append("[submodule \"unrelated-").Append(index).AppendLine("\"]")
                .Append("\tpath = unrelated/").Append(index).AppendLine();
        }

        await File.WriteAllTextAsync(gitmodules, declarations.ToString(), TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
    }

    /// <summary>Verifies a bounded index listing cannot hide a tracked direct gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task TruncatedTrackedIndexOutputFailsClosedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rm", "--cached", "--", ".gitmodules").ConfigureAwait(true);
        File.Delete(Path.Combine(fixture.Checkout, ".gitmodules"));
        string references = Path.Combine(fixture.Checkout, "references");
        for (int index = 0; index < 600; index++)
        {
            string file = Path.Combine(references, "A" + index.ToString("D4", CultureInfo.InvariantCulture) + "-" + new string('x', 80));
            await File.WriteAllTextAsync(file, "index filler", TestContext.Current.CancellationToken).ConfigureAwait(true);
        }

        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "--", "references").ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
    }

    /// <summary>Verifies Git-quoted unusual gitlinks remain visible when declarations are missing.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingGitmodulesDetectsQuotedTrackedGitlinkAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rm", "--cached", "--", ".gitmodules", "references/Hexalith.Dep").ConfigureAwait(true);
        File.Delete(Path.Combine(fixture.Checkout, ".gitmodules"));
        string hash = (await WorkspaceGitFixture.RunGitAsync(Path.Combine(fixture.Directory, "dependency"), "rev-parse", "HEAD").ConfigureAwait(true)).Output.TrimEnd('\r', '\n');
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-index", "--add", "--cacheinfo", $"160000,{hash},references/odd\"name").ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain(".gitmodules");
    }

    /// <summary>Verifies a dangling declaration symlink is a broken Git marker, not an empty declaration set.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DanglingGitmodulesMarkerFailsClosedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string marker = Path.Combine(fixture.Checkout, ".gitmodules");
        File.Delete(marker);
        _ = File.CreateSymbolicLink(marker, Path.Combine(fixture.Directory, "missing-declarations"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ReadDirectReferencesAsync(fixture.Checkout, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain(marker);
    }

    /// <summary>Verifies both modes require a real stage-zero gitlink for each declaration.</summary>
    /// <param name="mode">The selected tool mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task DeclaredReferenceWithoutIndexedGitlinkFailsAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rm", "--cached", "--", "references/Hexalith.Dep").ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
        error.Diagnostic.Message.ShouldContain("stage-zero gitlink");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a legal Git subsection name with spaces preserves its declared path.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SpaceContainingGitSubsectionKeepsDirectReferenceAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(gitmodules, declarations.Replace("[submodule \"references/Hexalith.Dep\"]", "[submodule \"a direct reference with spaces\"]", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Entries.ShouldContain(new SourceMappingEntry("Hexalith.Dep", "package", null));
    }

    /// <summary>Verifies root declarations cannot be supplied by an external symlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ExternalGitmodulesSymlinkFailsBeforeCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string external = Path.Combine(fixture.Directory, "external.gitmodules");
        File.Move(gitmodules, external);
        _ = File.CreateSymbolicLink(gitmodules, external);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain(gitmodules);
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a mixed-separator declaration is never interpreted as a direct path.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MixedSeparatorDirectReferenceFailsAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(gitmodules, declarations.Replace("path = references/Hexalith.Dep", "path = references/Hexalith\\Dep", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
    }

    /// <summary>Verifies a manifest symlink cannot escape the physical active root.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ManifestSymlinkOutsideRootFailsResolutionAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string external = Path.Combine(fixture.Directory, "external-module.json");
        await File.WriteAllTextAsync(external, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string alias = Path.Combine(fixture.Checkout, "alias-module.json");
        _ = File.CreateSymbolicLink(alias, external);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(alias, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW001");
        error.Diagnostic.Message.ShouldContain(alias);
        error.Diagnostic.Message.ShouldContain(fixture.Checkout);
    }

    /// <summary>Verifies a non-Git manifest symlink cannot escape its active module.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NonGitManifestSymlinkOutsideRootFailsResolutionAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            string external = Path.Combine(Path.GetDirectoryName(directory)!, "external-" + Guid.NewGuid().ToString("N") + ".json");
            await File.WriteAllTextAsync(external, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            try
            {
                string alias = Path.Combine(directory, "module.json");
                _ = File.CreateSymbolicLink(alias, external);
                WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(alias, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);
                error.Diagnostic.RuleId.ShouldBe("HXW001");
            }
            finally
            {
                File.Delete(external);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies tracked direct gitlinks cannot disappear with a missing working-tree .gitmodules.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingTrackedGitmodulesFailsBeforeCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        File.Delete(Path.Combine(fixture.Checkout, ".gitmodules"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain(".gitmodules");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a symlink alias to a package-origin reference is excluded from the active root.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task AliasToPackageOnlyReferenceIsNotMappedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string project = Path.Combine(direct, "Dep.csproj");
        await File.WriteAllTextAsync(project, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string alias = Path.Combine(fixture.Checkout, "alias");
        _ = Directory.CreateSymbolicLink(alias, direct);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.ContainsProject(Path.Combine(alias, "Dep.csproj")).ShouldBeFalse();
    }

    /// <summary>Verifies an independent repository under a direct checkout is not reported as a submodule.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task IndependentNestedGitRepositoryIsAllowedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string independent = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "independent");
        _ = Directory.CreateDirectory(independent);
        _ = await WorkspaceGitFixture.RunGitAsync(independent, "init", "-b", "main").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Entries.ShouldContain(entry => entry.Identity == "Hexalith.Dep" && entry.Origin == "source");
    }

    /// <summary>Verifies a stale nested gitfile is found when the direct checkout marker is absent.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task StaleNestedSubmoduleWithoutDirectMarkerFailsPreflightAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        File.Delete(Path.Combine(direct, ".git"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(Path.Combine(direct, "nested", "Other"));
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a dangling nested Git marker fails before another direct checkout begins.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DanglingNestedGitSymlinkFailsPreflightAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string nested = Path.Combine(direct, "nested", "Other");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        File.Delete(Path.Combine(nested, ".git"));
        _ = File.CreateSymbolicLink(Path.Combine(nested, ".git"), Path.Combine(fixture.Directory, "missing-git-directory"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
        error.Diagnostic.Message.ShouldContain(nested);
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies nested directory enumeration failures retain the direct identity and nested path.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NestedEnumerationFailureUsesHxw003ContextAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            string direct = Path.Combine(root, "references", "Hexalith.Dep");

            WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(
                () => SubmoduleInitializer.CheckNestedAsync(root, direct, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            error.Diagnostic.RuleId.ShouldBe("HXW003");
            error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
            error.Diagnostic.Message.ShouldContain(direct);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>Verifies a Git directory with trailing whitespace is not mistaken for the modules directory.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task NestedGitfilePreservesTrailingDirectoryWhitespaceAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Windows does not support trailing spaces in directory names consistently.");
        }

        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string independent = Path.Combine(direct, "independent");
        string separateGitDirectory = Path.Combine(fixture.Checkout, ".git", "modules ");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "init", "--separate-git-dir", separateGitDirectory, independent).ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Entries.ShouldContain(entry => entry.Identity == "Hexalith.Dep" && entry.Origin == "source");
    }

    /// <summary>Verifies certificate and key variables reach the spawned Git process without network access.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task GitProcessReceivesClientCertificateEnvironmentAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string? originalCertificate = Environment.GetEnvironmentVariable("GIT_SSL_CERT");
        string? originalKey = Environment.GetEnvironmentVariable("GIT_SSL_KEY");
        const string certificate = "hexalith-fixture-client-cert";
        const string key = "hexalith-fixture-client-key";
        try
        {
            Environment.SetEnvironmentVariable("GIT_SSL_CERT", certificate);
            Environment.SetEnvironmentVariable("GIT_SSL_KEY", key);

            CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(
                fixture.Checkout,
                TestContext.Current.CancellationToken,
                "-c",
                "alias.capture-environment=!env",
                "capture-environment").ConfigureAwait(true);

            result.Started.ShouldBeTrue();
            result.ExitCode.ShouldBe(0);
            result.OutputTruncated.ShouldBeFalse();
            result.Output.ShouldContain("GIT_SSL_CERT=" + certificate);
            result.Output.ShouldContain("GIT_SSL_KEY=" + key);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_SSL_CERT", originalCertificate);
            Environment.SetEnvironmentVariable("GIT_SSL_KEY", originalKey);
        }
    }

    /// <summary>Verifies source mode initializes the recorded direct gitlink and leaves nested submodules untouched.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task SourceModeInitializesOnlyDirectReferenceAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true).ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string nested = Path.Combine(direct, "nested", "Other");
        File.Exists(Path.Combine(direct, ".git")).ShouldBeFalse();

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Mode.ShouldBe(WorkspaceMode.Source);
        mapping.Root.ShouldBe(fixture.Checkout);
        mapping.ActiveModule.ShouldBe("checkout");
        mapping.Entries.Single(entry => entry.Identity == "Hexalith.Dep").ShouldBe(new SourceMappingEntry("Hexalith.Dep", "source", direct));
        File.Exists(Path.Combine(direct, ".git")).ShouldBeTrue();
        File.Exists(Path.Combine(nested, ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies package mode records package origins without initializing direct sources.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageModeLeavesDirectReferencesUninitializedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Entries.Single(entry => entry.Identity == "Hexalith.Dep").ShouldBe(new SourceMappingEntry("Hexalith.Dep", "package", null));
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a package-origin direct path cannot point outside the active root.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageOriginSymlinkOutsideRootFailsBeforeMappingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string outside = Path.Combine(fixture.Directory, "outside");
        _ = Directory.CreateDirectory(outside);
        Directory.Delete(direct);
        _ = Directory.CreateSymbolicLink(direct, outside);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
        error.Diagnostic.Message.ShouldContain(direct);
        File.Exists(Path.Combine(outside, ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies package mode never identifies a project from a checkout past the recorded gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task PackageOriginStaleCheckoutFailsBeforeMappingAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(direct, "New.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><PackageId>Hexalith.Dep.New</PackageId></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "New.csproj").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "advance package source").ConfigureAwait(true);
        string stale = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW004");
        error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
        error.Diagnostic.Message.ShouldContain(stale);
        (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(stale);
    }

    /// <summary>Verifies a manifest in a direct reference still resolves the outermost superproject.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DirectReferenceManifestUsesOutermostRootAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string manifest = Path.Combine(direct, "module.json");
        await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Root.ShouldBe(fixture.Checkout);
        mapping.ActiveModule.ShouldBe("Hexalith.Dep");
        mapping.Entries.ShouldContain(new SourceMappingEntry("Hexalith.Dep", "source", direct));
    }

    /// <summary>Verifies an initialized direct reference is returned to its recorded gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InitializedDirectReferenceChecksOutRecordedGitlinkAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string recorded = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(direct, "changed.txt"), "new commit", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "changed.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "advance direct source").ConfigureAwait(true);

        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string actual = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        actual.ShouldBe(recorded);
    }

    /// <summary>Verifies a local update=none setting cannot skip the recorded checkout.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UpdateNoneCannotSkipRecordedCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        string recorded = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.name", "Workspace Test").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "config", "user.email", "workspace@example.invalid").ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(direct, "advanced.txt"), "advanced", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "add", "advanced.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "commit", "-m", "advance source").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "submodule.references/Hexalith.Dep.update", "none").ConfigureAwait(true);

        _ = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        string actual = (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        actual.ShouldBe(recorded);
    }

    /// <summary>Verifies a staged gitlink is the update target even before the root commit changes.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task StagedGitlinkIsTheRecordedCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string dependency = Path.Combine(fixture.Directory, "dependency");
        await File.WriteAllTextAsync(Path.Combine(dependency, "next.txt"), "staged", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(dependency, "add", "next.txt").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(dependency, "commit", "-m", "advance dependency").ConfigureAwait(true);
        string expected = (await WorkspaceGitFixture.RunGitAsync(dependency, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "fetch", "origin", "main").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "checkout", expected).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Dep").ConfigureAwait(true);

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Entries.ShouldContain(new SourceMappingEntry("Hexalith.Dep", "source", direct));
        (await WorkspaceGitFixture.RunGitAsync(direct, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim().ShouldBe(expected);
    }

    /// <summary>Verifies an absent nested Git directory still leaves an initialized submodule marker.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task StaleNestedGitfileWithDeletedGitDirectoryStopsPreflightAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        string marker = Path.Combine(direct, "nested", "Other", ".git");
        string target = (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken).ConfigureAwait(true))["gitdir: ".Length..].Trim();
        Directory.Delete(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(marker)!, target)), recursive: true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(Path.Combine(direct, "nested", "Other"));
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a retained directory-form nested Git marker is recognized by parent gitlink history.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DirectoryFormNestedSubmoduleStopsPreflightAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        string marker = Path.Combine(direct, "nested", "Other", ".git");
        string target = (await File.ReadAllTextAsync(marker, TestContext.Current.CancellationToken).ConfigureAwait(true))["gitdir: ".Length..].Trim();
        string gitDirectory = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(marker)!, target));
        File.Delete(marker);
        Directory.Move(gitDirectory, marker);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(Path.Combine(direct, "nested", "Other"));
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies Git retains XDG transport configuration for a local recorded gitlink.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task XdgGitConfigurationAllowsConfiguredLocalTransportAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "config", "--unset", "protocol.file.allow").ConfigureAwait(true);
        string configHome = Path.Combine(fixture.Directory, "xdg-config");
        string gitConfig = Path.Combine(configHome, "git", "config");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(gitConfig)!);
        await File.WriteAllTextAsync(gitConfig, "[protocol \"file\"]\n allow = always\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string? prior = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        try
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", configHome);
            SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);

            mapping.Entries.ShouldContain(new SourceMappingEntry("Hexalith.Dep", "source", Path.Combine(fixture.Checkout, "references", "Hexalith.Dep")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", prior);
        }
    }

    /// <summary>Verifies fixed mapping properties cannot be overwritten by an identity before checkout.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task ReservedPropertyIdentityFailsBeforeCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(gitmodules, declarations.Replace("Hexalith.Dep", "Hexalith.SourceMapping", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain("Hexalith.SourceMapping");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.SourceMapping", ".git")).ShouldBeFalse();
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a legal punctuation leaf cannot produce an empty MSBuild property suffix.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task EmptyGeneratedPropertySuffixFailsBeforeCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(gitmodules, declarations.Replace("Hexalith.Dep", "Hexalith.!!!", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain("empty MSBuild property suffix");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies an initialized nested submodule blocks every pending direct initialization.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InitializedNestedReferenceFailsBeforeOtherInitializationAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(Path.Combine(direct, "nested", "Other"));
        error.Diagnostic.Hint.ShouldNotBeNull().ShouldContain("deinit");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies a stale nested checkout blocks either mode before another direct reference is touched.</summary>
    /// <param name="mode">The requested mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task StaleNestedCheckoutBlocksBothModesAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withNested: true, withSecond: true).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "references/Hexalith.Dep").ConfigureAwait(true);
        string direct = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        File.Delete(Path.Combine(direct, ".gitmodules"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, mode, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(Path.Combine(direct, "nested", "Other"));
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Second", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies distinct identities cannot silently share generated MSBuild property names.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task CollidingGeneratedPropertySuffixesFailResolutionAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withSecond: true).ConfigureAwait(true);
        string gitmodules = Path.Combine(fixture.Checkout, ".gitmodules");
        string declarations = await File.ReadAllTextAsync(gitmodules, TestContext.Current.CancellationToken).ConfigureAwait(true);
        declarations = declarations.Replace("Hexalith.Dep", "Hexalith.Foo.Bar", StringComparison.Ordinal)
            .Replace("Hexalith.Second", "Hexalith.FooBar", StringComparison.Ordinal);
        await File.WriteAllTextAsync(gitmodules, declarations, TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW002");
        error.Diagnostic.Message.ShouldContain("Hexalith.Foo.Bar");
        error.Diagnostic.Message.ShouldContain("Hexalith.FooBar");
    }

    /// <summary>Verifies a sibling copy cannot replace a missing required direct source.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task MissingRequiredSourceDoesNotUseSiblingCopyAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        System.IO.Directory.Delete(Path.Combine(fixture.Directory, "dependency"), recursive: true);
        _ = System.IO.Directory.CreateDirectory(Path.Combine(fixture.Directory, "Hexalith.Dep"));

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW004");
        error.Diagnostic.Message.ShouldContain("Hexalith.Dep");
        error.Diagnostic.Message.ShouldContain(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep"));
    }

    /// <summary>Verifies a non-Git fixture maps only its active module without direct references.</summary>
    /// <param name="mode">The selected tool mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task NonGitWorkspaceMapsOnlyActiveModuleAsync(WorkspaceMode mode)
    {
        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = System.IO.Directory.CreateDirectory(directory);
        try
        {
            string manifest = Path.Combine(directory, "module.json");
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);

            mapping.HasGit.ShouldBeFalse();
            mapping.Mode.ShouldBe(mode);
            mapping.Entries.ShouldBe([new SourceMappingEntry(Path.GetFileName(directory), "source", directory)]);
            string localReference = Path.Combine(directory, "references", "Local", "Local.csproj");
            _ = System.IO.Directory.CreateDirectory(Path.GetDirectoryName(localReference)!);
            await File.WriteAllTextAsync(localReference, "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            mapping.ContainsProject(localReference).ShouldBeTrue();
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies corrupt Git metadata cannot silently turn a workspace into a non-Git mapping.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InvalidGitMarkerFailsResolutionAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = System.IO.Directory.CreateDirectory(directory);
        try
        {
            string manifest = Path.Combine(directory, "module.json");
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(Path.Combine(directory, ".git"), "gitdir: /missing/hexalith-workspace-git", TestContext.Current.CancellationToken).ConfigureAwait(true);

            WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            error.Diagnostic.RuleId.ShouldBe("HXW001");
            error.Diagnostic.Message.ShouldContain(manifest);
            error.Diagnostic.Message.ShouldContain("invalid or inaccessible");
        }
        finally
        {
            System.IO.Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>Verifies an absent direct manifest is restored from the recorded gitlink.</summary>
    /// <param name="mode">The tool-selected workspace mode.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source)]
    [InlineData(WorkspaceMode.Package)]
    public async Task MissingDirectManifestIsRestoredBeforeValidationAsync(WorkspaceMode mode)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync(withDirectManifest: true).ConfigureAwait(true);
        string manifest = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "module.json");
        File.Exists(manifest).ShouldBeFalse();

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, mode, TestContext.Current.CancellationToken).ConfigureAwait(true);

        File.Exists(manifest).ShouldBeTrue();
        mapping.ActiveModule.ShouldBe("Hexalith.Dep");
        mapping.HasGit.ShouldBeTrue();
    }

    /// <summary>Verifies unsupported modes cannot select package origins.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task UndefinedWorkspaceModeFailsBeforeCheckoutAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(
            () => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, (WorkspaceMode)42, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW001");
        error.Diagnostic.Message.ShouldContain("Unsupported workspace mode");
        File.Exists(Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", ".git")).ShouldBeFalse();
    }

    /// <summary>Verifies an unreadable nested Git marker cannot be classified as independent.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task InconclusiveNestedGitfileOwnershipFailsClosedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(
            fixture.Checkout,
            "-c",
            "protocol.file.allow=always",
            "submodule",
            "update",
            "--init",
            "--",
            "references/Hexalith.Dep").ConfigureAwait(true);
        string nested = Path.Combine(fixture.Checkout, "references", "Hexalith.Dep", "nested", "Unknown");
        _ = Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, ".git"), "gitdir: ../missing", TestContext.Current.CancellationToken).ConfigureAwait(true);

        WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(
            () => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

        error.Diagnostic.RuleId.ShouldBe("HXW003");
        error.Diagnostic.Message.ShouldContain(nested);
    }

    /// <summary>Verifies Git's line ending is removed without stripping legal path whitespace.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task GitRootWithTrailingWhitespaceIsPreservedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string renamed = fixture.Checkout + " ";
        Directory.Move(fixture.Checkout, renamed);
        string manifest = Path.Combine(renamed, "module.json");

        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);

        mapping.Root.ShouldBe(renamed);
        mapping.HasGit.ShouldBeTrue();
    }

    /// <summary>Verifies a dangling Git symlink still claims the workspace.</summary>
    /// <returns>A task for the assertion.</returns>
    [Fact]
    public async Task DanglingGitSymlinkFailsClosedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "hexalith-workspace-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            string manifest = Path.Combine(directory, "module.json");
            await File.WriteAllTextAsync(manifest, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = File.CreateSymbolicLink(Path.Combine(directory, ".git"), Path.Combine(directory, "gone"));

            WorkspaceMappingException error = await Should.ThrowAsync<WorkspaceMappingException>(
                () => WorkspaceRootResolver.ResolveAsync(manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken)).ConfigureAwait(true);

            error.Diagnostic.RuleId.ShouldBe("HXW001");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
