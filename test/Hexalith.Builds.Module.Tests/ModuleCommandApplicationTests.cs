// <copyright file="ModuleCommandApplicationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the public module command contract.
/// </summary>
public sealed class ModuleCommandApplicationTests
{
    private const string _manifestJson =
        """
        {
          "schema": "hexalith.module-manifest.v1",
          "id": "command-fixture",
          "modules": [
            {
              "id": "module-a",
              "descriptorAssembly": "assemblies/module-a.dll",
              "dependencies": [],
              "domain": "module-a",
              "applicationId": "module-a",
              "resourceId": "module-a"
            },
            {
              "id": "module-b",
              "descriptorAssembly": "assemblies/module-b.dll",
              "dependencies": ["module-a"],
              "domain": "module-b",
              "applicationId": "module-b",
              "resourceId": "module-b"
            }
          ],
          "platform": {
            "eventStoreVersion": "3.70.0",
            "daprRuntimeVersion": "1.18.0",
            "daprSdkVersion": "1.18.4",
            "frontComposerVersion": "4.0.1"
          },
          "ui": { "descriptorAssembly": "assemblies/ui.dll" },
          "profiles": {
            "full": {
              "fixture": "fixtures/full.json",
              "classes": ["pure-domain"]
            }
          }
        }
        """;

