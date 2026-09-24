// <copyright file="PublicCompositionCommandTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Security.Cryptography;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the public command boundary for executable runtime composition.
/// </summary>
public sealed class PublicCompositionCommandTests
{
    /// <summary>
    /// A targeted down evidence record preserves the exact public run identity argument.
    /// </summary>
    [Fact]
    public void DownEvidenceIncludesRequestedRunIdentity()
    {
        ModuleRunEvidence evidence = ModuleRunEvidenceFactory.Create(
            ModuleInvocationCommand.Down,
            ExecutableManifest(),
            null,
            null,
            null,
            new ToolCommandResult("completed", ToolOutcome.Passed(), []),
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            CompositionTestFiles.RunId,
            CompositionTestFiles.RunId);

        evidence.Invocation.Command.ShouldContain("--run-id " + CompositionTestFiles.RunId);
    }

    /// <summary>
    /// A validated executable manifest reaches the live prerequisite probe and fails closed before state mutation.
    /// </summary>
    /// <returns>A task that completes after the public result is checked.</returns>
    [Fact]
    public async Task ExecutableRunReportsMissingDaprWithoutCreatingStateAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = ExecutableManifest();
            string docker = CompositionTestFiles.CreateStubDocker(root);
            CompositionEngineOptions options = new(Path.Combine(root, "apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
            {
                DockerCommand = docker,
                DaprHome = Path.Combine(root, "missing-dapr"),
                StateDirectory = Path.Combine(root, "state"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
            };
            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["run", "--manifest", manifest, "--output", "json"],
                    output,
                    TextWriter.Null,
                    TestContext.Current.CancellationToken,
                    typeof(ModuleCommandApplication).Assembly.Location,
                    options).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable);
                output.ToString().ShouldContain("\"ruleId\":\"HXR011\"");
                output.ToString().ShouldNotContain("HXR003");
                Directory.Exists(options.StateDirectory).ShouldBeFalse();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Public down uses the engine state for only the matching manifest and remains idempotent.
    /// </summary>
    /// <returns>A task that completes after both public invocations.</returns>
    [Fact]
    public async Task ExecutableDownRemovesMatchingRunAndLeavesForeignRunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string manifest = ExecutableManifest();
            string docker = CompositionTestFiles.CreateStubDocker(root);
            CompositionEngineOptions options = new(Path.Combine(root, "apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
            {
                DockerCommand = docker,
                StateDirectory = Path.Combine(root, "state"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
            };
            CompositionEngine engine = new(options);
            string hash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(manifest, TestContext.Current.CancellationToken).ConfigureAwait(true)));
            const string siblingRunId = "abcdef0123456789abcdef0123456789";
            const string foreignRunId = "fedcba9876543210fedcba9876543210";
            await engine.StateStore.WriteAsync(
                new CompositionRunState(
                    CompositionRunState.SupportedSchema,
                    CompositionTestFiles.RunId,
                    CompositionRunStatus.Ready,
                    hash,
                    Path.Combine(options.WorkspaceRoot, CompositionTestFiles.RunId),
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await engine.StateStore.WriteAsync(
                new CompositionRunState(
                    CompositionRunState.SupportedSchema,
                    siblingRunId,
                    CompositionRunStatus.Ready,
                    hash,
                    Path.Combine(options.WorkspaceRoot, siblingRunId),
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            await engine.StateStore.WriteAsync(
                new CompositionRunState(
                    CompositionRunState.SupportedSchema,
                    foreignRunId,
                    CompositionRunStatus.Ready,
                    "FOREIGN",
                    Path.Combine(options.WorkspaceRoot, foreignRunId),
                    null,
                    null,
                    null,
                    DateTimeOffset.UtcNow),
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            StringWriter ambiguousOutput = new();
            await using (ambiguousOutput.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["down", "--manifest", manifest, "--output", "json"],
                    ambiguousOutput,
                    TextWriter.Null,
                    TestContext.Current.CancellationToken,
                    typeof(ModuleCommandApplication).Assembly.Location,
                    options).ConfigureAwait(true);
                exitCode.ShouldBe((int)ToolExitCode.UsageOrManifest);
                ambiguousOutput.ToString().ShouldContain("HXR024");
            }

            File.Exists(engine.StateStore.PathFor(CompositionTestFiles.RunId)).ShouldBeTrue();
            File.Exists(engine.StateStore.PathFor(siblingRunId)).ShouldBeTrue();
            string[] downArguments = ["down", "--manifest", manifest, "--run-id", CompositionTestFiles.RunId, "--output", "json"];
            foreach (int attempt in Enumerable.Range(0, 2))
            {
                StringWriter output = new();
                await using (output.ConfigureAwait(true))
                {
                    int exitCode = await ModuleCommandApplication.InvokeAsync(
                        downArguments,
                        output,
                        TextWriter.Null,
                        TestContext.Current.CancellationToken,
                        typeof(ModuleCommandApplication).Assembly.Location,
                        options).ConfigureAwait(true);
                    exitCode.ShouldBe((int)ToolExitCode.Success, output.ToString());
                }
            }

            File.Exists(engine.StateStore.PathFor(CompositionTestFiles.RunId)).ShouldBeFalse();
            File.Exists(engine.StateStore.PathFor(siblingRunId)).ShouldBeTrue();
            File.Exists(engine.StateStore.PathFor(foreignRunId)).ShouldBeTrue();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    private static string ExecutableManifest()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Hexalith.Builds.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "test", "fixtures", "module", "executable", "hexalith.module-manifest.v1.json");
    }
}
