// <copyright file="QualificationOwnerPlanLoaderTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Reflection;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Exercises the actual AppHost loader and its private parent launch environment.</summary>
[Collection(nameof(PlatformManifestTestGrouping))]
public sealed class QualificationOwnerPlanLoaderTests
{
    /// <summary>Serialized expansion must pass the actual loader before the key and required marker are withdrawn.</summary>
    /// <param name="fault">The serialized request or parent-launch alteration.</param>
    /// <returns>The actual loader, refusal and environment-withdrawal assertions.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture fault is unknown.</exception>
    [Theory]
    [InlineData("none")]
    [InlineData("drop-owners")]
    [InlineData("drop-binding")]
    [InlineData("drop-both")]
    [InlineData("change-owners")]
    [InlineData("foreign-key")]
    [InlineData("no-marker-with-binding")]
    public async Task ActualLoaderRequiresBoundExpandedRequestAsync(string fault)
    {
        using PlatformManifestTestWorkspace workspace = new();
        string key = CompositionSigningKey.Create();
        CompositionRunPlan plan = CreateFaultedPlan(workspace.Root, key, fault);
        string path = Path.Combine(workspace.Root, "parent-plan.json");
        await CompositionDocumentStore.WriteAsync(path, plan, TestContext.Current.CancellationToken).ConfigureAwait(true);
        string suppliedKey = fault == "foreign-key" ? CompositionSigningKey.Create() : key;
        Dictionary<string, string?> values = new(StringComparer.Ordinal)
        {
            [CompositionEnvironment.PlanPath] = path,
            [CompositionEnvironment.RunId] = plan.RunId,
            [CompositionEnvironment.SigningKey] = suppliedKey,
            [CompositionEnvironment.QualificationOwnersRequired] = fault == "no-marker-with-binding" ? null : "1",
        };
        Dictionary<string, string?> original = values.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach ((string name, string? value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }

            Type loader = Assembly.Load("Hexalith.Builds.Module.AppHost").GetType("Hexalith.Builds.ModuleHosts.AppHost.RunPlanSource", throwOnError: true)!;
            if (fault is "none" or "no-marker-with-binding")
            {
                CompositionRunPlan received = await LoadPlanAsync(loader).ConfigureAwait(true);
                received.QualificationOwners.ShouldBe(["tenants"]);
            }
            else
            {
                _ = await Should.ThrowAsync<InvalidDataException>(async () => await LoadPlanAsync(loader).ConfigureAwait(true)).ConfigureAwait(true);
            }

            MethodInfo take = loader.GetMethod("TakeSigningKey", BindingFlags.Public | BindingFlags.Static)!;
            string withdrawn = (string)take.Invoke(null, null)!;
            string.Equals(withdrawn, suppliedKey, StringComparison.Ordinal).ShouldBeTrue("The actual loader must return its private parent key without retaining it.");
            Environment.GetEnvironmentVariable(CompositionEnvironment.SigningKey).ShouldBeNull();
            Environment.GetEnvironmentVariable(CompositionEnvironment.QualificationOwnersRequired).ShouldBeNull();
            Directory.Exists(plan.Workspace).ShouldBeFalse();
        }
        finally
        {
            foreach ((string name, string? value) in original)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    /// <summary>Invokes the actual private AppHost loader through the existing assembly reference.</summary>
    /// <param name="loader">The actual AppHost loader type.</param>
    /// <returns>The loader's work started for this invocation.</returns>
    private static Task<CompositionRunPlan> LoadPlanAsync(Type loader) =>
        (Task<CompositionRunPlan>)loader.GetMethod("LoadAsync", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [TestContext.Current.CancellationToken])!;

    /// <summary>Creates the authenticated parent metadata before applying one explicit transport fault.</summary>
    /// <param name="root">The isolated workspace.</param>
    /// <param name="key">The private parent key; never retained in metadata or diagnostics.</param>
    /// <param name="fault">The finite transport fault.</param>
    /// <returns>The serialized request fixture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The fixture fault is unknown.</exception>
    private static CompositionRunPlan CreateFaultedPlan(string root, string key, string fault)
    {
        CompositionRunPlan plan = CompositionQualificationOwners.Bind(QualificationOwnerBindingTests.CreatePlan(root), key);
        return fault switch
        {
            "none" or "foreign-key" or "no-marker-with-binding" => plan,
            "drop-owners" => plan with { QualificationOwners = null },
            "drop-binding" => plan with { QualificationOwnersBinding = null },
            "drop-both" => plan with { QualificationOwners = null, QualificationOwnersBinding = null },
            "change-owners" => plan with { QualificationOwners = ["memories"] },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };
    }
}
