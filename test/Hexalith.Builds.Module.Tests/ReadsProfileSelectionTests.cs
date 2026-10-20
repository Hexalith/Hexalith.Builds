// <copyright file="ReadsProfileSelectionTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies native reads selection through the public command without launching infrastructure.</summary>
public sealed class ReadsProfileSelectionTests
{
    /// <summary>Binds the unique named native test in its declared mode before an unavailable startup seam.</summary>
    /// <param name="executor">The declared native test platform.</param>
    /// <param name="mode">The matching command and declaration mode.</param>
    /// <returns>The selection and nonpassing startup assertions.</returns>
    [Theory]
    [InlineData("vstest", "source")]
    [InlineData("vstest", "package")]
    [InlineData("mtp", "source")]
    [InlineData("mtp", "package")]
    public async Task NamedReadsSelectsItsNativeExecutorAndModeAsync(string executor, string mode)
    {
        using PlatformManifestTestWorkspace workspace = new();
        Declare(workspace, executor, mode);
        string manifest = workspace.Save();
        PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        ModuleTestSelection? observed = null;
        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root, mode) with
        {
            SelectedPlatformStartOverride = (_, selected, _) =>
            {
                starts++;
                observed = selected;
                ToolDiagnostic diagnostic = new("HXR011", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, "Offline selection regression; infrastructure was not started.", "prerequisite");
                return Task.FromResult(new CompositionStartResult(new ToolCommandResult("unavailable", ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.PrerequisiteUnavailable), [diagnostic]), null, null));
            },
        };
        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exit = await ModuleCommandApplication.InvokeAsync(
                ["test", "--manifest", manifest, "--profile", "reads", "--mode", mode, "--output", "json"],
                output,
                TextWriter.Null,
                TestContext.Current.CancellationToken,
                "unused-child.dll",
                options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable, output.ToString());
            starts.ShouldBe(1);
            ModuleTestSelection selected = observed.ShouldNotBeNull();
            selected.ModuleId.ShouldBe("sample");
            selected.Name.ShouldBe("reads");
            selected.Executor.ShouldBe(executor);
            selected.Entry.ShouldBe("Reads.Tests.csproj");
            selected.Mode.ShouldBe(mode);
            selected.MatchesSource(manifest).ShouldBeTrue();
            using JsonDocument result = JsonDocument.Parse(output.ToString());
            result.RootElement.GetProperty("status").GetString().ShouldBe("unavailable");
            result.RootElement.GetProperty("outcome").GetProperty("ruleId").GetString().ShouldBe("HXR011");
            Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
            Directory.Exists(options.StateDirectory).ShouldBeFalse();
        }
    }

    /// <summary>Rejects missing, ambiguous, filtered or mode-mismatched reads selections before startup.</summary>
    /// <param name="scenario">The unsupported selection.</param>
    /// <param name="ruleId">The stable causal rule.</param>
    /// <returns>The refusal and absence-of-startup assertions.</returns>
    [Theory]
    [InlineData("missing-profile", "HXC001")]
    [InlineData("unknown-profile", "HXC002")]
    [InlineData("absent-tests", "HXC002")]
    [InlineData("duplicate-name", "HXP003")]
    [InlineData("ambiguous-modules", "HXC002")]
    [InlineData("source-package", "HXC002")]
    [InlineData("package-source", "HXC002")]
    [InlineData("filter", "HXC002")]
    [InlineData("empty-filter", "HXC002")]
    public async Task InvalidReadsSelectionNeverStartsAsync(string scenario, string ruleId)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string declaredMode = scenario == "package-source" ? "package" : "source";
        string commandMode = scenario == "source-package" ? "package" : "source";
        Declare(workspace, "mtp", declaredMode);
        JsonObject module = workspace.Document["modules"]![0]!.AsObject();
        switch (scenario)
        {
            case "absent-tests":
                _ = module.Remove("tests");
                break;
            case "duplicate-name":
                module["tests"]!.AsArray().Add(module["tests"]![0]!.DeepClone());
                break;
            case "ambiguous-modules":
                workspace.Document["modules"]!.AsArray().Add(JsonNode.Parse(module.ToJsonString().Replace("sample", "other", StringComparison.Ordinal)));
                break;
            default:
                scenario.ShouldBeOneOf("missing-profile", "unknown-profile", "source-package", "package-source", "filter", "empty-filter");
                break;
        }

        string manifest = workspace.Save();
        if (scenario != "duplicate-name")
        {
            PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).IsValid.ShouldBeTrue();
        }

        int starts = 0;
        CompositionEngineOptions options = Options(workspace.Root, commandMode) with
        {
            SelectedPlatformStartOverride = (_, _, _) =>
            {
                starts++;
                throw new InvalidOperationException("Invalid reads selection reached startup.");
            },
        };
        List<string> arguments = ["test", "--manifest", manifest, "--mode", commandMode, "--output", "json"];
        if (scenario != "missing-profile")
        {
            arguments.AddRange(["--profile", scenario == "unknown-profile" ? "absent" : "reads"]);
        }

        if (scenario is "filter" or "empty-filter")
        {
            arguments.AddRange(["--filter", scenario == "filter" ? "Story=6.1" : string.Empty]);
        }

        StringWriter output = new();
        await using (output.ConfigureAwait(true))
        {
            int exit = await ModuleCommandApplication.InvokeAsync(
                [.. arguments], output, TextWriter.Null, TestContext.Current.CancellationToken, "unused-child.dll", options).ConfigureAwait(true);
            exit.ShouldBe((int)ToolExitCode.UsageOrManifest, output.ToString());
            starts.ShouldBe(0);
            using JsonDocument result = JsonDocument.Parse(output.ToString());
            result.RootElement.GetProperty("status").GetString().ShouldBe("failed");
            result.RootElement.GetProperty("outcome").GetProperty("ruleId").GetString().ShouldBe(ruleId);
            result.RootElement.GetProperty("diagnostics").EnumerateArray().ShouldContain(diagnostic => diagnostic.GetProperty("ruleId").GetString() == ruleId);
            Directory.Exists(options.WorkspaceRoot).ShouldBeFalse();
            Directory.Exists(options.StateDirectory).ShouldBeFalse();
        }
    }

    private static CompositionEngineOptions Options(string root, string mode) => new("unused-apphost", "unused-child")
    {
        RootManifestCheckOverride = (_, _) => Task.FromResult(true),
        SourceMapping = new SourceMapping(mode == "source" ? WorkspaceMode.Source : WorkspaceMode.Package, root, "sample", [new SourceMappingEntry("sample", mode, root)], false),
        WorkspaceRoot = Path.Combine(root, "workspace"),
        StateDirectory = Path.Combine(root, "state"),
        ContinuousIntegrationOverride = true,
    };

    private static void Declare(PlatformManifestTestWorkspace workspace, string executor, string mode)
    {
        // These entries are selection inputs only; no native execution or persisted result is claimed.
        File.WriteAllText(Path.Combine(workspace.Root, "Reads.Tests.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        JsonObject reads = new()
        {
            ["name"] = "reads",
            ["executor"] = executor,
            ["entry"] = "Reads.Tests.csproj",
            ["reportFormat"] = "trx",
            ["executionTimeoutSeconds"] = 60,
            ["stopTimeoutSeconds"] = 10,
            ["mode"] = mode,
        };
        JsonObject other = reads.DeepClone().AsObject();
        other["name"] = "full";
        other["executor"] = executor == "mtp" ? "vstest" : "mtp";
        workspace.Document["modules"]![0]!["tests"] = new JsonArray(reads, other);
    }
}
