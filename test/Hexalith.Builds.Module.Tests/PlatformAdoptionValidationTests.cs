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
    [Theory]
    [InlineData("missing-manifest", "declaration")]
    [InlineData("malformed-manifest", "declaration")]
    [InlineData("minimum-dependencies", "declaration")]
    [InlineData("missing-reference", "references")]
    [InlineData("declared-dependency", "references")]
    [InlineData("missing-gitlink", "gitlinks")]
    [InlineData("stale-gitlink", "gitlinks")]
    [InlineData("missing-pin", "tool-pin")]
    [InlineData("malformed-pin", "tool-pin")]
    [InlineData("wrong-pin", "tool-pin")]
    [InlineData("missing-inventory", "hosting-inventory")]
    [InlineData("malformed-inventory", "hosting-inventory")]
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
            case "declared-dependency": ChangeDependencies("[\"eventstore\",\"tenants\",\"memories\",\"folders\"]"); break;
            case "missing-reference": workspace.Write(".gitmodules", File.ReadAllText(Path.Combine(workspace.Root, ".gitmodules")).Replace("path = references/Hexalith.Tenants", "path = references/Hexalith.Unrelated", StringComparison.Ordinal)); break;
            case "missing-gitlink": _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "update-index", "--force-remove", "references/Hexalith.Tenants").ConfigureAwait(true); break;
            case "stale-gitlink":
                _ = await WorkspaceGitFixture.RunGitAsync(Path.Combine(workspace.Root, "references/Hexalith.Tenants"), "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "fixture stale checkout").ConfigureAwait(true);
                break;
            case "missing-pin": File.Delete(Path.Combine(workspace.Root, ".config/dotnet-tools.json")); break;
            case "malformed-pin": workspace.Write(".config/dotnet-tools.json", "[]"); break;
            case "wrong-pin": workspace.Write(".config/dotnet-tools.json", File.ReadAllText(Path.Combine(workspace.Root, ".config/dotnet-tools.json")).Replace("4.34.0", "4.33.1", StringComparison.Ordinal)); break;
            case "missing-inventory": File.Delete(Path.Combine(workspace.Root, "module/hosting-inventory.json")); break;
            case "malformed-inventory": workspace.Write("module/hosting-inventory.json", "[]"); break;
            case "unlisted-host": workspace.Write("src/Hexalith.Sample.AppHost/Hexalith.Sample.AppHost.csproj", "<Project />"); break;
            case "selector": workspace.Write("Directory.Build.props", "<Project><PropertyGroup><HexalithEventStoreFromSource Condition=\"Exists('local.csproj')\">true</HexalithEventStoreFromSource></PropertyGroup></Project>"); break;
            case "sibling": workspace.Write("Directory.Build.props", "<Project><PropertyGroup><HexalithEventStoreRoot>../Hexalith.EventStore</HexalithEventStoreRoot></PropertyGroup></Project>"); break;
            case "helper": workspace.Write("eng/resolve-source.ps1", "if (Test-Path '../Hexalith.EventStore') { $UseNuGetDeps = $false }"); break;
            case "literal-dapr": workspace.Write("src/Client.cs", "await dapr.PublishEventAsync(\"pubsub\", topic, data);"); break;
            case "subscription": workspace.Write("src/Consumer.cs", "[Topic(\"pubsub\", \"events\")] public void Receive() {}"); break;
        }

        if (mutation != "missing-gitlink" && mutation != "stale-gitlink")
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
        workspace.Write("module/hosting-inventory.json", File.ReadAllText(Path.Combine(workspace.Root, "module/hosting-inventory.json")).Replace(project, "Directory.Build.props", StringComparison.Ordinal));
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "hosting-inventory" && !row.Passed);
    }

    /// <summary>Verifies reports contain machine rows and human PASS/FAIL labels while legacy JSON stays unchanged.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task ReportsRenderRowsAndLeaveLegacyJsonUnchangedAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        PlatformAdoptionValidationResult report = await workspace.ValidateAsync().ConfigureAwait(true);
        ToolCommandResult result = new("validated", ToolOutcome.Passed(), report.Diagnostics) { Adoption = report.Rows, AdoptionScope = PlatformAdoptionValidator.Scope };
        using StringWriter human = new();
        await ToolDiagnosticFormatter.WriteAsync(human, result, ToolOutputFormat.Human, TestContext.Current.CancellationToken).ConfigureAwait(true);
        human.ToString().ShouldContain("PASS references file=.gitmodules");
        human.ToString().ShouldContain("SM-2 remains separately unverified");
        using StringWriter json = new();
        await ToolDiagnosticFormatter.WriteAsync(json, result, ToolOutputFormat.Json, TestContext.Current.CancellationToken).ConfigureAwait(true);
        using JsonDocument parsed = JsonDocument.Parse(json.ToString());
        parsed.RootElement.GetProperty("adoption").GetArrayLength().ShouldBe(9);
        using StringWriter legacy = new();
        await ToolDiagnosticFormatter.WriteAsync(legacy, new ToolCommandResult("validated", ToolOutcome.Passed(), []), ToolOutputFormat.Json, TestContext.Current.CancellationToken).ConfigureAwait(true);
        legacy.ToString().ShouldNotContain("adoption");
    }
}
