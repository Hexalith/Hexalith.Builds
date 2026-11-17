// <copyright file="ManagedBuildPreparationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Microsoft.Build.Utilities;

using Shouldly;

using Xunit;

using Task = System.Threading.Tasks.Task;

/// <summary>Qualifies explicit catalog authority against real local Git identities.</summary>
public sealed class ManagedBuildPreparationTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private static readonly string[] _commands = ["hexalith-module"];

    /// <summary>Managed authority selects a newer actual catalog while legacy tuple checks stay strict.</summary>
    /// <returns>The completed isolated producer verification.</returns>
    [Fact]
    public async Task ActiveCatalogCanAdvanceWithoutRelabellingPlatformAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        ManagedBuildAuthority authority = await PrepareInputsAsync(fixture).ConfigureAwait(true);
        string receipt = await WriteAuthorityAsync(fixture, authority).ConfigureAwait(true);
        int checks = 0;
        SourceMapping mapping = await ManagedBuildPreparationService.ResolveAsync(
            fixture.Manifest,
            receipt,
            false,
            TestContext.Current.CancellationToken,
            (_, _, _) =>
            {
                checks++;
                return Task.CompletedTask;
            },
            "6.1.0").ConfigureAwait(true);
        checks.ShouldBe(1);
        mapping.Mode.ShouldBe(WorkspaceMode.Package);
        mapping.PlatformIdentity.ShouldNotBeNull().CatalogCommit.ShouldBe(authority.PlatformCatalogCommit);
        mapping.EffectiveCatalogCommit.ShouldBe(authority.BuildsCommit);
        mapping.EffectiveCatalogCommit.ShouldNotBe(mapping.PlatformIdentity.CatalogCommit);
        string output = Path.Combine(fixture.Directory, "managed-output");
        _ = Directory.CreateDirectory(output);
        await SourceMappingMaterializer.WriteAsync(mapping, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(output, "source-mapping", "catalog-versions.json"));
        snapshot.CatalogCommit.ShouldBe(authority.BuildsCommit);
        snapshot.Versions["Hexalith.Dep"].ShouldBe("2.0.0");
        SourceMappingMaterializer.Environment(mapping, output)["Configuration"].ShouldBe("Release");

        // Legacy tuple behavior applies only when no owned producer configuration is present.
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rm", ".github/hexalith-ci.json").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "test: select legacy tuple context").ConfigureAwait(true);
        WorkspaceMappingException legacy = await Should.ThrowAsync<WorkspaceMappingException>(() => WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken)).ConfigureAwait(true);
        legacy.Diagnostics.ShouldContain(diagnostic => diagnostic.RuleId == "HXW009");
        SourceMapping source = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Source, TestContext.Current.CancellationToken).ConfigureAwait(true);
        source.IdentityFindings.ShouldContain(finding => finding.RuleId == "HXW009");
        (mapping with { ManagedAuthority = authority with { PlatformCatalogCommit = new string('1', 40) } }).ContentHash.ShouldNotBe(mapping.ContentHash);
        (mapping with { ManagedAuthority = authority with { BuildsCommit = new string('2', 40) } }).ContentHash.ShouldNotBe(mapping.ContentHash);
        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "status", "--porcelain").ConfigureAwait(true)).Output.ShouldBeEmpty();
        await File.AppendAllTextAsync(Path.Combine(output, "source-mapping", "pinned-tree", "Props", "Directory.Packages.props"), " ", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Should.Throw<InvalidDataException>(() => CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, Path.Combine(output, "source-mapping", "catalog-versions.json")));
    }

    /// <summary>Missing, stale, wrong-kind and differently scoped authorities fail before materialization.</summary>
    /// <returns>The completed refusal verification.</returns>
    [Fact]
    public async Task StaleOrQualifiedAuthorityCannotEnterProductionAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        ManagedBuildAuthority authority = await PrepareInputsAsync(fixture).ConfigureAwait(true);
        foreach (ManagedBuildAuthority candidate in new[]
        {
            authority with { Kind = "candidate-qualification" },
            authority with { ModuleHead = new string('0', 40) },
            authority with { OriginalBuildsGitlink = new string('0', 40) },
            authority with { DeliveredSha = null },
            authority with { BuildsTag = "v99.0.0" },
            authority with { PlatformCommit = new string('0', 40) },
        })
        {
            string receipt = await WriteAuthorityAsync(fixture, candidate).ConfigureAwait(true);
            _ = await Should.ThrowAsync<WorkspaceMappingException>(() => ManagedBuildPreparationService.ResolveAsync(fixture.Manifest, receipt, false, TestContext.Current.CancellationToken, (_, _, _) => Task.CompletedTask, "6.1.0")).ConfigureAwait(true);
        }

        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "status", "--porcelain").ConfigureAwait(true)).Output.ShouldBeEmpty();
    }

    /// <summary>Real SDK restore/build and repeated on-demand lookup preserve every managed producer byte.</summary>
    /// <returns>The complete immutable-lifetime assertion.</returns>
    [Fact]
    public async Task ManagedSdkBuildKeepsUnseededCatalogImmutableAsync()
    {
        const string catalog = "<Project><PropertyGroup><DaprVersion>13.6.0-preview.1</DaprVersion><DaprVersion Condition=\"$([System.String]::Copy('$(MSBuildProjectName)').StartsWith('Hexalith.Folders'))\">13.0.0</DaprVersion></PropertyGroup><ItemGroup><PackageVersion Include=\"Hexalith.Dep\" Version=\"1.0.0\" /><PackageVersion Include=\"CommunityToolkit.Aspire.Hosting.Dapr\" Version=\"$(DaprVersion)\" /></ItemGroup></Project>";
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateWithCatalogAsync(catalog).ConfigureAwait(true);
        string consumer = Path.Combine(fixture.Checkout, "src", "Consumer");
        _ = Directory.CreateDirectory(consumer);
        string project = Path.Combine(consumer, "Hexalith.Folders.Aspire.csproj");
        await File.WriteAllTextAsync(project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net11.0</TargetFramework></PropertyGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(consumer, "Consumer.cs"), "public sealed class Consumer { }", TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, ".gitignore"), "**/bin/\n**/obj/\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "src", ".gitignore").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "test: add managed SDK consumer").ConfigureAwait(true);
        ManagedBuildAuthority authority = await PrepareInputsAsync(fixture).ConfigureAwait(true);
        string receipt = await WriteAuthorityAsync(fixture, authority).ConfigureAwait(true);
        SourceMapping mapping = await ManagedBuildPreparationService.ResolveAsync(
            fixture.Manifest,
            receipt,
            false,
            TestContext.Current.CancellationToken,
            (_, _, _) => Task.CompletedTask,
            "6.1.0").ConfigureAwait(true);
        string output = Path.Combine(fixture.Directory, "immutable-output");
        await SourceMappingMaterializer.WriteAsync(mapping, output, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string snapshotPath = Path.Combine(output, "source-mapping", "catalog-versions.json");
        CatalogVersionSnapshot snapshot = CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, snapshotPath);
        snapshot.ProjectVersions.ContainsKey("Hexalith.Folders.Aspire").ShouldBeFalse();
        Dictionary<string, string> before = ProducerInventory(output);
        Dictionary<string, string> environment = new(SourceMappingMaterializer.Environment(mapping, output), StringComparer.Ordinal)
        {
            ["NUGET_PACKAGES"] = Path.Combine(fixture.Directory, "sdk-packages"),
        };
        string feed = Path.Combine(fixture.Directory, "empty-feed");
        _ = Directory.CreateDirectory(feed);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            foreach (string command in new[] { "restore", "build" })
            {
                string[] arguments = [command, project, "-noAutoResponse", "-nologo", "-v:q", "-p:Configuration=Release", "-warnaserror", "-p:RestoreSources=" + feed];
                if (command == "build")
                {
                    arguments = [.. arguments, "--no-restore"];
                }

                CompositionProcessResult result = await CompositionProcess.RunAsync(
                    CompositionSdkProcess.CreateStartInfo(arguments, fixture.Checkout, environment),
                    TimeSpan.FromMinutes(2),
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
                ProducerInventory(output).ShouldBe(before);
            }
        }

        foreach (string version in new[] { "13.0.0", "13.6.0-preview.1" })
        {
            TaskItem package = new("CommunityToolkit.Aspire.Hosting.Dapr");
            package.SetMetadata("Version", version);
            CatalogTestBuildEngine engine = new();
            CatalogVersionValidationTask task = new()
            {
                BuildEngine = engine,
                MappingFile = Path.Combine(output, "source-mapping", "mapping.json"),
                MappingHash = mapping.ContentHash,
                CatalogFile = snapshotPath,
                ProjectName = "Hexalith.Folders.Aspire",
                Packages = [package],
            };
            task.Execute().ShouldBe(version == "13.0.0");
            if (version != "13.0.0")
            {
                engine.Error.ShouldNotBeNull().Code.ShouldBe("HXW012");
            }

            ProducerInventory(output).ShouldBe(before);
        }

        File.Exists(snapshotPath + ".lock").ShouldBeFalse();
        CatalogVersionMaterializer.ReadVerifiedSnapshot(mapping, mapping.ContentHash, snapshotPath).ProjectVersions.ContainsKey("Hexalith.Folders.Aspire").ShouldBeFalse();
        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "status", "--porcelain").ConfigureAwait(true)).Output.ShouldBeEmpty();
    }

    /// <summary>Checkout's HTTPS origin is accepted; credentials, query and alternate origins refuse.</summary>
    /// <returns>The completed exact-origin verification.</returns>
    [Fact]
    public async Task CheckoutHttpsOriginFormsAreValidatedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        ManagedBuildAuthority authority = await PrepareInputsAsync(fixture).ConfigureAwait(true);
        string receipt = await WriteAuthorityAsync(fixture, authority).ConfigureAwait(true);
        foreach (string origin in new[] { "https://github.com/Example/Fixture", "https://github.com/Example/Fixture.git" })
        {
            _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "remote", "set-url", "origin", origin).ConfigureAwait(true);
            SourceMapping mapping = await ManagedBuildPreparationService.ResolveAsync(fixture.Manifest, receipt, false, TestContext.Current.CancellationToken, (_, _, _) => Task.CompletedTask, "6.1.0").ConfigureAwait(true);
            mapping.ManagedAuthority.ShouldBe(authority);
        }

        foreach (string origin in new[] { "https://user@github.com/Example/Fixture", "https://github.com/Example/Fixture?token=redacted", "https://github.com/Example/Fixture#ref", "https://example.invalid/Example/Fixture", "http://github.com/Example/Fixture", "git@github.com:Example/Fixture.git" })
        {
            _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "remote", "set-url", "origin", origin).ConfigureAwait(true);
            _ = await Should.ThrowAsync<WorkspaceMappingException>(() => ManagedBuildPreparationService.ResolveAsync(fixture.Manifest, receipt, false, TestContext.Current.CancellationToken, (_, _, _) => Task.CompletedTask, "6.1.0")).ConfigureAwait(true);
        }
    }

    /// <summary>Absent authority preserves the original hash payload and persisted mapping shape.</summary>
    /// <returns>The completed compatibility verification.</returns>
    [Fact]
    public async Task LegacySerializedHashPayloadIsUnchangedAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        SourceMapping mapping = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string original = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            mapping.Mode,
            mapping.Root,
            mapping.ActiveModule,
            mapping.Entries,
            mapping.HasGit,
            mapping.PlatformIdentity,
            mapping.IdentityFindings,
        })));
        mapping.ContentHash.ShouldBe(original);
        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(mapping));
        document.RootElement.TryGetProperty("ManagedAuthority", out _).ShouldBeFalse();
        document.RootElement.TryGetProperty("EffectiveCatalogCommit", out _).ShouldBeFalse();
        document.RootElement.TryGetProperty("EffectiveBuildsRoot", out _).ShouldBeFalse();
    }

    /// <summary>Qualification uses an external actual catalog while preserving the original owned pin.</summary>
    /// <returns>The completed explicit qualification verification.</returns>
    [Fact]
    public async Task QualifiedMappingRetainsOriginalPinAndUsesExternalCatalogAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        ManagedBuildAuthority accepted = await PrepareInputsAsync(fixture).ConfigureAwait(true);
        string external = Path.Combine(fixture.Directory, "external-builds");
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Directory, "clone", "--no-checkout", accepted.BuildsRoot, external).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(external, "checkout", "--detach", accepted.BuildsCommit).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(accepted.BuildsRoot, "checkout", "--detach", accepted.PlatformCatalogCommit).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "references/Hexalith.Builds").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "retain original owned catalog").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "update-ref", "refs/remotes/origin/main", head).ConfigureAwait(true);
        string candidateRoot = Path.Combine(fixture.Directory, "cicd");
        const string configPath = "rendered/Fixture/.github/hexalith-ci.json";
        _ = Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(candidateRoot, configPath))!);
        await File.WriteAllBytesAsync(Path.Combine(candidateRoot, configPath), await File.ReadAllBytesAsync(Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json"), TestContext.Current.CancellationToken).ConfigureAwait(true), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(candidateRoot, "init", "-b", "main").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(candidateRoot, "add", "rendered").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(candidateRoot, "update-index", "--add", "--cacheinfo", "160000," + head + ",references/Fixture").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(candidateRoot, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind exact candidate content").ConfigureAwait(true);
        string candidate = (await WorkspaceGitFixture.RunGitAsync(candidateRoot, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        string tree = (await WorkspaceGitFixture.RunGitAsync(candidateRoot, "rev-parse", "HEAD^{tree}").ConfigureAwait(true)).Output.Trim();
        string blob = (await WorkspaceGitFixture.RunGitAsync(candidateRoot, "rev-parse", "HEAD:" + configPath).ConfigureAwait(true)).Output.Trim();
        ManagedBuildAuthority qualified = accepted with
        {
            Kind = "candidate-qualification",
            ModuleHead = head,
            OriginalBuildsGitlink = accepted.PlatformCatalogCommit,
            BuildsRoot = external,
            DeliveredSha = null,
            PerimeterDigest = null,
            Qualification = new CandidateBuildQualification(head, new string('1', 64), new string('1', 64), new string('2', 64), candidateRoot, candidate, tree, configPath, blob, accepted.BuildsCommit),
        };
        string receipt = await WriteAuthorityAsync(fixture, qualified).ConfigureAwait(true);
        SourceMapping mapping = await ManagedBuildPreparationService.ResolveAsync(fixture.Manifest, receipt, true, TestContext.Current.CancellationToken, toolVersionOverride: "6.1.0").ConfigureAwait(true);
        mapping.EffectiveBuildsRoot.ShouldBe(external);
        mapping.EffectiveCatalogCommit.ShouldBe(accepted.BuildsCommit);
        mapping.ManagedAuthority.ShouldNotBeNull().OriginalBuildsGitlink.ShouldBe(accepted.PlatformCatalogCommit);
        mapping.PlatformIdentity.ShouldNotBeNull().CatalogCommit.ShouldBe(accepted.PlatformCatalogCommit);
        string immutable = Path.Combine(fixture.Directory, "qualified-immutable-output");
        await SourceMappingMaterializer.WriteAsync(mapping, immutable, TestContext.Current.CancellationToken).ConfigureAwait(true);
        Dictionary<string, string> qualifiedBefore = ProducerInventory(immutable);
        for (int iteration = 0; iteration < 2; iteration++)
        {
            TaskItem package = new("Hexalith.Dep");
            package.SetMetadata("Version", "2.0.0");
            CatalogVersionValidationTask task = new()
            {
                BuildEngine = new CatalogTestBuildEngine(),
                MappingFile = Path.Combine(immutable, "source-mapping", "mapping.json"),
                MappingHash = mapping.ContentHash,
                CatalogFile = Path.Combine(immutable, "source-mapping", "catalog-versions.json"),
                ProjectName = "Qualified.Unseeded.Consumer",
                Packages = [package],
            };
            task.Execute().ShouldBeTrue();
            ProducerInventory(immutable).ShouldBe(qualifiedBefore);
        }

        string tool = Environment.GetEnvironmentVariable("HEXALITH_MANAGED_SOURCE_PACKAGE_TOOL") ?? string.Empty;
        if (tool.Length > 0)
        {
            string workspace = Path.Combine(fixture.Directory, "installed-tool-output");
            CompositionProcessResult installed = await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(tool, ["prepare-qualified-build", "--manifest", fixture.Manifest, "--authority", receipt, "--workspace", workspace, "--output", "json"], fixture.Checkout, null),
                TimeSpan.FromMinutes(2),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            installed.ExitCode.ShouldBe(0, installed.Output + installed.ErrorOutput);
            using JsonDocument result = JsonDocument.Parse(installed.Output);
            result.RootElement.TryGetProperty("runId", out _).ShouldBeFalse();
            result.RootElement.GetProperty("buildPreparation").GetProperty("mode").GetString().ShouldBe("package");
        }

        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "ls-tree", "HEAD", "--", "references/Hexalith.Builds").ConfigureAwait(true)).Output.ShouldContain(accepted.PlatformCatalogCommit);
        (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "status", "--porcelain").ConfigureAwait(true)).Output.ShouldBeEmpty();
    }

    /// <summary>The shared Python route invokes real SDK guards and builds canonical isolated outputs under one immutable lifetime.</summary>
    /// <returns>The complete source integration assertion.</returns>
    [Fact]
    public async Task SharedIsolatedDeclarationBuildsWithinVerifiedLifetimeAsync()
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string source = Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "isolated");
        foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(fixture.Checkout, Path.GetRelativePath(source, file));
            _ = Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, ".gitignore"), "**/bin/\n**/obj/\n", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", "adapter", "fixture", ".gitignore").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "test: bind isolated SDK sources").ConfigureAwait(true);
        ManagedBuildAuthority authority = await PrepareInputsAsync(fixture, withRestorePolicy: true).ConfigureAwait(true);
        string corpus = Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "Github", "lib", "tests", "fixtures", "contract.json");
        JsonObject configuration = JsonNode.Parse(await File.ReadAllTextAsync(corpus, TestContext.Current.CancellationToken).ConfigureAwait(true))!.AsArray()[0]!["document"]!.DeepClone().AsObject();
        configuration["solution"] = "Fixture.slnx";
        configuration["buildsTag"] = "v6.1.0";
        configuration["moduleManifest"] = "module.json";
        configuration["buildProjects"] = new JsonArray(new JsonObject
        {
            ["id"] = "isolated",
            ["project"] = "adapter/Adapter.csproj",
            ["isolatedContract"] = "adapter/contract.json",
        });
        byte[] configurationBytes = JsonSerializer.SerializeToUtf8Bytes(configuration);
        await File.WriteAllBytesAsync(Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json"), configurationBytes, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "Fixture.slnx"), "<Solution />", TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".github/hexalith-ci.json", "Fixture.slnx").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "test: bind isolated declaration").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        authority = authority with { ModuleHead = head, ConfigurationDigest = Convert.ToHexStringLower(SHA256.HashData(configurationBytes)) };
        string receipt = await WriteAuthorityAsync(fixture, authority).ConfigureAwait(true);
        SourceMapping mapping = await ManagedBuildPreparationService.ResolveAsync(fixture.Manifest, receipt, false, TestContext.Current.CancellationToken, (_, _, _) => Task.CompletedTask, "6.1.0").ConfigureAwait(true);
        string workspace = Path.Combine(fixture.Directory, "isolated-producer");
        await SourceMappingMaterializer.WriteAsync(mapping, workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string input = Path.Combine(fixture.Directory, "source-driver.json");
        string data = JsonSerializer.Serialize(new
        {
            root = fixture.Checkout,
            authority = receipt,
            workspace,
            environment = SourceMappingMaterializer.Environment(mapping, workspace),
        });
        await File.WriteAllTextAsync(input, data, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string library = Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "Github", "lib");
        string driver = Path.Combine(library, "tests", "fixtures", "isolated_source_driver.py");
        Dictionary<string, string> processEnvironment = new(StringComparer.Ordinal)
        {
            ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "1",
            ["MSBUILDDISABLENODEREUSE"] = "0",
        };
        CompositionProcessResult result = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("python3", [driver, library, input], fixture.Checkout, processEnvironment),
            TimeSpan.FromMinutes(3),
            TestContext.Current.CancellationToken).ConfigureAwait(true);
        result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
        result.Output.ShouldContain("canonical DLL and immutable lifetime passed");
    }

    /// <summary>Both new public commands reject missing authority without creating producer state.</summary>
    /// <param name="command">The explicit runtime-free command.</param>
    /// <returns>The completed command verification.</returns>
    [Theory]
    [InlineData("prepare-managed-build")]
    [InlineData("prepare-qualified-build")]
    public async Task PublicCommandsRejectMissingAuthorityAsync(string command)
    {
        using WorkspaceGitFixture fixture = await WorkspaceGitFixture.CreateAsync().ConfigureAwait(true);
        string declaration = await File.ReadAllTextAsync(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "platform", "valid.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(fixture.Manifest, declaration, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "README.md"), "Never execute this file.", TestContext.Current.CancellationToken).ConfigureAwait(true);
        string output = Path.Combine(fixture.Directory, "refused-output");
        StringWriter writer = new();
        await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable lifetime = writer.ConfigureAwait(true);
        int exit = await ModuleCommandApplication.InvokeAsync([command, "--manifest", fixture.Manifest, "--authority", Path.Combine(fixture.Directory, "missing.json"), "--workspace", output, "--output", "json"], writer, TextWriter.Null, TestContext.Current.CancellationToken).ConfigureAwait(true);
        exit.ShouldNotBe(0);
        writer.ToString().ShouldContain("HXW012");
        Directory.Exists(output).ShouldBeFalse();
    }

    private static Dictionary<string, string> ProducerInventory(string directory)
        => System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .ToDictionary(path => Path.GetRelativePath(directory, path), path => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.Ordinal);

    private static async Task<ManagedBuildAuthority> PrepareInputsAsync(WorkspaceGitFixture fixture, bool withRestorePolicy = false)
    {
        string declaration = await File.ReadAllTextAsync(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "test", "fixtures", "module", "platform", "valid.json"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(fixture.Manifest, declaration, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, "README.md"), "Never execute this file.", TestContext.Current.CancellationToken).ConfigureAwait(true);
        SourceMapping old = await WorkspaceRootResolver.ResolveAsync(fixture.Manifest, WorkspaceMode.Package, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string builds = Path.Combine(fixture.Checkout, "references", "Hexalith.Builds");
        string catalog = Path.Combine(builds, "Props", "Directory.Packages.props");
        string originalCatalog = await File.ReadAllTextAsync(catalog, TestContext.Current.CancellationToken).ConfigureAwait(true);
        await File.WriteAllTextAsync(catalog, originalCatalog.Replace("1.0.0", "2.0.0", StringComparison.Ordinal), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "add", "Props/Directory.Packages.props").ConfigureAwait(true);
        if (withRestorePolicy)
        {
            File.Copy(Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "NuGet.ci.Config"), Path.Combine(builds, "NuGet.ci.Config"));
            _ = await WorkspaceGitFixture.RunGitAsync(builds, "add", "NuGet.ci.Config").ConfigureAwait(true);
        }

        _ = await WorkspaceGitFixture.RunGitAsync(builds, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "advance actual catalog").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(builds, "tag", "v6.1.0").ConfigureAwait(true);
        string active = (await WorkspaceGitFixture.RunGitAsync(builds, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        _ = Directory.CreateDirectory(Path.Combine(fixture.Checkout, ".config"));
        await File.WriteAllTextAsync(Path.Combine(fixture.Checkout, ".config", "dotnet-tools.json"), JsonSerializer.Serialize(new { version = 1, isRoot = true, tools = new Dictionary<string, object> { ["hexalith.builds.module.cli"] = new { version = "6.1.0", commands = _commands, rollForward = false } } }), TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(fixture.Checkout, ".github"));
        byte[] configuration = JsonSerializer.SerializeToUtf8Bytes(new { schema = "hexalith.ci.v1", buildsTag = "v6.1.0", moduleManifest = "module.json" });
        await File.WriteAllBytesAsync(Path.Combine(fixture.Checkout, ".github", "hexalith-ci.json"), configuration, TestContext.Current.CancellationToken).ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "add", ".config", ".github", "references/Hexalith.Builds", "module.json", "README.md").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "-c", "user.name=Qualification", "-c", "user.email=qualification@example.invalid", "commit", "-m", "bind managed product inputs").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "remote", "set-url", "origin", "https://github.com/Example/Fixture.git").ConfigureAwait(true);
        string head = (await WorkspaceGitFixture.RunGitAsync(fixture.Checkout, "rev-parse", "HEAD").ConfigureAwait(true)).Output.Trim();
        PlatformIdentity platform = old.PlatformIdentity.ShouldNotBeNull();
        return new ManagedBuildAuthority("hexalith.managed-build.v1", "accepted-delivery", "Example/Fixture", fixture.Checkout, head, active, "v6.1.0", active, builds, new string('1', 40), Convert.ToHexStringLower(SHA256.HashData(configuration)), new string('2', 64), platform.Commit, platform.CatalogCommit!, platform.ReleaseTag!, "6.1.0");
    }

    private static async Task<string> WriteAuthorityAsync(WorkspaceGitFixture fixture, ManagedBuildAuthority authority)
    {
        string receipt = Path.Combine(fixture.Directory, "authority.json");
        await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(authority, _json), TestContext.Current.CancellationToken).ConfigureAwait(true);
        return receipt;
    }
}
