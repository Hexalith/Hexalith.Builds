// <copyright file="PlatformAdoptionValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Tests.Workspace;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;

using Shouldly;

using Xunit;

/// <summary>Verifies bounded static adoption, independent failures and read-only inspection.</summary>
public sealed class PlatformAdoptionValidationTests
{
    /// <summary>Verifies the complete fixture passes every requirement without writes or sibling selection.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task CompleteWorkspacePassesAndRemainsUnchangedAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("Directory.Build.props", "<Project><Import Project=\"Optional.props\" Condition=\"Exists('Optional.props')\" /><ItemGroup Condition=\"'$(UseHexalithProjectReferences)' == 'true'\"><ProjectReference Include=\"$(HexalithEventStoreRoot)/src/Hexalith.EventStore.Contracts/Hexalith.EventStore.Contracts.csproj\" /></ItemGroup><ItemGroup Condition=\"'$(UseHexalithProjectReferences)' != 'true'\"><PackageReference Include=\"Hexalith.EventStore.Contracts\" /></ItemGroup></Project>");
        workspace.Write("src/NormalGuard.cs", "namespace Hexalith.Sample; class Sample { bool Check() => System.IO.Directory.Exists(\"data\"); }");
        await workspace.TrackAsync().ConfigureAwait(true);
        _ = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(workspace.Root)!, "Hexalith.EventStore"));
        string before = (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output;
        string[] files = Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, report.Rows.Where(row => !row.Passed)));
        report.Rows.Count.ShouldBe(9);
        report.Diagnostics.ShouldBeEmpty();
        string after = (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output;
        after.ShouldBe(before);
        Directory.GetFiles(workspace.Root, "*", SearchOption.AllDirectories).ShouldBe(files, ignoreOrder: true);
        PlatformAdoptionValidator.Scope.ShouldContain("SM-2 remains separately unverified");
    }

    /// <summary>Verifies one or more independent failures still produce every static requirement.</summary>
    /// <param name="mutation">The input mutation.</param>
    /// <param name="requirement">The expected failing requirement.</param>
    /// <returns>The assertion task.</returns>
    /// <exception cref="ArgumentException">The fixture mutation is unknown.</exception>
    [Theory]
    [InlineData("missing-manifest", "declaration")]
    [InlineData("malformed-manifest", "declaration")]
    [InlineData("minimum-dependencies", "declaration")]
    [InlineData("self-dependency", "declaration")]
    [InlineData("missing-reference", "references")]
    [InlineData("declared-dependency", "references")]
    [InlineData("missing-gitlink", "gitlinks")]
    [InlineData("stale-gitlink", "gitlinks")]
    [InlineData("uninitialized-reference", "gitlinks")]
    [InlineData("missing-pin", "tool-pin")]
    [InlineData("malformed-pin", "tool-pin")]
    [InlineData("wrong-pin", "tool-pin")]
    [InlineData("missing-inventory", "hosting-inventory")]
    [InlineData("malformed-inventory", "hosting-inventory")]
    [InlineData("arbitrary-exclusion", "hosting-inventory")]
    [InlineData("unlisted-host", "hosting-inventory")]
    [InlineData("selector", "dependency-selectors")]
    [InlineData("sibling", "dependency-selectors")]
    [InlineData("helper", "dependency-selectors")]
    [InlineData("literal-dapr", "dapr-subscriptions")]
    [InlineData("subscription", "dapr-subscriptions")]
    public async Task EveryRequirementFailureIsReportedAsync(string mutation, string requirement)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        switch (mutation)
        {
            case "missing-manifest": File.Delete(workspace.Manifest); break;
            case "malformed-manifest": workspace.Write("module.json", "{"); break;
            case "minimum-dependencies": ChangeDependencies("[]"); break;
            case "self-dependency": ChangeDependencies("[\"eventstore\",\"tenants\",\"memories\",\"sample\"]"); break;
            case "declared-dependency": ChangeDependencies("[\"eventstore\",\"tenants\",\"memories\",\"folders\"]"); break;
            case "missing-reference": workspace.Write(".gitmodules", (await File.ReadAllTextAsync(Path.Combine(workspace.Root, ".gitmodules"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("path = references/Hexalith.Tenants", "path = references/Hexalith.Unrelated", StringComparison.Ordinal)); break;
            case "missing-gitlink": _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "update-index", "--force-remove", "references/Hexalith.Tenants").ConfigureAwait(true); break;
            case "stale-gitlink":
                _ = await WorkspaceGitFixture.RunGitAsync(Path.Combine(workspace.Root, "references/Hexalith.Tenants"), "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "fixture stale checkout").ConfigureAwait(true);
                break;
            case "uninitialized-reference":
                _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "submodule", "deinit", "-f", "--", "references/Hexalith.Tenants").ConfigureAwait(true);
                break;
            case "missing-pin": File.Delete(Path.Combine(workspace.Root, ".config/dotnet-tools.json")); break;
            case "malformed-pin": workspace.Write(".config/dotnet-tools.json", "[]"); break;
            case "wrong-pin": workspace.Write(".config/dotnet-tools.json", (await File.ReadAllTextAsync(Path.Combine(workspace.Root, ".config/dotnet-tools.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("4.34.0", "4.33.1", StringComparison.Ordinal)); break;
            case "missing-inventory": File.Delete(Path.Combine(workspace.Root, "module/hosting-inventory.json")); break;
            case "malformed-inventory": workspace.Write("module/hosting-inventory.json", "[]"); break;
            case "arbitrary-exclusion": workspace.Write("module/hosting-inventory.json", "{\"schemaVersion\":1,\"projects\":[],\"exclusions\":[\".\"]}"); break;
            case "unlisted-host": workspace.Write("src/Hexalith.Sample.AppHost/Hexalith.Sample.AppHost.csproj", "<Project />"); break;
            case "selector": workspace.Write("Directory.Build.props", "<Project><PropertyGroup><HexalithEventStoreFromSource Condition=\"Exists('local.csproj')\">true</HexalithEventStoreFromSource></PropertyGroup></Project>"); break;
            case "sibling": workspace.Write("Directory.Build.props", "<Project><PropertyGroup><HexalithEventStoreRoot>../Hexalith.EventStore</HexalithEventStoreRoot></PropertyGroup></Project>"); break;
            case "helper": workspace.Write("eng/resolve-source.ps1", "if (Test-Path '../Hexalith.EventStore') { $UseNuGetDeps = $false }"); break;
            case "literal-dapr": workspace.Write("src/Client.cs", "await dapr.PublishEventAsync(\"pubsub\", topic, data);"); break;
            case "subscription": workspace.Write("src/Consumer.cs", "[Topic(\"pubsub\", \"events\")] public void Receive() {}"); break;
            default: throw new ArgumentException("Unknown fixture mutation.", nameof(mutation));
        }

        if (mutation is not "missing-gitlink" and not "stale-gitlink")
        {
            await workspace.TrackAsync().ConfigureAwait(true);
        }

        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.IsValid.ShouldBeFalse();
        report.Rows.ShouldContain(row => row.Requirement == requirement && !row.Passed && row.File.Length > 0 && row.Reason.Length > 0);
        report.Rows.Select(row => row.Requirement).Distinct().Count().ShouldBe(9);
        report.Rows.ShouldContain(row => row.Requirement != requirement && row.Passed);

        void ChangeDependencies(string json)
        {
            JsonNode document = JsonNode.Parse(File.ReadAllText(workspace.Manifest))!;
            document["modules"]![0]!["identity"]!["dependencies"] = JsonNode.Parse(json);
            workspace.Write("module.json", document.ToJsonString());
        }
    }

    /// <summary>Verifies external manifests and non-Git working directories fail with independent rows.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task ExternalManifestAndMissingWorkspaceRemainStructuredAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        using PlatformManifestTestWorkspace outside = new();
        PlatformAdoptionValidationResult external = await PlatformAdoptionValidator.ValidateAsync([outside.Save()], workspace.Root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        external.Rows.ShouldContain(row => row.Requirement == "declaration" && !row.Passed && row.Reason.Contains("inside", StringComparison.Ordinal));
        PlatformAdoptionValidationResult noGit = await PlatformAdoptionValidator.ValidateAsync([outside.Save()], Path.GetRelativePath(Directory.GetCurrentDirectory(), outside.Root), TestContext.Current.CancellationToken).ConfigureAwait(true);
        noGit.Rows.ShouldContain(row => row.Requirement == "workspace" && !row.Passed);
        noGit.Rows.Select(row => row.Requirement).Distinct().Count().ShouldBe(9);
        noGit.Rows.ShouldNotContain(row => row.Reason.Contains("basePath", StringComparison.Ordinal));
    }

    /// <summary>Verifies nested initialization is rejected without initialization or checkout writes.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task InitializedNestedReferenceFailsReadOnlyAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync(nested: true).ConfigureAwait(true);
        string direct = Path.Combine(workspace.Root, "references/Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(direct, "-c", "protocol.file.allow=always", "submodule", "update", "--init", "--", "nested/Other").ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "nested-references" && !row.Passed && row.Reason.Contains("nested", StringComparison.OrdinalIgnoreCase));
        PlatformAdoptionRow nested = report.Rows.Single(row => row.Requirement == "nested-references" && !row.Passed);
        Path.IsPathFullyQualified(nested.File).ShouldBeFalse();
        nested.File.ShouldContain("references/Hexalith.Dep");
        nested.Reason.ShouldContain("submodule deinit");
        File.Exists(Path.Combine(direct, "nested/Other/.git")).ShouldBeTrue();
    }

    /// <summary>Verifies exact frozen hosting exclusions cannot hide root build selectors or arbitrary directories.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task FrozenInventoryIsConfinedToExactHostingDirectoryAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        const string project = "src/Hexalith.Sample.AppHost/Hexalith.Sample.AppHost.csproj";
        workspace.Write(project, "<Project />");
        workspace.Write("src/Hexalith.Sample.AppHost/Program.cs", "app.MapSubscribeHandler();");
        workspace.Write("module/hosting-inventory.json", "{\"schemaVersion\":1,\"projects\":[{\"path\":\"" + project + "\",\"kind\":\"AppHost\",\"owner\":\"owner\",\"consumers\":[\"sample\"],\"state\":\"frozen\",\"replacementPlan\":\"pending\",\"retirementPlan\":\"pending\",\"gates\":{\"producerAdoption\":\"pending\",\"ownerReviewedSourcePackageDeployedParity\":\"pending\",\"eventStoreAd22ExactSubjectRemovalAuthority\":\"pending\"}}]}");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
        workspace.Write("Directory.Build.props", "<Project><PropertyGroup><UseNuGetDeps>true</UseNuGetDeps></PropertyGroup></Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == "Directory.Build.props");
        workspace.Write("module/hosting-inventory.json", (await File.ReadAllTextAsync(Path.Combine(workspace.Root, "module/hosting-inventory.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace(project, "Directory.Build.props", StringComparison.Ordinal));
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "hosting-inventory" && !row.Passed);
    }

    /// <summary>Verifies tracked helpers below references remain module-owned audit inputs.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task TrackedReferencesHelperIsAuditedAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("references/resolve-source.sh", "if [ -d ../Hexalith.EventStore ]; then UseNuGetDeps=false; fi");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == "references/resolve-source.sh");
    }

    /// <summary>Verifies repository identity matching preserves actual mixed-case direct paths.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task MixedCaseChatBotDependencyUsesActualDirectPathAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string source = Path.Combine(workspace.Root, "references/Hexalith.Dep");
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(source).AbsoluteUri, "references/Hexalith.ChatBot").ConfigureAwait(true);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(workspace.Manifest, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        document["modules"]![0]!["identity"]!["dependencies"]!.AsArray().Add("chatbot");
        workspace.Write("module.json", document.ToJsonString());
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies an additional recorded dependency cannot pass while uninitialized and is never initialized by validation.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task UninitializedAdditionalDeclaredDependencyFailsReadOnlyAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string source = Path.Combine(workspace.Root, "references/Hexalith.Dep");
        const string reference = "references/Hexalith.ChatBot";
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(source).AbsoluteUri, reference).ConfigureAwait(true);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(workspace.Manifest, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        document["modules"]![0]!["identity"]!["dependencies"]!.AsArray().Add("chatbot");
        workspace.Write("module.json", document.ToJsonString());
        await workspace.TrackAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "submodule", "deinit", "-f", "--", reference).ConfigureAwait(true);
        string before = (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output;
        string dependency = Path.Combine(workspace.Root, reference);
        File.Exists(Path.Combine(dependency, ".git")).ShouldBeFalse();
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.Single(row => row.Requirement == "references").Passed.ShouldBeTrue();
        report.Rows.ShouldContain(row => row.Requirement == "gitlinks" && !row.Passed && row.Reason.Contains(reference, StringComparison.Ordinal));
        File.Exists(Path.Combine(dependency, ".git")).ShouldBeFalse();
        Directory.Exists(Path.Combine(dependency, ".git")).ShouldBeFalse();
        Directory.GetFileSystemEntries(dependency).ShouldBeEmpty();
        (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output.ShouldBe(before);
    }

    /// <summary>Verifies a declared direct self-reference fails even with a valid recorded gitlink.</summary>
    /// <param name="uninitializedRequiredReference">Whether another required reference is uninitialized.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectSelfReferenceIsRejectedAsync(bool uninitializedRequiredReference)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(Path.Combine(workspace.Root, "references/Hexalith.Dep")).AbsoluteUri, "references/Hexalith.Sample").ConfigureAwait(true);
        if (uninitializedRequiredReference)
        {
            _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "submodule", "deinit", "-f", "--", "references/Hexalith.Tenants").ConfigureAwait(true);
        }

        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "references" && !row.Passed && row.Reason.Contains("self reference", StringComparison.Ordinal));
        report.Rows.Single(row => row.Requirement == "gitlinks").Passed.ShouldBe(!uninitializedRequiredReference);
    }

    /// <summary>Verifies frozen hosts cannot hide dependency assignments and missing projects grant no exclusion.</summary>
    /// <param name="deleteProject">Whether the tracked host project is removed after inventory creation.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenHostsCannotBypassDependencyOrExistenceChecksAsync(bool deleteProject)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        const string project = "src/Hexalith.Sample.AppHost/Hexalith.Sample.AppHost.csproj";
        workspace.Write(project, "<Project><PropertyGroup><UseNuGetDeps>true</UseNuGetDeps></PropertyGroup></Project>");
        workspace.Write("src/Hexalith.Sample.AppHost/Program.cs", "app.MapSubscribeHandler();");
        workspace.Write("module/hosting-inventory.json", "{\"schemaVersion\":1,\"projects\":[{\"path\":\"" + project + "\",\"kind\":\"AppHost\",\"owner\":\"owner\",\"consumers\":[\"sample\"],\"state\":\"frozen\",\"replacementPlan\":\"pending\",\"retirementPlan\":\"pending\",\"gates\":{\"producerAdoption\":\"pending\",\"ownerReviewedSourcePackageDeployedParity\":\"pending\",\"eventStoreAd22ExactSubjectRemovalAuthority\":\"pending\"}}]}");
        await workspace.TrackAsync().ConfigureAwait(true);
        if (deleteProject)
        {
            File.Delete(Path.Combine(workspace.Root, project));
        }

        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == project);
        if (deleteProject)
        {
            report.Rows.ShouldContain(row => row.Requirement == "hosting-inventory" && !row.Passed);
            report.Rows.ShouldContain(row => row.Requirement == "dapr-subscriptions" && !row.Passed && row.File.EndsWith("Program.cs", StringComparison.Ordinal));
        }
        else
        {
            report.Rows.ShouldContain(row => row.Requirement == "dapr-subscriptions" && row.Passed);
        }
    }

    /// <summary>Verifies case-insensitive generated properties and dependency-selecting imports fail.</summary>
    /// <param name="body">The build body.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("<PropertyGroup><HexalithEventStoreFromSource>true</HexalithEventStoreFromSource></PropertyGroup>")]
    [InlineData("<PropertyGroup><hexalitheventstorefromsource>true</hexalitheventstorefromsource></PropertyGroup>")]
    [InlineData("<PropertyGroup><usenugetdeps>true</usenugetdeps></PropertyGroup>")]
    [InlineData("<PropertyGroup><usehexalithprojectreferences>true</usehexalithprojectreferences></PropertyGroup>")]
    [InlineData("<PropertyGroup><hexalitheventstoreroot>references/Hexalith.EventStore</hexalitheventstoreroot></PropertyGroup>")]
    [InlineData("<Import Project=\"$(HexalithBuildsRoot)/Hexalith.Build.props\" Condition=\"Exists('$(HexalithBuildsRoot)/Hexalith.Build.props')\" />")]
    [InlineData("<PropertyGroup><HexalithSourceMappingMode>package</HexalithSourceMappingMode></PropertyGroup>")]
    [InlineData("<PropertyGroup><hexalithsourcemappingmode>source</hexalithsourcemappingmode></PropertyGroup>")]
    [InlineData("<PropertyGroup><HexalithSourceMappingApplies>false</HexalithSourceMappingApplies></PropertyGroup>")]
    [InlineData("<PropertyGroup><HexalithSourceMappingHash>replacement</HexalithSourceMappingHash></PropertyGroup>")]
    [InlineData("<PropertyGroup><HexalithSourceMappingActiveModule>other</HexalithSourceMappingActiveModule></PropertyGroup>")]
    [InlineData("<PropertyGroup><HexalithSourceMappingHasGit>false</HexalithSourceMappingHasGit></PropertyGroup>")]
    [InlineData("<PropertyGroup><HexalithTenantsBasePath>references/Hexalith.Tenants/src</HexalithTenantsBasePath></PropertyGroup>")]
    [InlineData("<PropertyGroup><hexalithtenantsbasepath>local</hexalithtenantsbasepath></PropertyGroup>")]
    public async Task ToolOwnedPropertiesAndSelectingImportsFailAsync(string body)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("Directory.Build.props", "<Project>" + body + "</Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == "Directory.Build.props");
    }

    /// <summary>Verifies project metadata and MSBuild tasks cannot replace reserved mapping properties.</summary>
    /// <param name="body">The build declaration.</param>
    /// <param name="passes">Whether its properties are ordinary build settings.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("<ItemGroup><ProjectReference Include=\"Local.csproj\" AdditionalProperties=\"UseHexalithProjectReferences=false\" /></ItemGroup>", false)]
    [InlineData("<ItemGroup><ProjectReference Include=\"Local.csproj\"><AdditionalProperties>Configuration=Debug;HexalithTenantsRoot=../Hexalith.Tenants</AdditionalProperties></ProjectReference></ItemGroup>", false)]
    [InlineData("<Target Name=\"BuildLocal\"><MSBuild Projects=\"Local.csproj\" Properties=\"Configuration=Debug;UseHexalithProjectReferences=false\" /></Target>", false)]
    [InlineData("<Target Name=\"BuildLocal\"><MSBuild Projects=\"Local.csproj\" Properties=\"HexalithTenantsRoot=../Hexalith.Tenants\" /></Target>", false)]
    [InlineData("<ItemGroup><ProjectReference Include=\"Local.csproj\" AdditionalProperties=\"Configuration=Debug;TargetFramework=net10.0\" /></ItemGroup>", true)]
    [InlineData("<ItemGroup><ProjectReference Include=\"Local.csproj\"><AdditionalProperties>Configuration=Debug</AdditionalProperties></ProjectReference></ItemGroup>", true)]
    [InlineData("<Target Name=\"BuildLocal\"><MSBuild Projects=\"Local.csproj\" Properties=\"Configuration=Release;EnableFeature=true\" /></Target>", true)]
    public async Task BuildMetadataAndTasksPreserveReservedPropertiesAsync(string body, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("Directory.Build.targets", "<Project>" + body + "</Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies ordinary path construction and content-root access are not existence probes.</summary>
    /// <param name="source">Ordinary module source.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("var path = Path.Combine(environment.ContentRootPath, \"data\");")]
    [InlineData("var path = Path.Combine(\"Hexalith.Sample\", \"data\");")]
    [InlineData("bool exists = File.Exists(Path.Combine(repoRoot, \"Hexalith.Tenants.sln\"));")]
    [InlineData("var root = FindRepositoryRoot(); var source = GetSourcePath(); var packageRoot = GetPackageDirectory();")]
    [InlineData("// if (File.Exists(\"../Hexalith.EventStore\")) UseNuGetDeps = true;\nclass Sample {}")]
    [InlineData("/* return FindRepositoryRoot(\"references/Hexalith.EventStore\"); */ class Sample {}")]
    [InlineData("var root = FindRepositoryRoot(); var catalog = XDocument.Load(Path.Combine(root, \"references/Hexalith.Builds/Props/Directory.Packages.props\"));")]
    [InlineData("references.ShouldContain(@\"..\\Hexalith.Sample.Contracts\\Hexalith.Sample.Contracts.csproj\");")]
    [InlineData("var example = \"File.Exists(\\\"../Hexalith.Other\\\")\";")]
    [InlineData("Directory.Exists(Path.Combine(partiesRoot, \"references/Hexalith.McpCli\")).ShouldBeFalse();")]
    [InlineData("Directory.Exists(Path.Combine(partiesRoot, \"references/Hexalith.McpCli\")).ShouldBeTrue();")]
    [InlineData("Assert.False(Directory.Exists(Path.Combine(partiesRoot, \"references/Hexalith.McpCli\")));")]
    [InlineData("Assert.True(System.IO.File.Exists(Path.Combine(partiesRoot, \"references/Hexalith.McpCli\")), \"required\");")]
    public async Task OrdinaryPathAccessPassesAsync(string source)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("src/PathAccess.cs", source);
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies comments and literal-only text cannot impersonate C# invocations or attributes.</summary>
    /// <param name="source">The C# input.</param>
    /// <param name="passes">Whether bounded Dapr inspection should pass.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("// removed app.MapSubscribeHandler();\nclass Sample {}", true)]
    [InlineData("/* [global::Dapr.Topic(\"pubsub\", \"events\")] */ class Sample {}", true)]
    [InlineData("var text = \"WithTopic(\";", true)]
    [InlineData("var text = @\"MapSubscribeHandler(\";", true)]
    [InlineData("var text = \"\"\"[Topic(\"pubsub\", \"events\")]\"\"\";", true)]
    [InlineData("// dapr.PublishEventAsync(\"literal\", topic, value);", true)]
    [InlineData("app.MapSubscribeHandler();", false)]
    [InlineData("endpoint.WithTopic(\"events\");", false)]
    [InlineData("[Dapr.Topic(\"pubsub\", \"events\")] class Sample {}", false)]
    [InlineData("[global::Dapr.Topic(\"pubsub\", \"events\")] class Sample {}", false)]
    [InlineData("app.MapGet(\"/dapr/subscribe\", () => result);", false)]
    [InlineData("await dapr.PublishEventAsync(\"literal\", topic, value);", false)]
    [InlineData("[HttpPost(\"orders\"), Topic(\"pubsub\", \"orders\")] public void Receive() {}", false)]
    [InlineData("[HttpGet(\"dapr/subscribe\")] public object Subscribe() => topics;", false)]
    [InlineData("await dapr.TrySaveStateAsync(\"literal\", key, value, etag);", false)]
    [InlineData("await dapr.QueryStateAsync<State>(\"literal\", query);", false)]
    [InlineData("await dapr.PublishBulkEventAsync(\"literal\", topic, values);", false)]
    [InlineData("await dapr.InvokeBindingAsync(\"literal\", operation, value);", false)]
    [InlineData("app.MapSubscribeHandler /* removed discovery */ ();", false)]
    [InlineData("app.MapSubscribeHandler // inter-token comment\n ();", false)]
    [InlineData("endpoint.WithTopic /* comment */ (\"events\");", false)]
    [InlineData("[Dapr./* qualifier */Topic(@\"pubsub\", @\"events\")] class Sample {}", false)]
    [InlineData("await dapr.PublishEventAsync /* comment */ (@\"pubsub\", topic, data);", false)]
    [InlineData("var options = new Options { PubSubName = @\"pubsub\" };", false)]
    [InlineData("app.MapGet(/* route */@\"/dapr/subscribe\", () => topics);", false)]
    [InlineData("app.Map(\"/dapr/subscribe\", handler);", false)]
    [InlineData("[Route(\"dapr/subscribe\")] public object Subscribe() => topics;", false)]
    [InlineData("await dapr.GetStateAndETagAsync<State>(\"literal\", key);", false)]
    [InlineData("await dapr.SaveBulkStateAsync(\"literal\", values);", false)]
    [InlineData("await dapr.TryDeleteStateAsync(\"literal\", key, etag);", false)]
    [InlineData("await dapr.DeleteBulkStateAsync(\"literal\", values);", false)]
    [InlineData("await dapr.PublishByteEventAsync(\"literal\", topic, bytes);", false)]
    [InlineData("app.MapGet(pattern: \"/dapr/subscribe\", handler: () => topics);", false)]
    [InlineData("app.MapGet(\"/dapr/subscribe/\", () => topics);", false)]
    [InlineData("app.MapGet(pattern: \"/dapr/subscribe/\", handler: () => topics);", false)]
    [InlineData("await dapr.PublishEventAsync(\"\"\"pubsub\"\"\", topic, data);", false)]
    [InlineData("await dapr.PublishEventAsync($\"pubsub\", topic, data);", false)]
    [InlineData("await dapr.PublishEventAsync($@\"pubsub\", topic, data);", false)]
    [InlineData("await dapr.PublishEventAsync($\"{componentName}\", topic, data);", true)]
    [InlineData("await dapr.InvokeBindingAsync(bindingName: \"literal\", operation: \"create\", data: value);", false)]
    [InlineData("const string StoreName = \"BusinessStore\";", true)]
    [InlineData("const string ComponentName = \"BusinessComponent\";", true)]
    [InlineData("var options = new BusinessOptions { StoreName = \"BusinessStore\", ComponentName = \"BusinessComponent\" };", true)]
    [InlineData("const string StoreName = \"statestore\"; await dapr.GetStateAsync<State>(StoreName, key);", false)]
    [InlineData("const string ComponentName = \"pubsub\"; await dapr.PublishEventAsync(ComponentName, topic, data);", false)]
    [InlineData("var options = new DaprOptions { StoreName = \"statestore\" };", false)]
    [InlineData("var options = new Dapr.StateStoreOptions { ComponentName = \"statestore\" };", false)]
    [InlineData("var options = new Options { StateStoreName = \"statestore\" };", false)]
    [InlineData("var options = new Options { SecretStoreName = \"secrets\" };", false)]
    [InlineData("await dapr.GetStateAsync<State>(key: key, storeName: \"literal\");", false)]
    [InlineData("var value = business.Load(key: key, StoreName: \"BusinessStore\");", true)]
    [InlineData("await dapr.PublishEventAsync(component, topic, business.Create(StoreName: \"BusinessStore\"));", true)]
    [InlineData("// dapr.PublishEventAsync($\"pubsub\", topic, data);", true)]
    [InlineData("// app.MapSubscribeHandler /* comment */ ();", true)]
    [InlineData("var text = @\"MapSubscribeHandler /* comment */ (\";", true)]
    [InlineData("var text = \"\"\"dapr.PublishEventAsync(@\"pubsub\", topic, data)\"\"\";", true)]
    [InlineData("await dapr.GetStateAsync<List<Order>>(\"statestore\", key);", false)]
    [InlineData("await dapr.GetStateAsync<List<Order>>(configuration[\"Sample:Dapr:StateComponent\"], key);", true)]
    [InlineData("await dapr.GetStateAsync<Dictionary<string, Order>>(\"statestore\", key);", false)]
    [InlineData("await dapr.GetStateAsync<Order>(key: GetKey(), storeName: \"literal\");", false)]
    [InlineData("await dapr.GetStateAsync<Order>(key: GetKey(), storeName: configuration[\"Sample:Dapr:StateComponent\"]);", true)]
    [InlineData("await dapr.GetStateAsync<Order>(key: GetKey(CreateKey()), storeName: \"literal\");", false)]
    [InlineData("await dapr.PublishEventAsync(data: CreateEvent(), pubsubName: \"literal\", topicName: topic);", false)]
    [InlineData("await dapr.InvokeBindingAsync(data: CreateData(), bindingName: \"literal\", operation: \"create\");", false)]
    [InlineData("var value = business.Load(key: GetKey(), storeName: \"BusinessStore\");", true)]
    [InlineData("await dapr.GetStateAsync<Order>(key: business.Create(storeName: \"BusinessStore\"), storeName: configuredName);", true)]
    [InlineData("await dapr.PublishEventAsync(@$\"pubsub\", topic, data);", false)]
    [InlineData("await dapr.PublishEventAsync(@$\"{componentName}\", topic, data);", true)]
    public async Task DaprAuditUsesExecutableSyntaxAsync(string source, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("src/Consumer.cs", source);
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.Single(row => row.Requirement == "dapr-subscriptions").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies declared Dapr configuration keys reject physical defaults in each supported format.</summary>
    /// <param name="format">The configuration format.</param>
    /// <param name="value">The configured value.</param>
    /// <param name="passes">Whether the value is an empty or injected input.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("json", "physical-pubsub", false)]
    [InlineData("yaml", "physical-pubsub", false)]
    [InlineData("yml", "physical-pubsub", false)]
    [InlineData("json", "", true)]
    [InlineData("yaml", "", true)]
    [InlineData("yml", "", true)]
    [InlineData("json", "${SAMPLE_EVENTS_COMPONENT}", true)]
    [InlineData("yaml", "${SAMPLE_EVENTS_COMPONENT}", true)]
    [InlineData("yml", "${SAMPLE_EVENTS_COMPONENT}", true)]
    [InlineData("json", null, true)]
    [InlineData("yaml", null, true)]
    [InlineData("yml", null, true)]
    [InlineData("json", "$(Sample:Dapr:EventsComponent)", true)]
    [InlineData("json", "{{ Sample.Dapr.EventsComponent }}", true)]
    [InlineData("yaml", "$(Sample:Dapr:EventsComponent)", true)]
    [InlineData("yaml", "{{ Sample.Dapr.EventsComponent }}", true)]
    [InlineData("yml", "$(Sample:Dapr:EventsComponent)", true)]
    [InlineData("yml", "{{ Sample.Dapr.EventsComponent }}", true)]
    public async Task DeclaredDaprConfigurationDefaultsAreAuditedAsync(string format, string? value, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        JsonNode manifest = JsonNode.Parse(await File.ReadAllTextAsync(workspace.Manifest, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        manifest["modules"]![0]!["runtime"]!["dapr"]![0]!["configurationKey"] = "Sample:Dapr:EventsComponent";
        workspace.Write("module.json", manifest.ToJsonString());
        string configuration = format == "json"
            ? new JsonObject { ["Sample"] = new JsonObject { ["Dapr"] = new JsonObject { ["EventsComponent"] = value } } }.ToJsonString()
            : "Sample:\n  Dapr:\n    EventsComponent: " + JsonSerializer.Serialize(value) + "\n";
        workspace.Write("appsettings." + format, configuration);
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.Single(row => row.Requirement == "dapr-subscriptions").Passed.ShouldBe(passes);
        if (!passes)
        {
            report.Rows.ShouldContain(row => !row.Passed && row.File == "appsettings." + format);
        }
    }

    /// <summary>Verifies generic component-name defaults fail in each supported configuration format.</summary>
    /// <param name="format">The configuration format.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("yaml")]
    [InlineData("yml")]
    public async Task GenericDaprConfigurationDefaultsAreAuditedAsync(string format)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string filename = "appsettings." + format;
        workspace.Write(filename, format == "json" ? "{\"PubSubName\":\"pubsub\"}" : "PubSubName: pubsub\n");
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "dapr-subscriptions" && !row.Passed && row.File == filename);
    }

    /// <summary>Verifies invalid command arrays cannot masquerade as a runnable exact tool pin.</summary>
    /// <param name="commands">The commands value, or missing sentinel.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("<missing>")]
    [InlineData("[]")]
    [InlineData("[\"another-command\"]")]
    public async Task ToolPinRequiresRunnableModuleCommandAsync(string commands)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        JsonNode pin = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Root, ".config/dotnet-tools.json"), TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        JsonObject tool = pin["tools"]!["hexalith.builds.module.cli"]!.AsObject();
        if (commands == "<missing>")
        {
            _ = tool.Remove("commands");
        }
        else
        {
            tool["commands"] = JsonNode.Parse(commands);
        }

        workspace.Write(".config/dotnet-tools.json", pin.ToJsonString());
        workspace.Write("references/Hexalith.Platform/.config/dotnet-tools.json", pin.ToJsonString());
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "tool-pin" && !row.Passed && row.Reason.Contains("command", StringComparison.Ordinal));
    }

    /// <summary>Verifies matching working-tree pins cannot override the committed direct Platform identity.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task MatchingWorkingTreePinsMustMatchCommittedPlatformPinAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string pin = (await File.ReadAllTextAsync(Path.Combine(workspace.Root, ".config/dotnet-tools.json"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("4.34.0", "4.35.0", StringComparison.Ordinal);
        workspace.Write(".config/dotnet-tools.json", pin);
        workspace.Write("references/Hexalith.Platform/.config/dotnet-tools.json", pin);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "tool-pin" && !row.Passed && row.Reason.Contains("committed", StringComparison.Ordinal));
    }

    /// <summary>Verifies reports contain machine rows and human PASS/FAIL labels while legacy JSON stays unchanged.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task ReportsRenderRowsAndLeaveLegacyJsonUnchangedAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        ToolCommandResult result = new("validated", ToolOutcome.Passed(), report.Diagnostics) { Adoption = report.Rows, AdoptionScope = PlatformAdoptionValidator.Scope };
        StringWriter human = new();
        await using (human.ConfigureAwait(true))
        {
            await ToolDiagnosticFormatter.WriteAsync(human, result, ToolOutputFormat.Human, TestContext.Current.CancellationToken).ConfigureAwait(true);
            human.ToString().ShouldContain("PASS references file=.gitmodules");
            human.ToString().ShouldContain("SM-2 remains separately unverified");
        }

        StringWriter json = new();
        await using (json.ConfigureAwait(true))
        {
            await ToolDiagnosticFormatter.WriteAsync(json, result, ToolOutputFormat.Json, TestContext.Current.CancellationToken).ConfigureAwait(true);
            using JsonDocument parsed = JsonDocument.Parse(json.ToString());
            JsonElement output = parsed.RootElement;
            output.GetProperty("adoptionScope").GetString().ShouldBe(PlatformAdoptionValidator.Scope);
            JsonElement[] rendered = [.. output.GetProperty("adoption").EnumerateArray()];
            rendered.Length.ShouldBe(report.Rows.Count);
            for (int index = 0; index < rendered.Length; index++)
            {
                rendered[index].GetProperty("requirement").GetString().ShouldBe(report.Rows[index].Requirement);
                rendered[index].GetProperty("passed").GetBoolean().ShouldBe(report.Rows[index].Passed);
                rendered[index].GetProperty("file").GetString().ShouldBe(report.Rows[index].File);
                rendered[index].GetProperty("reason").GetString().ShouldBe(report.Rows[index].Reason);
            }
        }

        StringWriter legacy = new();
        await using (legacy.ConfigureAwait(true))
        {
            await ToolDiagnosticFormatter.WriteAsync(legacy, new ToolCommandResult("validated", ToolOutcome.Passed(), []), ToolOutputFormat.Json, TestContext.Current.CancellationToken).ConfigureAwait(true);
            legacy.ToString().ShouldNotContain("adoption");
        }
    }

    /// <summary>Verifies tracked-file inventories larger than the ordinary process output bound pass.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task LargeTrackedPathInventoryPassesAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        for (int index = 0; index < 800; index++)
        {
            workspace.Write("evidence/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string('x', 90) + ".txt", "evidence");
        }

        await workspace.TrackAsync().ConfigureAwait(true);
        string paths = (await Hexalith.Builds.Tooling.Workspace.GitWorkspaceProcess.RunAsync(workspace.Root, 1_048_576, TestContext.Current.CancellationToken, "ls-files", "-z").ConfigureAwait(true)).Output;
        paths.Length.ShouldBeGreaterThan(65_536);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies only supported application configuration receives JSON/YAML inspection and size limits.</summary>
    /// <param name="path">The tracked input path.</param>
    /// <param name="passes">Whether bounded static inspection accepts the input.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("evidence/report.json", true)]
    [InlineData("api/openapi.yaml", true)]
    [InlineData("appsettings.Development.json", false)]
    [InlineData("appsettings.yaml", false)]
    public async Task ConfigurationFilteringPrecedesSizeBoundAsync(string path, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write(path, new string(' ', 1_048_577));
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBeTrue();
        report.Rows.Single(row => row.Requirement == "dapr-subscriptions").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies application JSON accepts comments and trailing commas while auditing physical defaults.</summary>
    /// <param name="value">The configured component name.</param>
    /// <param name="passes">Whether the name is injected.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("${PUBSUB}", true)]
    [InlineData("literal", false)]
    public async Task JsonConfigurationCommentsAndTrailingCommasAreSupportedAsync(string value, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("appsettings.Development.json", "{ /* configuration comment */ \"PubSubName\":\"" + value + "\", }");
        workspace.Write(".vscode/launch.json", "{ // editor configuration is outside the audit\n \"version\": \"0.2.0\", }");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dapr-subscriptions").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies only the inert direct Builds bootstrap is permitted.</summary>
    /// <param name="body">The MSBuild property body.</param>
    /// <param name="passes">Whether the exact bootstrap is used.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("<HexalithBuildsRoot Condition=\" '$(HexalithBuildsRoot)' == '' \"> $(MSBuildThisFileDirectory)references/Hexalith.Builds </HexalithBuildsRoot>", true)]
    [InlineData("<HexalithBuildsRoot Condition=\"'$(HexalithBuildsRoot)'==''\">$(MSBuildThisFileDirectory)references\\Hexalith.Builds</HexalithBuildsRoot>", true)]
    [InlineData("<HexalithBuildsRoot>$(MSBuildThisFileDirectory)references/Hexalith.Builds</HexalithBuildsRoot>", false)]
    [InlineData("<HexalithBuildsRoot Condition=\"'$(HexalithBuildsRoot)'==''\">$(MSBuildThisFileDirectory)../Hexalith.Builds</HexalithBuildsRoot>", false)]
    [InlineData("<HexalithBuildsRoot Condition=\"'$(HexalithBuildsRoot)'=='' and Exists('local')\">$(MSBuildThisFileDirectory)references/Hexalith.Builds</HexalithBuildsRoot>", false)]
    [InlineData("<HexalithEventStoreRoot Condition=\"'$(HexalithEventStoreRoot)'==''\">$(MSBuildThisFileDirectory)references/Hexalith.EventStore</HexalithEventStoreRoot>", false)]
    [InlineData("<HexalithBuildsFromSource Condition=\"'$(HexalithBuildsFromSource)'==''\">true</HexalithBuildsFromSource>", false)]
    [InlineData("<UseNuGetDeps Condition=\"'$(UseNuGetDeps)'==''\">true</UseNuGetDeps>", false)]
    public async Task ExactBuildsBootstrapDefaultIsPermittedAsync(string body, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("Directory.Build.props", "<Project><PropertyGroup>" + body + "</PropertyGroup></Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies relative project references are resolved against the owning project directory.</summary>
    /// <param name="include">The project reference path.</param>
    /// <param name="passes">Whether it stays inside the active module.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("../Hexalith.Sample.Contracts/Hexalith.Sample.Contracts.csproj", true)]
    [InlineData("..\\Hexalith.Sample.Contracts\\Hexalith.Sample.Contracts.csproj", true)]
    [InlineData("../../../../Hexalith.Other/src/Other.csproj", false)]
    [InlineData("../../../references/Hexalith.Other/references/Hexalith.Nested/Other.csproj", false)]
    public async Task IntraRepositoryProjectReferencesPassAsync(string include, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("src/libraries/Hexalith.Sample/Hexalith.Sample.csproj", "<Project><ItemGroup><ProjectReference Include=\"" + include + "\" /></ItemGroup></Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies script comments and ordinary repository probes do not select dependencies.</summary>
    /// <param name="path">The script path.</param>
    /// <param name="source">The script contents.</param>
    /// <param name="passes">Whether it avoids dependency selection.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("eng/check.ps1", "# Test-Path '../Hexalith.Other'\nTest-Path $repoRoot", true)]
    [InlineData("eng/check.py", "# os.path.exists('../Hexalith.Other')\nos.path.exists(repoRoot)", true)]
    [InlineData("eng/check.sh", "# if [ -d ../Hexalith.Other ]; then UseNuGetDeps=false; fi\ntest -d data", true)]
    [InlineData("eng/check.cs", "bool exists = File.Exists(Path.Combine(repoRoot, \"references/Hexalith.Other\"));", false)]
    [InlineData("eng/check.py", "os.path.exists('../Hexalith.Other')", false)]
    [InlineData("eng/check.ps1", "Test-Path $HexalithOtherFromSource", false)]
    [InlineData("eng/check.cs", "var source = FindSiblingRoot();", false)]
    [InlineData("eng/check.cs", "var source = GetSourcePath(\"../Hexalith.Other\");", false)]
    [InlineData("eng/check.cs", "var source = File.Exists(\n    \"references/Hexalith.Other/Other.csproj\"\n);", false)]
    [InlineData("eng/check.cs", "var source = Directory.Exists(\n    @\"references\\Hexalith.Other\"\n);", false)]
    [InlineData("eng/check.cs", "var source = File.Exists(\n    Path.Combine(repoRoot,\n        \"references/Hexalith.Other/Other.csproj\"));", false)]
    [InlineData("eng/check.cs", "var source = File.Exists(\n    HexalithTenantsBasePath\n);", false)]
    [InlineData("eng/check.py", "os.path.exists(\n    \"../Hexalith.Other\"\n)", false)]
    [InlineData("eng/check.py", "Path(\n    \"references/Hexalith.Other\"\n).exists()", false)]
    [InlineData("eng/check.ps1", "Test-Path (\n    'references/Hexalith.Other'\n)", false)]
    [InlineData("eng/check.ps1", "Test-Path `\n    '../Hexalith.Other'", false)]
    [InlineData("eng/check.sh", "test -d ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "test -d \\\n    references/Hexalith.Other", false)]
    [InlineData("eng/check.cs", "var exists = File.Exists(\"data\"); var path = \"references/Hexalith.Other\";", true)]
    [InlineData("eng/check.cs", "var exists = File.Exists(\"data\") && Other(\"references/Hexalith.Other\");", true)]
    [InlineData("eng/check.cs", "var exists = File.Exists(\n    Path.Combine(repoRoot, \"Hexalith.Tenants.slnx\"));", true)]
    [InlineData("eng/check.py", "os.path.exists(\"data\")\nPath(\"references/Hexalith.Other\")", true)]
    [InlineData("eng/check.ps1", "Test-Path data\n$path = '../Hexalith.Other'", true)]
    [InlineData("eng/check.sh", "test -d data; printf '%s' '../Hexalith.Other'", true)]
    [InlineData("eng/check.sh", "test -d data && echo references/Hexalith.Other", true)]
    [InlineData("eng/check.cs", "if (Directory.Exists(Path.Combine(partiesRoot, \"references/Hexalith.McpCli\"))) UseNuGetDeps = true;", false)]
    [InlineData("eng/check.cs", "Assert.True(Directory.Exists(\"../Hexalith.Other\") && selected);", false)]
    [InlineData("eng/check.cs", "if (Path.Exists(\"../Hexalith.Other\")) UseNuGetDeps = true;", false)]
    [InlineData("eng/check.ps1", "[System.IO.Directory]::Exists('../Hexalith.Other')", false)]
    [InlineData("eng/check.ps1", "[IO.File]::Exists('../Hexalith.Other')", false)]
    [InlineData("eng/check.py", "os.path.isfile('../Hexalith.Other')", false)]
    [InlineData("eng/check.py", "os.path.lexists('../Hexalith.Other')", false)]
    [InlineData("eng/check.sh", "cp src/* out/\ntest -d ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "curl https://example.invalid; test -d ../Hexalith.Other", false)]
    [InlineData("eng/check.ps1", "Copy-Item src/* out/; Test-Path '../Hexalith.Other'", false)]
    [InlineData("eng/check.ps1", "Invoke-WebRequest https://example.invalid; Test-Path '../Hexalith.Other'", false)]
    [InlineData("eng/check.ps1", "$UseNuGetDeps = $true", false)]
    [InlineData("eng/check.sh", "dotnet build -p:UseHexalithProjectReferences=false", false)]
    [InlineData("eng/check.py", "UseHexalithProjectReferences = False", false)]
    [InlineData("eng/check.sh", "HexalithTenantsRoot=../Hexalith.Tenants", false)]
    [InlineData("eng/check.ps1", "# $UseNuGetDeps = $true\n$EnableFeature = $true", true)]
    [InlineData("eng/check.ps1", "Write-Host '$UseNuGetDeps = $true'", true)]
    [InlineData("eng/check.sh", "echo 'dotnet build -p:UseHexalithProjectReferences=false'", true)]
    [InlineData("eng/check.py", "# UseHexalithProjectReferences = False\nmessage = 'UseNuGetDeps = True'", true)]
    [InlineData("eng/check.sh", "dotnet build -p:Configuration=Release", true)]
    [InlineData("eng/check.cs", "Assert.True(ready); if (Directory.Exists(\"../Hexalith.EventStore\")) return Select();", false)]
    [InlineData("eng/check.cs", "var found = Directory.Exists(\"../Hexalith.EventStore\"); found.ShouldBeTrue();", false)]
    [InlineData("eng/check.cs", "var source = new[] { Path.Combine(repoRoot, \"..\", \"Hexalith.Builds\", \"Props\") }.Where(File.Exists);", false)]
    [InlineData("eng/check.cs", "if (!File.Exists(source)) source = Path.Combine(RepositoryRoot, \"..\", \"Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(repoRoot, \"..\", \"references\", \"Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(\nrepoRoot, \"..\",\n\"Hexalith.Builds\", \"Props\");", false)]
    [InlineData("eng/check.py", "source = os.path.join(repo_root, '..', 'Hexalith.Builds')", false)]
    [InlineData("eng/check.py", "source = os.path.join(repo_root, '..', 'references', 'Hexalith.Builds')", false)]
    [InlineData("eng/check.ps1", "$source = Join-Path $root '..' 'Hexalith.Builds'", false)]
    [InlineData("eng/check.ps1", "$source = Join-Path $root '..' 'references' 'Hexalith.Builds'", false)]
    [InlineData("eng/check.cs", "// Path.Combine(repoRoot, \"..\", \"Hexalith.Builds\");", true)]
    [InlineData("eng/check.cs", "var text = \"Path.Combine(root, \\\"..\\\", \\\"Hexalith.Builds\\\")\";", true)]
    [InlineData("eng/check.cs", "var path = Path.Combine(repoRoot, \"..\", \"artifacts\");", true)]
    [InlineData("eng/check.cs", "var path = Path.Combine(repoRoot, \"..\", \"data\", \"Hexalith.Other\");", true)]
    [InlineData("eng/check.py", "# os.path.join(root, '..', 'Hexalith.Builds')\nsource = os.path.join(root, '..', 'data')", true)]
    [InlineData("eng/check.ps1", "# Join-Path $root '..' 'Hexalith.Builds'\nJoin-Path $root '..' 'data'", true)]
    [InlineData("eng/check.ps1", "[IO.Path]::Exists('../Hexalith.Other')", false)]
    [InlineData("eng/check.sh", "test -L ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "test -r ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "test -s ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "test -x ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "[ -L ../Hexalith.Other ]", false)]
    [InlineData("eng/check.sh", "test -r data", true)]
    [InlineData("eng/check.sh", "test -n ../Hexalith.Other", true)]
    [InlineData("eng/check.cs", "if (Directory.Exists(Path.Combine(root, \"references\", \"Hexalith.Other\"))) return Select();", false)]
    [InlineData("eng/check.cs", "if (File.Exists(Path.Combine(root, \"references\", \"Hexalith.Other\", \"Other.csproj\"))) return Select();", false)]
    [InlineData("eng/check.py", "os.path.exists(os.path.join(root, 'references', 'Hexalith.Other'))", false)]
    [InlineData("eng/check.ps1", "Test-Path (Join-Path $root 'references' 'Hexalith.Other')", false)]
    [InlineData("eng/check.cs", "Directory.Exists(Path.Combine(root, \"references\", \"Hexalith.Other\")).ShouldBeFalse();", true)]
    [InlineData("eng/check.cs", "Assert.True(Directory.Exists(Path.Combine(root, \"references\", \"Hexalith.Other\")));", true)]
    [InlineData("eng/check.cs", "var path = Path.Combine(root, \"references\", \"Hexalith.Other\");", true)]
    [InlineData("eng/check.cs", "if (Directory.Exists(Path.Combine(root, \"references\", \"data\"))) return Select();", true)]
    [InlineData("eng/check.sh", "[ ! -d ../Hexalith.Other ]", false)]
    [InlineData("eng/check.sh", "test ! -d ../Hexalith.Other", false)]
    [InlineData("eng/check.sh", "[ ! -d data ]", true)]
    [InlineData("eng/check.sh", "test ! -d data", true)]
    [InlineData("eng/check.sh", "dotnet build \"-p:UseNuGetDeps=false\"", false)]
    [InlineData("eng/check.ps1", "dotnet build '-p:UseHexalithProjectReferences=false'", false)]
    [InlineData("eng/check.sh", "dotnet build \"--property:HexalithTenantsRoot=../Hexalith.Tenants\"", false)]
    [InlineData("eng/check.sh", "dotnet build \"-p:Configuration=Release\"", true)]
    [InlineData("eng/check.sh", "echo 'dotnet build \"-p:UseNuGetDeps=false\"'", true)]
    [InlineData("eng/check.sh", "echo dotnet build \"-p:UseNuGetDeps=false\"", true)]
    [InlineData("eng/check.sh", "# dotnet build \"-p:UseNuGetDeps=false\"\necho ready", true)]
    [InlineData("eng/check.ps1", "Write-Host 'dotnet build \"-p:UseNuGetDeps=false\"'", true)]
    [InlineData("eng/check.sh", "if [[ $UseNuGetDeps == true ]]; then echo package; fi", true)]
    [InlineData("eng/check.py", "if UseHexalithProjectReferences == False:\n    print(\"package\")", true)]
    [InlineData("eng/check.sh", "if ready; then dotnet build \"-p:UseNuGetDeps=false\"; fi", false)]
    [InlineData("eng/check.sh", "for c in Debug; do dotnet build \"-p:UseNuGetDeps=false\"; done", false)]
    [InlineData("eng/check.sh", "exec dotnet build \"-p:UseNuGetDeps=false\"", false)]
    [InlineData("eng/check.sh", "dotnet build \"/p:UseNuGetDeps=false\"", false)]
    [InlineData("eng/check.sh", "dotnet build \"-property:UseNuGetDeps=false\"", false)]
    [InlineData("eng/check.ps1", "dotnet build '/property:UseHexalithProjectReferences=false'", false)]
    [InlineData("eng/check.sh", "dotnet build \"-p:Configuration=Release;UseNuGetDeps=false\"", false)]
    [InlineData("eng/check.sh", "dotnet build \"-property:Configuration=Release\"", true)]
    [InlineData("eng/check.sh", "dotnet build \"-p:Configuration=Release;Platform=x64\"", true)]
    [InlineData("eng/check.cs", "var source = Path.Join(repoRoot, \"..\", \"Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(repoRoot, \"../Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(repoRoot, @\"..\\..\\Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(repoRoot, \"../..\", \"Hexalith.Builds\");", false)]
    [InlineData("eng/check.cs", "var source = Path.Combine(repoRoot, \"../references/Hexalith.Builds\");", false)]
    [InlineData("eng/check.ps1", "$source = Join-Path $root '../Hexalith.Builds'", false)]
    [InlineData("eng/check.py", "source = os.path.join(repo_root, '../Hexalith.Builds')", false)]
    [InlineData("eng/check.cs", "var path = Path.Join(repoRoot, \"..\", \"artifacts\");", true)]
    [InlineData("eng/check.cs", "var path = Path.Combine(repoRoot, \"../artifacts/Hexalith.Other\");", true)]
    [InlineData("eng/check.cs", "var path = Path.Combine(repoRoot, \"src/Hexalith.Sample\");", true)]
    [InlineData("eng/check.cs", "var text = \"Path.Join(root, \\\"../Hexalith.Builds\\\")\";", true)]
    public async Task DependencyProbesRequireDependencyTargetsAsync(string path, string source, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write(path, source);
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies imports cannot discover parent build files outside the active module.</summary>
    /// <param name="path">The importing build file.</param>
    /// <param name="method">The MSBuild lookup method.</param>
    /// <param name="passes">Whether its search starts inside the module.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("Directory.Build.props", "GetPathOfFileAbove", false)]
    [InlineData("Directory.Build.props", "GetDirectoryNameOfFileAbove", false)]
    [InlineData("tests/Directory.Build.props", "GetPathOfFileAbove", true)]
    [InlineData("tests/Directory.Build.props", "GetDirectoryNameOfFileAbove", true)]
    public async Task AncestorImportsStayWithinActiveModuleAsync(string path, string method, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string arguments = method == "GetPathOfFileAbove"
            ? "'Directory.Build.props', '$(MSBuildThisFileDirectory)../'"
            : "'$(MSBuildThisFileDirectory)../', 'Directory.Build.props'";
        workspace.Write(path, "<Project><Import Project=\"$([MSBuild]::" + method + "(" + arguments + "))\" /></Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies malformed frozen inventories cannot grant source-scan exclusions.</summary>
    /// <param name="mutation">The invalid record or confinement case.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("pending-owner")]
    [InlineData("pending-consumer")]
    [InlineData("not-frozen")]
    [InlineData("src-directory")]
    [InlineData("another-project")]
    [InlineData("another-project-uppercase")]
    [InlineData("eng-directory")]
    [InlineData("src-directory-uppercase")]
    [InlineData("missing-replacement-plan")]
    [InlineData("missing-retirement-plan")]
    [InlineData("empty-gates")]
    [InlineData("missing-producerAdoption")]
    [InlineData("missing-ownerReviewedSourcePackageDeployedParity")]
    [InlineData("missing-eventStoreAd22ExactSubjectRemovalAuthority")]
    [InlineData("blank-producerAdoption")]
    [InlineData("blank-ownerReviewedSourcePackageDeployedParity")]
    [InlineData("blank-eventStoreAd22ExactSubjectRemovalAuthority")]
    [InlineData("kind-mismatch")]
    [InlineData("empty-consumers")]
    [InlineData("duplicate-record")]
    public async Task InvalidHostingRecordsGrantNoExemptionAsync(string mutation)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string directory = mutation switch
        {
            "src-directory" => "src",
            "src-directory-uppercase" => "Src",
            "eng-directory" => "eng",
            _ => "src/Hexalith.Sample.AppHost",
        };
        string project = directory + "/Hexalith.Sample.AppHost.csproj";
        workspace.Write(project, "<Project />");
        workspace.Write(directory + "/Program.cs", "app.MapSubscribeHandler();");
        if (mutation is "another-project" or "another-project-uppercase")
        {
            workspace.Write(directory + (mutation == "another-project" ? "/Other.csproj" : "/Other.CSPROJ"), "<Project />");
        }

        JsonObject record = HostingRecord(project);
        if (mutation == "pending-owner")
        {
            record["owner"] = "Pending: module owner";
        }

        if (mutation == "pending-consumer")
        {
            record["consumers"] = new JsonArray("PENDING: identify consumer");
        }

        if (mutation == "not-frozen")
        {
            record["state"] = "active";
        }

        switch (mutation)
        {
            case "missing-replacement-plan": _ = record.Remove("replacementPlan"); break;
            case "missing-retirement-plan": _ = record.Remove("retirementPlan"); break;
            case "empty-gates": record["gates"] = new JsonObject(); break;
            case "kind-mismatch": record["kind"] = "Aspire"; break;
            case "empty-consumers": record["consumers"] = new JsonArray(); break;
            default:
                if (mutation.StartsWith("missing-", StringComparison.Ordinal))
                {
                    _ = record["gates"]!.AsObject().Remove(mutation["missing-".Length..]);
                }
                else if (mutation.StartsWith("blank-", StringComparison.Ordinal))
                {
                    record["gates"]![mutation["blank-".Length..]] = " ";
                }

                break;
        }

        JsonArray projects = new(record);
        if (mutation == "duplicate-record")
        {
            projects.Add(record.DeepClone());
        }

        workspace.Write("module/hosting-inventory.json", new JsonObject { ["schemaVersion"] = 1, ["projects"] = projects }.ToJsonString());
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "hosting-inventory" && !row.Passed);
        report.Rows.ShouldContain(row => row.Requirement == "dapr-subscriptions" && !row.Passed && row.File == directory + "/Program.cs");
    }

    /// <summary>Verifies an exact top-level hosting directory can be frozen with explicit no-consumers.</summary>
    /// <param name="directory">The project's own directory, compared without case sensitivity.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("Hexalith.Sample.AppHost")]
    [InlineData("hexalith.sample.apphost")]
    public async Task TopLevelHostingDirectoryWithNoConsumersPassesAsync(string directory)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string project = directory + "/Hexalith.Sample.AppHost.csproj";
        workspace.Write(project, "<Project />");
        workspace.Write(directory + "/Program.cs", "app.MapSubscribeHandler();");
        workspace.Write("module/hosting-inventory.json", new JsonObject { ["schemaVersion"] = 1, ["projects"] = new JsonArray(HostingRecord(project)) }.ToJsonString());
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies local references keep valid guards while cross-module paths use only the mapping.</summary>
    /// <param name="body">The project or import declaration.</param>
    /// <param name="passes">Whether the reference preserves tool-owned dependency selection.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("<ProjectReference Include=\"../Hexalith.Sample.Contracts/Hexalith.Sample.Contracts.csproj\" Condition=\"Exists('../Hexalith.Sample.Contracts/Hexalith.Sample.Contracts.csproj')\" />", true)]
    [InlineData("<ProjectReference Include=\"..\\Hexalith.Sample.Contracts\\Hexalith.Sample.Contracts.csproj\" Condition=\"Exists('..\\Hexalith.Sample.Contracts\\Hexalith.Sample.Contracts.csproj')\" />", true)]
    [InlineData("<Import Project=\"../Hexalith.Sample.Contracts/Hexalith.Sample.props\" Condition=\"Exists('../Hexalith.Sample.Contracts/Hexalith.Sample.props')\" />", true)]
    [InlineData("<Import Project=\"$(MSBuildThisFileDirectory)Hexalith.Sample.props\" Condition=\"Exists('$(MSBuildThisFileDirectory)Hexalith.Sample.props')\" />", true)]
    [InlineData("<ProjectReference Include=\"../../../references/Hexalith.EventStore/src/Contracts.csproj\" />", false)]
    [InlineData("<Import Project=\"../../../references/Hexalith.Builds/Hexalith.Build.props\" />", false)]
    [InlineData("<ProjectReference Include=\"$(MSBuildThisFileDirectory)../../../references/Hexalith.EventStore/src/Contracts.csproj\" />", false)]
    [InlineData("<Import Project=\"$(MSBuildThisFileDirectory)../../../references/Hexalith.Builds/Hexalith.Build.props\" />", false)]
    [InlineData("<Import Project=\"../../../../Hexalith.Other/Hexalith.Other.props\" />", false)]
    [InlineData("<ProjectReference Include=\"$(HexalithEventStoreRoot)/src/Contracts.csproj\" Condition=\"'$(UseHexalithProjectReferences)' == 'true'\" />", true)]
    [InlineData("<PackageReference Include=\"Hexalith.EventStore.Contracts\" Condition=\"'$(UseHexalithProjectReferences)' != 'true'\" />", true)]
    [InlineData("<Import Project=\"$(HexalithBuildsRoot)/Hexalith.Build.props\" />", true)]
    [InlineData("<ProjectReference Include=\"$(HexalithTenantsBasePath)/Tenants.csproj\" Condition=\"'$(UseHexalithProjectReferences)' == 'true'\" />", true)]
    [InlineData("<ProjectReference Include=\"$(HexalithEventStoreRoot)/src/Contracts.csproj\" Condition=\"Exists('$(HexalithEventStoreRoot)/src/Contracts.csproj')\" />", false)]
    [InlineData("<Import Project=\"$(HexalithTenantsBasePath)/Tenants.props\" Condition=\"Exists('$(HexalithTenantsBasePath)/Tenants.props')\" />", false)]
    [InlineData("<PackageReference Include=\"Hexalith.EventStore.Contracts\" Condition=\"!Exists('use-source.marker')\" />", false)]
    [InlineData("<ProjectReference Include=\"$(HexalithEventStoreRoot)/Contracts.csproj\" Condition=\"Exists('use-source.marker')\" />", false)]
    [InlineData("<ProjectReference Include=\"  ../../../references/Hexalith.EventStore/src/Contracts.csproj  \" />", false)]
    [InlineData("<Import Project=\"\n    ../../../../Hexalith.Other/Hexalith.Other.props\n  \" />", false)]
    public async Task BuildReferencesDistinguishLocalAndMappedDependenciesAsync(string body, bool passes)
    {
        ArgumentNullException.ThrowIfNull(body);
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string contents = body.StartsWith("<Import ", StringComparison.Ordinal) ? body : "<ItemGroup>" + body + "</ItemGroup>";
        workspace.Write("src/libraries/Hexalith.Sample/Hexalith.Sample.csproj", "<Project>" + contents + "</Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies pin checks use the matched direct Platform path on case-sensitive filesystems.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task AlternateCasePlatformReferenceUsesItsActualPathAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "mv", "references/Hexalith.Platform", "references/PlatformTemp").ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "mv", "references/PlatformTemp", "references/hexalith.platform").ConfigureAwait(true);
        await workspace.TrackAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.Single(row => row.Requirement == "references").Passed.ShouldBeTrue();
        report.Rows.Single(row => row.Requirement == "tool-pin").Passed.ShouldBeTrue();
        report.IsValid.ShouldBeTrue(string.Join(Environment.NewLine, report.Rows.Where(row => !row.Passed)));
    }

    /// <summary>Verifies enabled and malformed roll-forward values fail through the public validator.</summary>
    /// <param name="value">The roll-forward JSON value.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("true")]
    [InlineData("\"false\"")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("0")]
    public async Task ToolPinRejectsEnabledOrMalformedRollForwardAsync(string value)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        string path = Path.Combine(workspace.Root, ".config/dotnet-tools.json");
        JsonNode pin = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true))!;
        pin["tools"]!["hexalith.builds.module.cli"]!["rollForward"] = JsonNode.Parse(value);
        workspace.Write(".config/dotnet-tools.json", pin.ToJsonString());
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "tool-pin" && !row.Passed && row.Reason.Contains("roll-forward", StringComparison.Ordinal));
    }

    /// <summary>Verifies tracked source links cannot make either source audit read outside the workspace.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task ExternalTrackedSourceSymlinkFailsBothAuditsReadOnlyAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        using PlatformManifestTestWorkspace outside = new();
        string external = Path.Combine(outside.Root, "External.cs");
        const string content = "class External {}";
        await File.WriteAllTextAsync(external, content, TestContext.Current.CancellationToken).ConfigureAwait(true);
        workspace.Write("src/External.cs", string.Empty);
        string link = Path.Combine(workspace.Root, "src/External.cs");
        File.Delete(link);
        _ = File.CreateSymbolicLink(link, external);
        await workspace.TrackAsync().ConfigureAwait(true);
        string before = (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output;
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        foreach (string requirement in new[] { "dependency-selectors", "dapr-subscriptions" })
        {
            report.Rows.ShouldContain(row => row.Requirement == requirement && !row.Passed && row.File == "src/External.cs" && row.Reason.Contains("physically escapes", StringComparison.Ordinal));
        }

        (await WorkspaceGitFixture.RunGitAsync(workspace.Root, "status", "--porcelain=v1", "--untracked-files=all").ConfigureAwait(true)).Output.ShouldBe(before);
        (await File.ReadAllTextAsync(external, TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldBe(content);
        new FileInfo(link).LinkTarget.ShouldBe(external);
    }

    /// <summary>Verifies custom build properties cannot hide dependency paths or ancestor discovery.</summary>
    /// <param name="path">The owning build file.</param>
    /// <param name="body">The property declaration.</param>
    /// <param name="passes">Whether the declaration stays within module-local paths.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("Directory.Packages.props", "<Hexalith1BuildPackageProps>$(MSBuildThisFileDirectory)references/Hexalith.Builds/Props/Directory.Packages.props</Hexalith1BuildPackageProps>", false)]
    [InlineData("Directory.Packages.props", "<Hexalith2BuildPackageProps>$(MSBuildThisFileDirectory)../Hexalith.Builds/Props/Directory.Packages.props</Hexalith2BuildPackageProps>", false)]
    [InlineData("Directory.Packages.props", "<Hexalith3BuildPackageProps>$(MSBuildThisFileDirectory)../../Hexalith.Builds/Props/Directory.Packages.props</Hexalith3BuildPackageProps>", false)]
    [InlineData("Module.Directory.Build.props", "<BuildPackageProps>$(MSBuildThisFileDirectory)../references/Hexalith.Builds/Hexalith.Build.props</BuildPackageProps>", false)]
    [InlineData("Module.Directory.Build.props", "<ParentDirectoryBuildProps>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</ParentDirectoryBuildProps>", false)]
    [InlineData("Module.Directory.Build.props", "<ParentDirectoryBuildProps>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Directory.Build.props'))</ParentDirectoryBuildProps>", false)]
    [InlineData("eng/Module.Directory.Build.props", "<ParentDirectoryBuildProps>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))</ParentDirectoryBuildProps>", true)]
    [InlineData("eng/Module.Directory.Build.props", "<ParentDirectoryBuildProps>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildThisFileDirectory)../', 'Directory.Build.props'))</ParentDirectoryBuildProps>", true)]
    [InlineData("Directory.Build.props", "<HexalithFrontComposerSourceAvailable Condition=\"Exists('$(HexalithFrontComposerRoot)/src/Contracts.csproj')\">true</HexalithFrontComposerSourceAvailable>", false)]
    [InlineData("Directory.Build.props", "<SourceAvailable Condition=\"Exists('references/Hexalith.Other/Other.props')\">true</SourceAvailable>", false)]
    [InlineData("Directory.Build.props", "<SourceAvailable Condition=\"Exists('../Hexalith.Other/Other.props')\">true</SourceAvailable>", false)]
    [InlineData("Directory.Build.props", "<SourceAvailable Condition=\"Exists('local/Optional.props')\">true</SourceAvailable>", true)]
    [InlineData("Directory.Build.props", "<OptionalAvailable Condition=\"Exists('../Optional.props')\">true</OptionalAvailable>", true)]
    [InlineData("Directory.Build.props", "<ProjectsRepositoryDirectory>$(MSBuildThisFileDirectory)</ProjectsRepositoryDirectory>", true)]
    [InlineData("src/libraries/Sample/Sample.csproj", "<ContractsDirectory>../Hexalith.Sample.Contracts</ContractsDirectory>", true)]
    [InlineData("Directory.Build.targets", @"<_ContainerProvenanceHttpsUrlPattern>\Ahttps://[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*(?::(?:[1-9][0-9]{0,3}|[1-5][0-9]{4}|6[0-4][0-9]{3}|65[0-4][0-9]{2}|655[0-2][0-9]|6553[0-5]))?(?:[/?#][^\s]*)?\z</_ContainerProvenanceHttpsUrlPattern>", true)]
    [InlineData("Directory.Build.targets", "<ContainerProvenanceRepositoryUrl>https://github.com/Hexalith/Hexalith.Parties</ContainerProvenanceRepositoryUrl>", true)]
    [InlineData("Directory.Build.targets", "<ContainerBaseImage>mcr.microsoft.com/dotnet/aspnet:10.0-alpine</ContainerBaseImage>", true)]
    [InlineData("Directory.Build.targets", "<ContainerImage>registry.hexalith.com/hexalith.parties:4.35.1</ContainerImage>", true)]
    [InlineData("Directory.Build.props", @"<BuildPackageProps>..\Hexalith.Builds\Props\Directory.Packages.props</BuildPackageProps>", false)]
    [InlineData("Directory.Build.props", "<DependencyDirectory>/tmp/Hexalith.Other/src</DependencyDirectory>", false)]
    [InlineData("Directory.Build.props", "<BaseOutputPath>$(MSBuildThisFileDirectory)../artifacts/</BaseOutputPath>", true)]
    [InlineData("Directory.Build.props", "<BaseIntermediateOutputPath>../obj/</BaseIntermediateOutputPath>", true)]
    [InlineData("Directory.Build.props", "<ArtifactsPath>$(MSBuildThisFileDirectory)../../artifacts/</ArtifactsPath>", true)]
    [InlineData("Directory.Packages.props", "<BuildPackageProps>\n    $(MSBuildThisFileDirectory)../Hexalith.Builds/Props/Directory.Packages.props\n  </BuildPackageProps>", false)]
    [InlineData("Directory.Packages.props", "<BuildPackageProps>  ../Hexalith.Builds/Props/Directory.Packages.props  </BuildPackageProps>", false)]
    [InlineData("Directory.Packages.props", "<BuildPackageProps>\n    $(MSBuildThisFileDirectory)eng/Hexalith.Sample.props\n  </BuildPackageProps>", true)]
    public async Task CustomBuildPropertiesCannotSelectDependencyPathsAsync(string path, string body, bool passes)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write(path, "<Project><PropertyGroup>" + body + "</PropertyGroup></Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "dependency-selectors").Passed.ShouldBe(passes);
    }

    /// <summary>Verifies unreadable non-Dapr file types affect only the dependency audit.</summary>
    /// <param name="extension">The supported dependency-only extension.</param>
    /// <param name="mutation">The file-read failure.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("csproj", "missing")]
    [InlineData("props", "missing")]
    [InlineData("targets", "missing")]
    [InlineData("ps1", "missing")]
    [InlineData("sh", "missing")]
    [InlineData("py", "missing")]
    [InlineData("csproj", "oversized")]
    [InlineData("props", "oversized")]
    [InlineData("targets", "oversized")]
    [InlineData("ps1", "oversized")]
    [InlineData("sh", "oversized")]
    [InlineData("py", "oversized")]
    [InlineData("csproj", "external")]
    [InlineData("props", "external")]
    [InlineData("targets", "external")]
    [InlineData("ps1", "external")]
    [InlineData("sh", "external")]
    [InlineData("py", "external")]
    public async Task DependencyOnlyFileFailuresDoNotEnterDaprAuditAsync(string extension, string mutation)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        using PlatformManifestTestWorkspace outside = new();
        string path = "eng/Audit." + extension;
        workspace.Write(path, mutation == "oversized" ? new string(' ', 1_048_577) : string.Empty);
        string fullPath = Path.Combine(workspace.Root, path);
        if (mutation == "external")
        {
            string external = Path.Combine(outside.Root, "External." + extension);
            await File.WriteAllTextAsync(external, "external content", TestContext.Current.CancellationToken).ConfigureAwait(true);
            File.Delete(fullPath);
            _ = File.CreateSymbolicLink(fullPath, external);
        }

        await workspace.TrackAsync().ConfigureAwait(true);
        if (mutation == "missing")
        {
            File.Delete(fullPath);
        }

        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        report.Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == path);
        report.Rows.Single(row => row.Requirement == "dapr-subscriptions").Passed.ShouldBeTrue();
    }

    /// <summary>Verifies YAML null spellings do not invent physical component defaults.</summary>
    /// <param name="format">The configuration extension.</param>
    /// <param name="value">The YAML null spelling.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("yaml", "Null")]
    [InlineData("yaml", "NULL")]
    [InlineData("yml", "Null")]
    [InlineData("yml", "NULL")]
    public async Task YamlNullDefaultsRemainUnsetAsync(string format, string value)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("appsettings." + format, "PubSubName: " + value + "\n");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).IsValid.ShouldBeTrue();
    }

    /// <summary>Verifies a missing pin reports the exact workspace-relative manifest.</summary>
    /// <param name="path">The missing module or direct Platform manifest.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData(".config/dotnet-tools.json")]
    [InlineData("references/Hexalith.Platform/.config/dotnet-tools.json")]
    public async Task MissingToolPinNamesItsActualManifestAsync(string path)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        File.Delete(Path.Combine(workspace.Root, path));
        PlatformAdoptionRow row = (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.Single(row => row.Requirement == "tool-pin");
        row.Passed.ShouldBeFalse();
        row.File.ShouldBe(path);
        row.Reason.ShouldContain(path);
    }

    /// <summary>Verifies declaration rows retain workspace-relative paths even when invoked below the root.</summary>
    /// <param name="missing">Whether the declaration is missing.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeclarationRowsUseWorkspaceRelativePathsAsync(bool missing)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        const string filename = "hexalith.module-manifest.v2.json";
        workspace.Write("module/" + filename, await File.ReadAllTextAsync(workspace.Manifest, TestContext.Current.CancellationToken).ConfigureAwait(true));
        await workspace.TrackAsync().ConfigureAwait(true);
        if (missing)
        {
            File.Delete(Path.Combine(workspace.Root, "module", filename));
        }

        PlatformAdoptionValidationResult report = await PlatformAdoptionValidator.ValidateAsync([filename], Path.Combine(workspace.Root, "module"), TestContext.Current.CancellationToken).ConfigureAwait(true);
        PlatformAdoptionRow row = report.Rows.Single(row => row.Requirement == "declaration");
        row.File.ShouldBe("module/" + filename);
        row.Passed.ShouldBe(!missing);
    }

    private static JsonObject HostingRecord(string project) => new()
    {
        ["path"] = project,
        ["kind"] = "AppHost",
        ["owner"] = "Sample owner",
        ["consumers"] = new JsonArray("none"),
        ["state"] = "frozen",
        ["replacementPlan"] = "pending",
        ["retirementPlan"] = "pending",
        ["gates"] = new JsonObject
        {
            ["producerAdoption"] = "pending",
            ["ownerReviewedSourcePackageDeployedParity"] = "pending",
            ["eventStoreAd22ExactSubjectRemovalAuthority"] = "pending",
        },
    };
}
