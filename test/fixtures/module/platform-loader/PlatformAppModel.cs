// <copyright file="PlatformAppModel.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Platform.Composition;

using System.Text.Json;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Exercises the shared contract across the packaged validation child boundary only.</summary>
public static class PlatformAppModel
{
    /// <summary>Records the loaded contract and adds an unstarted gateway resource.</summary>
    /// <param name="builder">The validation builder.</param>
    /// <param name="plan">The plan deserialized by the packaged host.</param>
    /// <param name="signingKey">An unused synthetic key.</param>
    /// <param name="seed">An unused package seed.</param>
    public static void ComposeRun(IDistributedApplicationBuilder builder, CompositionRunPlan plan, string signingKey, string? seed)
    {
        _ = signingKey;
        _ = seed;
        _ = builder.Eventing.Subscribe<BeforeStartEvent>((_, _) =>
        {
            File.WriteAllText(Path.Combine(plan.Workspace, "startup-attempted"), "forbidden");
            throw new InvalidOperationException("The loader fixture must never start resources.");
        });
        Console.WriteLine("Loader fixture entered composition.");
        if (int.TryParse(Environment.GetEnvironmentVariable("HEXALITH_LOADER_FIXTURE_DELAY_MS"), out int delay))
        {
            Thread.Sleep(delay);
        }
        System.Reflection.Assembly tooling = typeof(CompositionRunPlan).Assembly;
        File.WriteAllText(Path.Combine(plan.Workspace, "loaded-contract.json"), JsonSerializer.Serialize(new
        {
            identity = tooling.FullName,
            path = tooling.Location,
        }));
        _ = builder.AddExecutable("eventstore", Path.Combine(plan.Workspace, "must-not-start"), plan.Workspace)
            .WithAnnotation(new PlatformServerAnnotation());
    }
}
