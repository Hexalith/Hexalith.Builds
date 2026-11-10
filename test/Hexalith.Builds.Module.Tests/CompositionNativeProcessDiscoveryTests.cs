// <copyright file="CompositionNativeProcessDiscoveryTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Qualifies real process ownership on supported hosts, including the Windows/macOS environment reader.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CompositionNativeProcessDiscoveryTests
{
    /// <summary>Both public and private ownership tags preserve independent work during exact removal.</summary>
    /// <param name="privateTag">Whether this is an isolated native execution rather than an owner-wide scan.</param>
    /// <returns>The actual child, owner-service and independent peer observations.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTaggedChildRemovalPreservesIndependentPeerAsync(bool privateTag)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
        {
            Assert.Skip("Native process inspection requires a supported host.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            (_, string docker, string daprHome, _) = await CompositionTestFiles.BuildPortableToolchainAsync(root, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            string owner = CompositionRunPlanFactory.NewRunId();
            string identity = privateTag ? CompositionRunPlanFactory.NewRunId() : owner;
            string peer = CompositionRunPlanFactory.NewRunId();
            Dictionary<string, string> childEnvironment = new() { [CompositionEnvironment.RunId] = owner };
            Dictionary<string, string> peerEnvironment = new() { [CompositionEnvironment.RunId] = peer };
            if (privateTag)
            {
                childEnvironment[NativeTestHandoff._nativeExecutionId] = identity;
                peerEnvironment[NativeTestHandoff._nativeExecutionId] = peer;
            }

            using Process retained = new() { StartInfo = CompositionProcess.CreateStartInfo(Path.Combine(daprHome, ".dapr", "bin", "scheduler" + extension), [], root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = privateTag ? owner : CompositionRunPlanFactory.NewRunId() }) };
            retained.Start().ShouldBeTrue();
            try
            {
                CompositionRunResourceScanner scanner = privateTag ? CompositionRunResourceScanner.CreateNativeScanner() : new(docker);
                using Process child = new() { StartInfo = CompositionProcess.CreateStartInfo(Path.Combine(daprHome, ".dapr", "bin", "placement" + extension), [], root, childEnvironment) };
                using Process independent = new() { StartInfo = CompositionProcess.CreateStartInfo(Path.Combine(daprHome, ".dapr", "bin", "scheduler" + extension), [], root, peerEnvironment) };
                child.Start().ShouldBeTrue();
                independent.Start().ShouldBeTrue();
                try
                {
                    using CancellationTokenSource bound = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
                    bound.CancelAfter(TimeSpan.FromSeconds(10));
                    IReadOnlyList<int> before = scanner.FindTaggedProcesses(identity, bound.Token).ProcessIds;
                    before.ShouldContain(child.Id);
                    before.ShouldNotContain(independent.Id);
                    before.ShouldNotContain(retained.Id);
                    (IReadOnlyList<int> remaining, _) = await scanner.RemoveTaggedProcessesAsync(identity, bound.Token).ConfigureAwait(true);
                    child.Refresh();
                    child.HasExited.ShouldBeTrue();
                    remaining.ShouldNotContain(child.Id);
                    scanner.FindTaggedProcesses(identity, bound.Token).ProcessIds.ShouldNotContain(child.Id);
                    independent.Refresh();
                    independent.HasExited.ShouldBeFalse();
                    scanner.FindTaggedProcesses(peer, bound.Token).ProcessIds.ShouldContain(independent.Id);
                    retained.Refresh();
                    retained.HasExited.ShouldBeFalse("Private native removal must preserve retained owner services.");
                }
                finally
                {
                    CompositionProcess.Kill(child);
                    CompositionProcess.Kill(independent);
                }
            }
            finally
            {
                CompositionProcess.Kill(retained);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
