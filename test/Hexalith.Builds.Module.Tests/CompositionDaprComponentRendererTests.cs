// <copyright file="CompositionDaprComponentRendererTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the runner renders every Dapr component and configuration for the run.
/// </summary>
public sealed class CompositionDaprComponentRendererTests
{
    /// <summary>
    /// Verifies components bind to the run-scoped Redis port and are scoped to EventStore only.
    /// </summary>
    [Fact]
    public void ComponentsBindToRunScopedRedisAndEventStoreScope()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);

            string stateStore = CompositionDaprComponentRenderer.RenderStateStore(plan);
            string pubSub = CompositionDaprComponentRenderer.RenderPubSub(plan);

            stateStore.ShouldContain("type: state.redis");
            stateStore.ShouldContain("value: \"localhost:20001\"");
            stateStore.ShouldContain("name: actorStateStore\n      value: \"true\"");
            stateStore.ShouldContain("scopes:\n  - eventstore\n");
            pubSub.ShouldContain("type: pubsub.redis");
            pubSub.ShouldContain("value: \"localhost:20001\"");
            pubSub.ShouldContain("scopes:\n  - eventstore\n");
            stateStore.ShouldNotContain("6379");
            pubSub.ShouldNotContain("6379");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies the configuration gives each run its own name-resolution registry.
    /// </summary>
    [Fact]
    public void ConfigurationUsesPerRunNameResolution()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);

            string configuration = CompositionDaprComponentRenderer.RenderConfiguration(plan);

            configuration.ShouldContain("kind: Configuration");
            configuration.ShouldContain("name: hexalith-g4-" + CompositionTestFiles.RunId);
            configuration.ShouldContain("component: \"sqlite\"");
            configuration.ShouldContain(plan.Workspace.Replace('\\', '/') + "/dapr/name-resolution.db");
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>
    /// Verifies the renderer writes every document and an empty isolated resources directory in the workspace.
    /// </summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task WriteAsyncCreatesDocumentsInsideWorkspaceAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan plan = CompositionTestFiles.CreatePlan(root);

            await CompositionDaprComponentRenderer.WriteAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true);

            File.Exists(plan.StateStoreComponentPath).ShouldBeTrue();
            File.Exists(plan.PubSubComponentPath).ShouldBeTrue();
            File.Exists(plan.DaprConfigPath).ShouldBeTrue();
            Directory.Exists(plan.IsolatedResourcesPath).ShouldBeTrue();
            Directory.EnumerateFileSystemEntries(plan.IsolatedResourcesPath).ShouldBeEmpty();
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}