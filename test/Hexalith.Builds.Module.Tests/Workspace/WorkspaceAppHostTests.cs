// <copyright file="WorkspaceAppHostTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.Workspace;

using Shouldly;

using Xunit;

/// <summary>Verifies composed AppHost module projects receive the runner's mapping settings.</summary>
public sealed class WorkspaceAppHostTests
{
    /// <summary>Verifies every composed module project receives the same imports, hash and configuration.</summary>
    /// <param name="mode">The tool-selected mode.</param>
    /// <param name="configuration">The expected configuration.</param>
    /// <returns>A task for the assertion.</returns>
    [Theory]
    [InlineData(WorkspaceMode.Source, "Debug")]
    [InlineData(WorkspaceMode.Package, "Release")]
    public async Task ComposedModulesReceiveMappingSettingsAsync(WorkspaceMode mode, string configuration)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            CompositionRunPlan sample = CompositionTestFiles.CreatePlan(root);
            foreach (CompositionRunModule module in sample.Modules)
            {
                _ = Directory.CreateDirectory(Path.GetDirectoryName(module.ProjectPath)!);
                await File.WriteAllTextAsync(module.ProjectPath, "<Project Sdk=\"Microsoft.NET.Sdk\" />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            }

            SourceMapping mapping = new(mode, root, "Hexalith.Active", [new SourceMappingEntry("Hexalith.Active", "source", root)], true);
            CompositionRunPlan plan = CompositionRunPlanFactory.Create(sample.RunId, sample.Workspace, sample.DaprHome, sample.Ports, sample.Modules, sample.UiMarkers, mapping);
            await SourceMappingMaterializer.WriteAsync(mapping, plan.Workspace, TestContext.Current.CancellationToken).ConfigureAwait(true);
            await CompositionDaprComponentRenderer.WriteAsync(plan, TestContext.Current.CancellationToken).ConfigureAwait(true);
            IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
            {
                Args = [],
                DisableDashboard = true,
                AllowUnsecuredTransport = true,
            });
            _ = builder.AddDapr(options => options.DaprPath = Path.Combine(root, "dapr-home", "tools", "dapr"));

            RunTopology.Compose(builder, plan, CompositionSigningKey.Create());

            foreach (CompositionRunModule module in plan.Modules)
            {
                ProjectResource project = builder.Resources.OfType<ProjectResource>().Single(resource => resource.Name == module.ModuleId);
                project.TryGetEnvironmentVariables(out IEnumerable<EnvironmentCallbackAnnotation>? annotations).ShouldBeTrue();
                Dictionary<string, object> environment = new(StringComparer.Ordinal);
                EnvironmentCallbackContext context = new(builder.ExecutionContext, project, environment, TestContext.Current.CancellationToken);
                foreach (EnvironmentCallbackAnnotation annotation in annotations.TakeLast(SourceMappingMaterializer.Environment(mapping, plan.Workspace).Count))
                {
                    await annotation.Callback(context).ConfigureAwait(true);
                }

                environment["HEXALITH_SOURCE_MAPPING_HASH"].ShouldBe(mapping.ContentHash);
                environment["CustomBeforeDirectoryBuildProps"].ShouldBe(SourceMappingMaterializer.TargetsPath(plan.Workspace));
                environment["CustomAfterMicrosoftCommonProps"].ShouldBe(SourceMappingMaterializer.PropsPath(plan.Workspace));
                environment["CustomAfterDirectoryBuildTargets"].ShouldBe(SourceMappingMaterializer.LatePropsPath(plan.Workspace));
                environment["AfterMicrosoftNETSdkTargets"].ShouldBe(SourceMappingMaterializer.FinalTargetsPath(plan.Workspace));
                environment["Configuration"].ShouldBe(configuration);
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
