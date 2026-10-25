// <copyright file="CompositionNativeProcessDiscoveryTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Qualifies real native ownership observations on supported Windows and macOS hosts.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class CompositionNativeProcessDiscoveryTests
{
    /// <summary>Native discovery finds and removes a real tagged child while an independently tagged peer survives.</summary>
    /// <returns>The actual child, removal and peer observations without a synthetic discovery override.</returns>
    [Fact]
    public async Task NativeTaggedChildRemovalPreservesIndependentPeerAsync()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            Assert.Skip("Native process inspection is explicitly gated to Windows and macOS.");
        }

        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            (_, string docker, string daprHome, _) = await CompositionTestFiles.BuildPortableToolchainAsync(root, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
            string owner = CompositionRunPlanFactory.NewRunId();
            string peer = CompositionRunPlanFactory.NewRunId();
            using Process child = new() { StartInfo = CompositionProcess.CreateStartInfo(Path.Combine(daprHome, ".dapr", "bin", "placement" + extension), [], root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = owner }) };
            using Process independent = new() { StartInfo = CompositionProcess.CreateStartInfo(Path.Combine(daprHome, ".dapr", "bin", "scheduler" + extension), [], root, new Dictionary<string, string> { [CompositionEnvironment.RunId] = peer }) };
            child.Start().ShouldBeTrue();
            independent.Start().ShouldBeTrue();
            try
            {
                CompositionRunResourceScanner scanner = new(docker);
                CompositionRunResources before = await scanner.FindAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true);
                before.ProcessIds.ShouldContain(child.Id);
                before.ProcessIds.ShouldNotContain(independent.Id);
                using CancellationTokenSource bound = new(TimeSpan.FromSeconds(10));
                CompositionRunResources after = await scanner.RemoveAsync(owner, bound.Token).ConfigureAwait(true);
                child.Refresh();
                child.HasExited.ShouldBeTrue();
                (await scanner.FindAsync(owner, TestContext.Current.CancellationToken).ConfigureAwait(true)).ProcessIds.ShouldNotContain(child.Id);
                independent.Refresh();
                independent.HasExited.ShouldBeFalse();
                (await scanner.FindAsync(peer, TestContext.Current.CancellationToken).ConfigureAwait(true)).ProcessIds.ShouldContain(independent.Id);
                if (after.Unverified)
                {
                    after.IsEmpty.ShouldBeFalse();
                }
                else
                {
                    after.ProcessIds.ShouldNotContain(child.Id);
                }
            }
            finally
            {
                CompositionProcess.Kill(child);
                CompositionProcess.Kill(independent);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
