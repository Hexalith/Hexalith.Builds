// <copyright file="NativeMetadataPublicationTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Injects actual Linux sharing and storage failures into isolated metadata publishers.</summary>
public sealed class NativeMetadataPublicationTests
{
    /// <summary>Interrupted sharing recovers and failed durable flushes preserve the prior published document.</summary>
    /// <param name="scenario">The native fault injected into the standalone publisher.</param>
    /// <returns>The observed native injection and successful subprocess assertions.</returns>
    [Theory]
    [InlineData("eintr")]
    [InlineData("document-flush")]
    [InlineData("snapshot-flush")]
    [InlineData("document-flush-eintr")]
    [InlineData("document-flush-cancelled")]
    public async Task NativeMetadataFailuresPreserveCommittedBytesAsync(string scenario)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using PlatformManifestTestWorkspace workspace = new();
        string root = CompositionTestFiles.RepositoryRoot();
        string fixture = Path.Combine(root, "test", "fixtures", "module", "executable", "P0Fixture.NativeMetadataProbe");
        string probe = Path.Combine(workspace.Root, "native-probe.so");
        CompositionProcessResult compiled = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("cc", ["-shared", "-fPIC", "-Wall", "-Wextra", "-Werror", "-o", probe, Path.Combine(fixture, "native-probe.c"), "-ldl"], root, null), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        compiled.ExitCode.ShouldBe(0, compiled.Output + compiled.ErrorOutput);
        string harness = Path.Combine(root, "artifacts", "g4-fixture", "bin", "P0Fixture.NativeMetadataProbe", "P0Fixture.NativeMetadataProbe.dll");
        File.Exists(harness).ShouldBeTrue("The ordinary Module.Tests build must build the native metadata harness.");
        string log = Path.Combine(workspace.Root, "native-observations.log");
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["LD_PRELOAD"] = probe,
            ["HEXALITH_NATIVE_PROBE_MODE"] = scenario,
            ["HEXALITH_NATIVE_PROBE_LOG"] = log,
        };
        CompositionProcessResult observed = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("dotnet", [harness, probe, Path.Combine(workspace.Root, "publication"), scenario], root, environment), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
        observed.ExitCode.ShouldBe(0, observed.Output + observed.ErrorOutput);
        observed.Output.ShouldContain("Passed: " + scenario);
        string[] events = await File.ReadAllLinesAsync(log, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string[] expected = scenario switch
        {
            "eintr" => ["injected-EINTR", "shared-lock-ok"],
            "document-flush-eintr" => ["injected-flush-EINTR", "durable-flush-ok"],
            "document-flush-cancelled" => ["injected-flush-EINTR"],
            _ => ["injected-flush-failure"],
        };
        events.ShouldBe(expected);
    }
}