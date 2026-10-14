// <copyright file="AppHostRunnerValidationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using System.Text.Json;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Shouldly;

using Xunit;

/// <summary>Checks the real validation branch without starting a distributed application.</summary>
[Collection("AppHost validation")]
public sealed class AppHostRunnerValidationTests
{
    /// <summary>Verifies all ordinary composition failures become a message-only exit 3.</summary>
    /// <param name="kind">The composition failure kind.</param>
    /// <returns>The validation assertion.</returns>
    [Theory]
    [InlineData("json")]
    [InlineData("provenance")]
    [InlineData("io")]
    [InlineData("format")]
    public async Task ValidationReportsEveryNonFatalCompositionFailureAsync(string kind)
    {
        string root = CompositionTestFiles.CreateDirectory();
        TextWriter original = Console.Error;
        StringWriter errors = new();
        try
        {
            Console.SetError(errors);
            Exception refusal = kind switch
            {
                "json" => new JsonException("local-profile.json: malformed profile"),
                "provenance" => new KeyNotFoundException("provenance.json: missing owner"),
                "io" => new IOException("components.yaml: unreadable file"),
                _ => new FormatException("components.yaml: invalid YAML value"),
            };
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            int exit = await AppHostRunner.RunAsync(
                ["--validate-platform"],
                plan,
                CompositionSigningKey.Create(),
                (_, _, _) => throw refusal).ConfigureAwait(true);
            exit.ShouldBe(3);
            errors.ToString().ShouldBe(refusal.Message + Environment.NewLine);
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            Console.SetError(original);
            await errors.DisposeAsync().ConfigureAwait(true);
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies successful validation returns before building or starting the application.</summary>
    /// <returns>The validation assertion.</returns>
    [Fact]
    public async Task ValidPreflightReturnsWithoutStartingResourcesAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            PreflightStartTrap startup = new();
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);
            bool composed = false;
            int exit = await AppHostRunner.RunAsync(
                ["--validate-platform"],
                plan,
                CompositionSigningKey.Create(),
                (builder, _, _) =>
                {
                    composed = true;
                    builder.Resources.ShouldBeEmpty();
                    _ = builder.Services.AddSingleton<IHostedService>(startup);
                }).ConfigureAwait(true);
            exit.ShouldBe(0);
            composed.ShouldBeTrue();
            startup.Starts.ShouldBe(0);
            File.Exists(plan.ReadinessPath).ShouldBeFalse();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
