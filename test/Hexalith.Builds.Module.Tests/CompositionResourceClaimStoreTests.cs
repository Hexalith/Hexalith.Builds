// <copyright file="CompositionResourceClaimStoreTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Verifies atomic cross-instance exclusive claims and ownership-limited release.</summary>
public sealed class CompositionResourceClaimStoreTests
{
    /// <summary>Child model conflicts retain the real blocking identity for the parent refusal.</summary>
    /// <returns>The metadata-only child refusal assertions.</returns>
    [Fact]
    public async Task ChildConflictPreservesTheBlockingOwnerAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string owner = CompositionRunPlanFactory.NewRunId();
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root) with
            {
                DaprHome = CompositionTestFiles.CreateDaprHome(root, CompositionToolchainPins.DaprCliVersion, CompositionToolchainPins.DaprRuntimeVersion),
            };
            MemoryStream input = new();
            await using System.Runtime.CompilerServices.ConfiguredAsyncDisposable inputLifetime = input.ConfigureAwait(true);
            int exit = await Hexalith.Builds.ModuleHosts.AppHost.AppHostRunner.RunAsync(
                ["--validate-platform"],
                plan,
                "private-fixture-key",
                (_, _, _) => throw new CompositionResourceClaimConflictException("actual-exclusive-resource", owner),
                input).ConfigureAwait(true);
            exit.ShouldBe(3);
            CompositionStartupFailure failure = (await CompositionDocumentStore.TryReadAsync<CompositionStartupFailure>(CompositionStartupFailure.PathFor(plan.Workspace), TestContext.Current.CancellationToken).ConfigureAwait(true)).ShouldNotBeNull();
            failure.RuleId.ShouldBe("HXR031");
            failure.BlockingRunId.ShouldBe(owner);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Actual exclusive conflicts name the owner while independent selections can coexist.</summary>
    [Fact]
    public void ConflictNamesOwnerAndRollsBackOnlyTheRejectedAcquisition()
    {
        string root = CompositionTestFiles.CreateDirectory();
        string first = CompositionRunPlanFactory.NewRunId();
        string second = CompositionRunPlanFactory.NewRunId();
        try
        {
            CompositionResourceClaimStore owner = new(root);
            CompositionResourceClaimStore peer = new(root);
            owner.Acquire(first, ["tcp:127.0.0.1:12345"]);
            peer.Acquire(second, ["tcp:127.0.0.1:12346"]);
            CompositionResourceClaimConflictException conflict = Should.Throw<CompositionResourceClaimConflictException>(() => peer.Acquire(second, ["a-new-exclusive-resource", "tcp:127.0.0.1:12345"]));
            conflict.OwnerRunId.ShouldBe(first);
            conflict.Message.ShouldContain(first);
            owner.Acquire(first, ["a-new-exclusive-resource"]);
            peer.ReleaseOwned(second);
            peer.Acquire(second, ["tcp:127.0.0.1:12346"]);
            Should.Throw<CompositionResourceClaimConflictException>(() => peer.Acquire(second, ["tcp:127.0.0.1:12345"])).OwnerRunId.ShouldBe(first);
            owner.ReleaseOwned(first);
            peer.Acquire(second, ["tcp:127.0.0.1:12345"]);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Real processes contend through production transactions; terminating a holder releases its lock without deleting peer claims.</summary>
    /// <returns>The cross-process winner and interruption assertions.</returns>
    [Fact]
    public async Task SubprocessContentionAndInterruptedHolderPreservePeerClaimsAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string project = Path.Combine(root, "claim-child");
            _ = Directory.CreateDirectory(project);
            string assembly = System.Security.SecurityElement.Escape(typeof(CompositionResourceClaimStore).Assembly.Location);
            await File.WriteAllTextAsync(Path.Combine(project, "child.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><Reference Include=\"Hexalith.Builds.Tooling\"><HintPath>" + assembly + "</HintPath></Reference></ItemGroup></Project>", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(project, "Program.cs"),
                """
                using Hexalith.Builds.Tooling.Runtime;
                string root = args[0], owner = args[1], mode = args[2], signal = args[3];
                var store = new CompositionResourceClaimStore(root);
                if (mode == "hold") { store.Acquire(owner, Held()); return 9; }
                while (!File.Exists(signal)) await Task.Delay(5);
                try { store.Acquire(owner, ["cross-process-exclusive"]); return 0; }
                catch (CompositionResourceClaimConflictException conflict) { Console.WriteLine(conflict.OwnerRunId); return 23; }
                IEnumerable<string> Held() { File.WriteAllText(signal, "transaction held"); Thread.Sleep(Timeout.Infinite); yield return "never-published"; }
                """,
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionProcessResult build = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("dotnet", ["build", Path.Combine(project, "child.csproj"), "-c", "Debug", "-m:1", "--nologo", "-v:q"], project, null), TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken).ConfigureAwait(true);
            build.ExitCode.ShouldBe(0, build.Output);
            string executable = Path.Combine(project, "bin", "Debug", "net10.0", "child.dll");
            string[] owners = [CompositionRunPlanFactory.NewRunId(), CompositionRunPlanFactory.NewRunId()];
            string signal = Path.Combine(root, "contend");
            using Process first = Process.Start(CompositionProcess.CreateStartInfo("dotnet", [executable, root, owners[0], "contend", signal], project, null))!;
            using Process second = Process.Start(CompositionProcess.CreateStartInfo("dotnet", [executable, root, owners[1], "contend", signal], project, null))!;
            await File.WriteAllTextAsync(signal, "start", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await Task.WhenAll(first.WaitForExitAsync(TestContext.Current.CancellationToken), second.WaitForExitAsync(TestContext.Current.CancellationToken)).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken).ConfigureAwait(true);
            new[] { first.ExitCode, second.ExitCode }.Order().ShouldBe([0, 23]);
            string winner = first.ExitCode == 0 ? owners[0] : owners[1];
            Process loser = first.ExitCode == 0 ? second : first;
            (await loser.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken).ConfigureAwait(true)).Trim().ShouldBe(winner);
            foreach (string path in Directory.GetFiles(Path.Combine(root, "claims"), "*.json"))
            {
                using JsonDocument complete = JsonDocument.Parse(await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken).ConfigureAwait(true));
                complete.RootElement.GetProperty("OwnerRunId").GetString().ShouldBe(winner);
            }

            string held = Path.Combine(root, "held");
            using Process holder = Process.Start(CompositionProcess.CreateStartInfo("dotnet", [executable, root, owners[0], "hold", held], project, null))!;
            try
            {
                for (int i = 0; i < 200 && !File.Exists(held); i++)
                {
                    await Task.Delay(10, TestContext.Current.CancellationToken).ConfigureAwait(true);
                }

                File.Exists(held).ShouldBeTrue();
                Task waiting = Task.Run(() => new CompositionResourceClaimStore(root).Acquire(owners[1], ["after-interruption"]), TestContext.Current.CancellationToken);
                await Task.Delay(100, TestContext.Current.CancellationToken).ConfigureAwait(true);
                waiting.IsCompleted.ShouldBeFalse();
                holder.Kill(entireProcessTree: true);
                await holder.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                await waiting.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken).ConfigureAwait(true);
                Should.Throw<CompositionResourceClaimConflictException>(() => new CompositionResourceClaimStore(root).Acquire(CompositionRunPlanFactory.NewRunId(), ["cross-process-exclusive"])).OwnerRunId.ShouldBe(winner);
                Should.Throw<CompositionResourceClaimConflictException>(() => new CompositionResourceClaimStore(root).Acquire(CompositionRunPlanFactory.NewRunId(), ["after-interruption"])).OwnerRunId.ShouldBe(owners[1]);
            }
            finally
            {
                if (!holder.HasExited)
                {
                    holder.Kill(entireProcessTree: true);
                    await holder.WaitForExitAsync(TestContext.Current.CancellationToken).ConfigureAwait(true);
                }
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Exactly one concurrent process-equivalent store acquires an exclusive key.</summary>
    /// <returns>The atomic winner assertions.</returns>
    [Fact]
    public async Task ConcurrentAcquisitionsHaveExactlyOneWinnerAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string[] owners = [CompositionRunPlanFactory.NewRunId(), CompositionRunPlanFactory.NewRunId()];
            Task<bool>[] attempts = [.. owners.Select(owner => Task.Run(
                () =>
                {
                    try
                    {
                        new CompositionResourceClaimStore(root).Acquire(owner, ["actual-exclusive-resource"]);
                        return true;
                    }
                    catch (CompositionResourceClaimConflictException)
                    {
                        return false;
                    }
                },
                TestContext.Current.CancellationToken)),
            ];
            bool[] acquired = await Task.WhenAll(attempts).ConfigureAwait(true);
            acquired.Count(value => value).ShouldBe(1);
            string winner = owners[Array.IndexOf(acquired, true)];
            Should.Throw<CompositionResourceClaimConflictException>(() => new CompositionResourceClaimStore(root).Acquire(CompositionRunPlanFactory.NewRunId(), ["actual-exclusive-resource"])).OwnerRunId.ShouldBe(winner);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}