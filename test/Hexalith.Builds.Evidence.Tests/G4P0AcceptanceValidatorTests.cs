// <copyright file="G4P0AcceptanceValidatorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

using System.Text.Json;

using Hexalith.Builds.Evidence.Cli;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the public <c>hexalith.g4-p0-acceptance.v1</c> validation contract against the curated corpus.
/// </summary>
public sealed class G4P0AcceptanceValidatorTests
{
    /// <summary>
    /// Gets every negative acceptance control by file name.
    /// </summary>
    public static TheoryData<string> NegativeControls
    {
        get
        {
            TheoryData<string> controls = [];
            foreach (string path in Directory.EnumerateFiles(EvidenceFixturePath.Get("acceptance/negative"), "*.json")
                .Where(path => !path.EndsWith(".expected.json", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal))
            {
                controls.Add(Path.GetFileName(path));
            }

            return controls;
        }
    }

    /// <summary>
    /// Checks the complete contract sample passes through the public command.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task PositiveRecordMatchesItsExpectationAsync() =>
        await AssertMatchesExpectationAsync("acceptance/positive/p0-acceptance.json").ConfigureAwait(true);

    /// <summary>
    /// Checks each negative control fails with its declared stable rule.
    /// </summary>
    /// <param name="control">The negative control file name.</param>
    /// <returns>A task that completes after the assertion.</returns>
    [Theory]
    [MemberData(nameof(NegativeControls))]
    public async Task NegativeControlMatchesItsExpectationAsync(string control) =>
        await AssertMatchesExpectationAsync("acceptance/negative/" + control).ConfigureAwait(true);

    /// <summary>
    /// Checks the corpus covers every acceptance rule.
    /// </summary>
    [Fact]
    public void NegativeControlsCoverEveryAcceptanceRule()
    {
        HashSet<string> rules = [.. Directory.EnumerateFiles(EvidenceFixturePath.Get("acceptance/negative"), "*.expected.json")
            .Select(path => JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("outcomeRuleId").GetString()!)];

        rules.ShouldBe(["HXE200", "HXE201", "HXE202", "HXE203", "HXE204", "HXE205", "HXE206", "HXE207", "HXE208"], ignoreOrder: true);
    }

    /// <summary>
    /// Checks a missing JSON record fails closed instead of falling back to YAML parsing.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingRecordFailsClosedAsync()
    {
        (int exitCode, JsonElement output) = await InvokeAsync(EvidenceFixturePath.Get("acceptance/positive/absent.json")).ConfigureAwait(true);

        exitCode.ShouldBe(6);
        output.GetProperty("outcome").GetProperty("ruleId").GetString().ShouldBe("HXE200");
    }

    private static async Task AssertMatchesExpectationAsync(string relativePath)
    {
        string path = EvidenceFixturePath.Get(relativePath);
        using JsonDocument expected = JsonDocument.Parse(await File.ReadAllTextAsync(
            path[..^".json".Length] + ".expected.json",
            TestContext.Current.CancellationToken).ConfigureAwait(true));
        JsonElement expectation = expected.RootElement;

        (int exitCode, JsonElement output) = await InvokeAsync(path).ConfigureAwait(true);

        exitCode.ShouldBe(expectation.GetProperty("exitCode").GetInt32(), relativePath);
        output.GetProperty("status").GetString().ShouldBe(expectation.GetProperty("status").GetString(), relativePath);
        JsonElement outcome = output.GetProperty("outcome");
        outcome.GetProperty("exitCode").GetString().ShouldBe(expectation.GetProperty("outcomeExitCode").GetString(), relativePath);
        outcome.GetProperty("phase").GetString().ShouldBe(expectation.GetProperty("phase").GetString(), relativePath);
        outcome.GetProperty("category").GetString().ShouldBe(expectation.GetProperty("category").GetString(), relativePath);
        outcome.GetProperty("ruleId").ToString().ShouldBe(expectation.GetProperty("outcomeRuleId").ToString(), relativePath);
        output.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("ruleId").GetString())
            .ShouldBe(expectation.GetProperty("ruleIds").EnumerateArray().Select(rule => rule.GetString()), relativePath);
    }

    private static async Task<(int ExitCode, JsonElement Output)> InvokeAsync(string path)
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await EvidenceCommandApplication.InvokeAsync(
                    ["validate", path, "--output", "json"],
                    standardOutput,
                    standardError,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);
                standardError.ToString().ShouldBeEmpty();
                using JsonDocument document = JsonDocument.Parse(standardOutput.ToString());
                return (exitCode, document.RootElement.Clone());
            }
        }
    }
}