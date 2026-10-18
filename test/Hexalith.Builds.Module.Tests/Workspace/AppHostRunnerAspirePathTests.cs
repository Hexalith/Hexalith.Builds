// <copyright file="AppHostRunnerAspirePathTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Hexalith.Builds.ModuleHosts.AppHost;
using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Verifies the Builds-owned AppHost replaces the project directory and Aspire store baked into its assembly metadata
/// with the run-private directories named by the runner.
/// </summary>
[Collection("AppHost validation")]
public sealed class AppHostRunnerAspirePathTests
{
    /// <summary>Verifies the runner's directories become the AppHost directory and the Aspire store root.</summary>
    /// <returns>A task that completes after the assertion.</returns>
    [Fact]
    public async Task RunnerDirectoriesReplaceTheBakedAppHostDirectoryAndStoreAsync()
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string appHostDirectory = Path.Combine(root, "apphost");
            string store = Path.Combine(root, "aspire-store");
            _ = Directory.CreateDirectory(appHostDirectory);
            _ = Directory.CreateDirectory(store);
            Dictionary<string, string> environment = new(StringComparer.Ordinal)
            {
                [CompositionEnvironment.AppHostDirectory] = appHostDirectory,
                [CompositionEnvironment.AspireStorePath] = store,
            };

            IDistributedApplicationBuilder builder = AppHostRunner.CreateBuilder([], name => environment.GetValueOrDefault(name));

            builder.AppHostDirectory.ShouldBe(appHostDirectory);
            builder.Configuration["AppHost:Directory"].ShouldBe(appHostDirectory);
            builder.Configuration["Aspire:Store:Path"].ShouldBe(store);
            DistributedApplication app = builder.Build();
            await using (app.ConfigureAwait(true))
            {
                IAspireStore aspireStore = app.Services.GetRequiredService<IAspireStore>();
                aspireStore.BasePath.ShouldBe(Path.Combine(store, ".aspire"));
                Directory.Exists(aspireStore.BasePath).ShouldBeTrue();
            }
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }

    /// <summary>Verifies blank runner values leave Aspire's own resolution in place.</summary>
    [Fact]
    public void BlankRunnerDirectoriesKeepAspireResolution()
    {
        string unchanged = AppHostRunner.CreateBuilder([], _ => null).AppHostDirectory;

        IDistributedApplicationBuilder builder = AppHostRunner.CreateBuilder([], _ => " ");

        builder.AppHostDirectory.ShouldBe(unchanged);
        builder.Configuration["Aspire:Store:Path"].ShouldNotBe(" ");
    }
}
