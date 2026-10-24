// <copyright file="LocalRunLifecycleFixture.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;

using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Creates actual durable owner state for lifecycle tests without provisioning external infrastructure.</summary>
internal static class LocalRunLifecycleFixture
{
    /// <summary>Creates one declared owner with stable issued identities.</summary>
    /// <param name="workspace">The isolated declaration workspace.</param>
    /// <param name="mode">The selected artifact mode.</param>
    /// <returns>The test selection and durable state.</returns>
    internal static (ModuleTestSelection Selection, CompositionRunState State) Create(PlatformManifestTestWorkspace workspace, string mode = "source")
    {
        workspace.Change("modules.0.lifecycle.tasks", "[]");
        workspace.Change("modules.0.tests", """
            [{"name":"verify","executor":"python3","entry":"runner.py","reportFormat":"trx","executionTimeoutSeconds":2,"stopTimeoutSeconds":4,"mode":"source"}]
            """);
        workspace.Change("modules.0.tests.0.mode", JsonSerializer.Serialize(mode));
        if (mode == "package")
        {
            workspace.Change("modules.0.tests.0.executor", "\"vstest\"");
            workspace.Change("modules.0.tests.0.entry", "\"runner.csproj\"");
            File.WriteAllText(Path.Combine(workspace.Root, "runner.csproj"), "<Project />");
        }

        File.WriteAllText(Path.Combine(workspace.Root, "runner.py"), "print('fixture')");
        string manifest = workspace.Save();
        ModuleTestSelection selected = ModuleTestSelection.Select(PlatformManifestValidator.Validate([manifest], TestContext.Current.CancellationToken).Declarations!, "verify", manifest, DateTimeOffset.UtcNow).ShouldNotBeNull();
        CompositionRunState state = CompositionRunPlanFactory.CreateInitialState(CompositionTestFiles.RunId, Path.Combine(workspace.Root, "runs", CompositionTestFiles.RunId), selected.ManifestHash, selected.RequestedAtUtc) with
        {
            SelectedTest = selected,
            RetainOnFailure = true,
            ExecutionContext = JsonSerializer.SerializeToElement(new { kind = "local" }),
            EffectiveDeadlineSeconds = 600,
            StartupDeadlineUtc = selected.RequestedAtUtc.AddSeconds(600),
        };
        return (selected, state);
    }

    /// <summary>Builds bounded engine settings with a local Docker process double.</summary>
    /// <param name="root">The isolated workspace.</param>
    /// <param name="selected">The declared selection.</param>
    /// <returns>The lifecycle options.</returns>
    internal static CompositionEngineOptions Options(string root, ModuleTestSelection selected) => new("unused-apphost", "unused-child")
    {
        SelectedTest = selected,
        WorkspaceRoot = Path.Combine(root, "runs"),
        StateDirectory = Path.Combine(root, "state"),
        DockerCommand = CompositionTestFiles.CreateStubDocker(root),
        RetainOnFailure = true,
        StopTimeout = TimeSpan.FromMilliseconds(100),
    };
}