    /// <summary>
    /// Verifies manifest validation completes before any runtime lifecycle phase.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RunWithMissingManifestReturnsUsageExitCodeAsync()
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["run", "--manifest", "missing-manifest.json", "--output", "json"],
                    standardOutput,
                    standardError,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
                standardOutput.ToString().ShouldContain("HXM005");
                standardError.ToString().ShouldBeEmpty();
            }
        }
    }

    /// <summary>
    /// Verifies parser failures use the stable JSON diagnostics contract instead of parser-owned output.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingRequiredOptionWritesStableJsonUsageDiagnosticAsync()
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["run", "--output", "json"],
                    standardOutput,
                    standardError,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
                standardOutput.ToString().ShouldContain("HXC001");
                standardOutput.ToString().ShouldContain("\"phase\":\"Usage\"");
                standardError.ToString().ShouldBeEmpty();
            }
        }
    }

    /// <summary>
    /// Verifies an unknown profile fails before runtime composition.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task TestWithUnknownProfileReturnsUsageExitCodeAsync()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        ["test", "--manifest", Path.Combine(directory, "manifest.json"), "--profile", "missing"],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
                    standardOutput.ToString().ShouldContain("HXC002");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies raw profile secrets cannot be retained when profile validation emits evidence.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task SecretBearingProfileIsRedactedFromEvidenceAsync()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "test",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--profile",
                            "Bearer profile-redaction-control",
                            "--evidence",
                            "evidence/profile-failure.json",
                            "--output",
                            "json",
                        ],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
                    standardOutput.ToString().ShouldContain("HXC003");
                    standardOutput.ToString().ShouldNotContain("profile-redaction-control");
                    string evidence = await File.ReadAllTextAsync(
                        Path.Combine(directory, "evidence", "profile-failure.json"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true);
                    evidence.ShouldNotContain("profile-redaction-control");
                    evidence.ShouldNotContain("Bearer");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies an idempotent down command does not claim a runtime was running.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownWithValidManifestReturnsSuccessAsync()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "down",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--filter",
                            "Bearer fixture-redaction-control",
                            "--evidence",
                            "evidence/module-run.json",
                        ],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.Success);
                    standardOutput.ToString().ShouldContain("HXI001");
                    standardOutput.ToString().ShouldNotContain("passed");
                    string evidencePath = Path.Combine(directory, "evidence", "module-run.json");
                    File.Exists(evidencePath).ShouldBeTrue();
                    byte[] evidence = await File.ReadAllBytesAsync(
                        evidencePath,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);
                    evidence[^1].ShouldBe((byte)'\n');
                    string evidenceText = await File.ReadAllTextAsync(
                        evidencePath,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);
                    evidenceText.ShouldContain("hexalith.module-run-evidence.v1");
                    evidenceText.ShouldNotContain("fixture-redaction-control");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies an unresolved live platform prerequisite remains unavailable rather than passing or skipping.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RunWithValidManifestReturnsExplicitPrerequisiteUnavailableAsync()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        ["run", "--manifest", Path.Combine(directory, "manifest.json"), "--output", "json"],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable);
                    standardOutput.ToString().ShouldContain("HXR002");
                    standardOutput.ToString().ShouldNotContain("passed");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies a later evidence failure cannot replace the first causal prerequisite outcome.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RunWithUnavailablePrerequisiteAndInvalidEvidenceRetainsPrerequisiteExitCodeAsync()
    {
        string directory = CreateFixtureDirectory();
        string invalidEvidencePath = Path.Combine(Path.GetTempPath(), $"hexalith-evidence-{Guid.NewGuid():N}.json");

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "run",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--evidence",
                            invalidEvidencePath,
                            "--output",
                            "json",
                        ],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable);
                    standardOutput.ToString().ShouldContain("HXR002");
                    standardOutput.ToString().ShouldContain("HXE001");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies an evidence failure after successful cleanup is a distinct non-passing evidence outcome.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownWithInvalidEvidenceReturnsEvidenceExitCodeAsync()
    {
        string directory = CreateFixtureDirectory();
        string invalidEvidencePath = Path.Combine(Path.GetTempPath(), $"hexalith-evidence-{Guid.NewGuid():N}.json");

        try
        {
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "down",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--evidence",
                            invalidEvidencePath,
                            "--output",
                            "json",
                        ],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.EvidenceSchemaOrPolicy);
                    standardOutput.ToString().ShouldContain("HXE001");
                    standardOutput.ToString().ShouldNotContain("passed");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    /// <summary>
    /// Verifies an evidence directory symlink cannot write outside the consumer repository.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task DownWithEscapingEvidenceSymlinkReturnsEvidenceFailureAsync()
    {
        string directory = CreateFixtureDirectory();
        string externalDirectory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-evidence-external-{Guid.NewGuid():N}");

        try
        {
            _ = Directory.CreateDirectory(externalDirectory);
            _ = Directory.CreateSymbolicLink(Path.Combine(directory, "evidence"), externalDirectory);
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "down",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--evidence",
                            "evidence/escaped.json",
                            "--output",
                            "json",
                        ],
                        standardOutput,
                        standardError,
                        TestContext.Current.CancellationToken).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.EvidenceSchemaOrPolicy);
                    standardOutput.ToString().ShouldContain("HXE001");
                    File.Exists(Path.Combine(externalDirectory, "escaped.json")).ShouldBeFalse();
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
            Directory.Delete(externalDirectory, true);
        }
    }

    /// <summary>
    /// Verifies a public command cancellation retains canonical cancelled evidence rather than a pass.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task CancelledDownWritesCancelledEvidenceAsync()
    {
        string directory = CreateFixtureDirectory();

        try
        {
            using CancellationTokenSource cancellationTokenSource = new();
            await cancellationTokenSource.CancelAsync().ConfigureAwait(true);
            StringWriter standardOutput = new();
            await using (standardOutput.ConfigureAwait(true))
            {
                StringWriter standardError = new();
                await using (standardError.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        [
                            "down",
                            "--manifest",
                            Path.Combine(directory, "manifest.json"),
                            "--evidence",
                            "evidence/cancelled.json",
                            "--output",
                            "json",
                        ],
                        standardOutput,
                        standardError,
                        cancellationTokenSource.Token).ConfigureAwait(true);

                    exitCode.ShouldBe((int)ToolExitCode.Cancelled);
                    standardOutput.ToString().ShouldContain("HXC130");
                    string evidence = await File.ReadAllTextAsync(
                        Path.Combine(directory, "evidence", "cancelled.json"),
                        TestContext.Current.CancellationToken).ConfigureAwait(true);
                    evidence.ShouldContain("\"finalStatus\":\"cancelled\"");
                    evidence.ShouldNotContain("\"finalStatus\":\"completed\"");
                }
            }
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static string CreateFixtureDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"hexalith-builds-command-{Guid.NewGuid():N}");
        _ = Directory.CreateDirectory(Path.Combine(directory, "assemblies"));
        _ = Directory.CreateDirectory(Path.Combine(directory, "fixtures"));
        File.WriteAllText(Path.Combine(directory, "assemblies", "module-a.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "assemblies", "module-b.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "assemblies", "ui.dll"), string.Empty);
        File.WriteAllText(Path.Combine(directory, "fixtures", "full.json"), "{}");
        File.WriteAllText(Path.Combine(directory, "manifest.json"), _manifestJson);
        return directory;
    }
}