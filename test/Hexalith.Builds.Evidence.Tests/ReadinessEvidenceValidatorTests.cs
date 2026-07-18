// <copyright file="ReadinessEvidenceValidatorTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Evidence;
using Hexalith.Builds.Tooling.Filesystem;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies strict, deterministic readiness-evidence validation.
/// </summary>
public sealed class ReadinessEvidenceValidatorTests
{
    /// <summary>
    /// Verifies the canonical positive matrix resolves defaults, coverage, artifacts, and Markdown identities.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task PositiveFixturePassesAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("positive/readiness.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Status.ShouldBe("passed");
        result.Outcome.ExitCode.ShouldBe(ToolExitCode.Success);
        result.Diagnostics.ShouldBeEmpty();
    }

    /// <summary>
    /// Verifies duplicate YAML keys fail before schema or policy evaluation.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DuplicateYamlKeyFailsAsEvidenceSchemaAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("negative/duplicate-key.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Outcome.ExitCode.ShouldBe(ToolExitCode.EvidenceSchemaOrPolicy);
        result.Outcome.Category.ShouldBe(ToolFailureCategory.EvidenceSchema);
        result.Outcome.RuleId.ShouldBe("HXE001");
        result.Diagnostics.Count.ShouldBe(1);
    }

    /// <summary>
    /// Verifies an unsupported schema fails before business rule evaluation.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task UnsupportedSchemaFailsAsEvidenceSchemaAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("negative/unsupported-schema.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Outcome.Category.ShouldBe(ToolFailureCategory.EvidenceSchema);
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXE004");
    }

    /// <summary>
    /// Verifies unknown schema fields fail closed without a policy evaluation.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task UnknownFieldFailsAsEvidenceSchemaAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("negative/unknown-field.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Outcome.Category.ShouldBe(ToolFailureCategory.EvidenceSchema);
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXE003");
    }

    /// <summary>
    /// Verifies a hash-matched artifact with only a schema marker cannot satisfy a passed readiness claim.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task IncompleteModuleRunArtifactFailsClosedAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("negative/invalid-artifact.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Outcome.Category.ShouldBe(ToolFailureCategory.EvidencePolicy);
        result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXE148");
    }

    /// <summary>
    /// Verifies an artifact link that points outside its repository root is rejected before it is read.
    /// </summary>
    [Fact]
    public void EscapingArtifactSymlinkIsRejectedByRepositoryResolver()
    {
        string repositoryRoot = Path.Combine(Path.GetTempPath(), $"hexalith-builds-evidence-root-{Guid.NewGuid():N}");
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-evidence-external-{Guid.NewGuid():N}");

        try
        {
            _ = Directory.CreateDirectory(repositoryRoot);
            _ = Directory.CreateDirectory(externalDirectory);
            File.WriteAllText(Path.Combine(externalDirectory, "artifact.json"), "{}");
            _ = Directory.CreateSymbolicLink(Path.Combine(repositoryRoot, "evidence"), externalDirectory);

            RepositoryPathResolver.TryResolveExistingFile(repositoryRoot, "evidence/artifact.json", out _).ShouldBeFalse();
        }
        finally
        {
            Directory.Delete(repositoryRoot, true);
            Directory.Delete(externalDirectory, true);
        }
    }

    /// <summary>
    /// Verifies policy controls expose deterministic, metadata-only diagnostics.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task PolicyControlsFailWithStableSortedDiagnosticsAsync()
    {
        ToolCommandResult result = await ReadinessEvidenceValidator.ValidateAsync(
            EvidenceFixturePath.Get("negative/policy-controls.yaml"),
            TestContext.Current.CancellationToken).ConfigureAwait(true);

        result.Outcome.Category.ShouldBe(ToolFailureCategory.EvidencePolicy);
        result.Outcome.RuleId.ShouldBe("HXE141");
        IReadOnlyList<string> ruleIds = [.. result.Diagnostics.Select(diagnostic => diagnostic.RuleId)];
        ruleIds.ShouldContain("HXE124");
        ruleIds.ShouldContain("HXE140");
        ruleIds.ShouldContain("HXE141");
        ruleIds.ShouldContain("HXE142");
        ruleIds.ShouldContain("HXE145");
        ruleIds.ShouldContain("HXE151");
        result.Diagnostics[0].Row.ShouldBe("release-failed");
        result.Diagnostics.All(diagnostic => !diagnostic.Message.Contains("0123456789abcdef", StringComparison.Ordinal)).ShouldBeTrue();
    }
}