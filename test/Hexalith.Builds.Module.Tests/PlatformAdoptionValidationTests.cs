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
    [InlineData("missing-reference", "references")]
    [InlineData("declared-dependency", "references")]
    [InlineData("missing-gitlink", "gitlinks")]
    [InlineData("stale-gitlink", "gitlinks")]
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
            case "declared-dependency": ChangeDependencies("[\"eventstore\",\"tenants\",\"memories\",\"folders\"]"); break;
            case "missing-reference": workspace.Write(".gitmodules", (await File.ReadAllTextAsync(Path.Combine(workspace.Root, ".gitmodules"), TestContext.Current.CancellationToken).ConfigureAwait(true)).Replace("path = references/Hexalith.Tenants", "path = references/Hexalith.Unrelated", StringComparison.Ordinal)); break;
            case "missing-gitlink": _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "update-index", "--force-remove", "references/Hexalith.Tenants").ConfigureAwait(true); break;
            case "stale-gitlink":
                _ = await WorkspaceGitFixture.RunGitAsync(Path.Combine(workspace.Root, "references/Hexalith.Tenants"), "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "fixture stale checkout").ConfigureAwait(true);
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
        PlatformAdoptionValidationResult noGit = await PlatformAdoptionValidator.ValidateAsync([outside.Save()], outside.Root, TestContext.Current.CancellationToken).ConfigureAwait(true);
        noGit.Rows.ShouldContain(row => row.Requirement == "workspace" && !row.Passed);
        noGit.Rows.Select(row => row.Requirement).Distinct().Count().ShouldBe(9);
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

    /// <summary>Verifies a declared direct self-reference fails even with a valid recorded gitlink.</summary>
    /// <returns>The assertion task.</returns>
    [Fact]
    public async Task DirectSelfReferenceIsRejectedAsync()
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        _ = await WorkspaceGitFixture.RunGitAsync(workspace.Root, "-c", "protocol.file.allow=always", "submodule", "add", new Uri(Path.Combine(workspace.Root, "references/Hexalith.Dep")).AbsoluteUri, "references/Hexalith.Sample").ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "references" && !row.Passed && row.Reason.Contains("self reference", StringComparison.Ordinal));
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
    public async Task ToolOwnedPropertiesAndSelectingImportsFailAsync(string body)
    {
        using PlatformAdoptionTestWorkspace workspace = await PlatformAdoptionTestWorkspace.CreateAsync().ConfigureAwait(true);
        workspace.Write("Directory.Build.props", "<Project>" + body + "</Project>");
        await workspace.TrackAsync().ConfigureAwait(true);
        (await workspace.ValidateAsync().ConfigureAwait(true)).Rows.ShouldContain(row => row.Requirement == "dependency-selectors" && !row.Passed && row.File == "Directory.Build.props");
    }

    /// <summary>Verifies ordinary path construction and content-root access are not existence probes.</summary>
    /// <param name="source">Ordinary module source.</param>
    /// <returns>The assertion task.</returns>
    [Theory]
    [InlineData("var path = Path.Combine(environment.ContentRootPath, \"data\");")]
    [InlineData("var path = Path.Combine(\"Hexalith.Sample\", \"data\");")]
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
            parsed.RootElement.GetProperty("adoption").GetArrayLength().ShouldBe(9);
        }

        StringWriter legacy = new();
        await using (legacy.ConfigureAwait(true))
        {
            await ToolDiagnosticFormatter.WriteAsync(legacy, new ToolCommandResult("validated", ToolOutcome.Passed(), []), ToolOutputFormat.Json, TestContext.Current.CancellationToken).ConfigureAwait(true);
            legacy.ToString().ShouldNotContain("adoption");
        }
    }
}
