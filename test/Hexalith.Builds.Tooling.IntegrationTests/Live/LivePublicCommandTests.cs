// <copyright file="LivePublicCommandTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Proves public commands use the live composition engine across separate CLI processes.
/// </summary>
[Collection(LiveLane.Name)]
public sealed class LivePublicCommandTests
{
    /// <summary>
    /// A public run survives the invoking process, down removes its resources, and unsupported test execution stays non-passing.
    /// </summary>
    /// <returns>A task that completes after the live public commands.</returns>
    [Fact]
    public async Task RunDownAndTestUseRunScopedLiveCompositionAsync()
    {
        LiveGate.SkipUnlessEnabled();
        using CancellationTokenSource bound = new(TimeSpan.FromMinutes(12));
        string? runId = null;
        CompositionEngine engine = new(new CompositionEngineOptions(LiveRepository.AppHostAssembly, LiveRepository.DescriptorChildAssembly));
        try
        {
            CompositionProcessResult started = await InvokeAsync("run", null, bound.Token).ConfigureAwait(true);
            started.Started.ShouldBeTrue();
            started.ExitCode.ShouldBe(0, started.Output);
            using (JsonDocument output = JsonDocument.Parse(started.Output))
            {
                output.RootElement.GetProperty("status").GetString().ShouldBe("ready");
                runId = output.RootElement.GetProperty("runId").GetString();
            }

            CompositionRunPlanFactory.IsRunId(runId).ShouldBeTrue();
            CompositionRunState? state = await engine.StateStore.TryReadAsync(runId!, bound.Token).ConfigureAwait(true);
            state.ShouldNotBeNull().Status.ShouldBe(CompositionRunStatus.Ready);
            CompositionRunResources live = await engine.Scanner.FindAsync(runId!, bound.Token).ConfigureAwait(true);
            live.ContainerIds.Count.ShouldBe(1);
            live.ProcessIds.Count.ShouldBeGreaterThanOrEqualTo(8);

            CompositionProcessResult stopped = await InvokeAsync("down", runId, bound.Token).ConfigureAwait(true);
            stopped.ExitCode.ShouldBe(0, stopped.Output);
            using (JsonDocument output = JsonDocument.Parse(stopped.Output))
            {
                output.RootElement.GetProperty("runId").GetString().ShouldBe(runId);
            }

            (await engine.Scanner.FindAsync(runId!, bound.Token).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
            File.Exists(engine.StateStore.PathFor(runId!)).ShouldBeFalse();
            (await InvokeAsync("down", runId, bound.Token).ConfigureAwait(true)).ExitCode.ShouldBe(0);

            CompositionProcessResult tested = await InvokeAsync("test", null, bound.Token).ConfigureAwait(true);
            tested.ExitCode.ShouldBe(2, tested.Output);
            using (JsonDocument output = JsonDocument.Parse(tested.Output))
            {
                output.RootElement.GetProperty("status").GetString().ShouldBe("unavailable");
                output.RootElement.GetProperty("outcome").GetProperty("ruleId").GetString().ShouldBe("HXR029");
                string testRunId = output.RootElement.GetProperty("runId").GetString()!;
                (await engine.Scanner.FindAsync(testRunId, bound.Token).ConfigureAwait(true)).IsEmpty.ShouldBeTrue();
                File.Exists(engine.StateStore.PathFor(testRunId)).ShouldBeFalse();
            }
        }
        finally
        {
            if (runId is not null)
            {
                _ = await InvokeAsync("down", runId, CancellationToken.None).ConfigureAwait(true);
            }
        }
    }

    private static Task<CompositionProcessResult> InvokeAsync(string command, string? runId, CancellationToken cancellationToken)
    {
        List<string> arguments = ["exec", LiveRepository.DescriptorChildAssembly, command, "--manifest", LiveRepository.Manifest, "--output", "json"];
        if (command == "test")
        {
            arguments.AddRange(["--profile", "live"]);
        }

        if (runId is not null)
        {
            arguments.AddRange(["--run-id", runId]);
        }

        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            [CompositionPrerequisiteProbe.DaprHomeVariable] = Environment.GetEnvironmentVariable(CompositionPrerequisiteProbe.DaprHomeVariable)!,
        };
        return CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo("dotnet", arguments, LiveRepository.Root, environment),
            TimeSpan.FromMinutes(7),
            cancellationToken);
    }
}
