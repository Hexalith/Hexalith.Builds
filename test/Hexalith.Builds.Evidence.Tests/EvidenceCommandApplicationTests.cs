// <copyright file="EvidenceCommandApplicationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Evidence.Tests;

using Hexalith.Builds.Evidence.Cli;
using Hexalith.Builds.Tooling.Diagnostics;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the public evidence command contract.
/// </summary>
public sealed class EvidenceCommandApplicationTests
{
    /// <summary>
    /// Verifies the command validates canonical evidence and writes its JSON result.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task ValidatePositiveFixtureWritesPassedJsonAsync()
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await EvidenceCommandApplication.InvokeAsync(
                    ["validate", EvidenceFixturePath.Get("positive/readiness.yaml"), "--output", "json"],
                    standardOutput,
                    standardError,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.Success);
                standardOutput.ToString().ShouldContain("\"status\":\"passed\"");
                standardError.ToString().ShouldBeEmpty();
            }
        }
    }

    /// <summary>
    /// Verifies duplicate YAML failures use the evidence exit code and a stable JSON rule identifier.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task ValidateDuplicateKeyWritesStableJsonDiagnosticAsync()
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await EvidenceCommandApplication.InvokeAsync(
                    ["validate", EvidenceFixturePath.Get("negative/duplicate-key.yaml"), "--output", "json"],
                    standardOutput,
                    standardError,
                    TestContext.Current.CancellationToken).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.EvidenceSchemaOrPolicy);
                standardOutput.ToString().ShouldContain("HXE001");
                standardError.ToString().ShouldBeEmpty();
            }
        }
    }

    /// <summary>
    /// Verifies a missing required argument uses stable JSON usage diagnostics.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task MissingEvidencePathWritesStableJsonUsageDiagnosticAsync()
    {
        StringWriter standardOutput = new();
        await using (standardOutput.ConfigureAwait(true))
        {
            StringWriter standardError = new();
            await using (standardError.ConfigureAwait(true))
            {
                int exitCode = await EvidenceCommandApplication.InvokeAsync(
                    ["validate", "--output", "json"],
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
}