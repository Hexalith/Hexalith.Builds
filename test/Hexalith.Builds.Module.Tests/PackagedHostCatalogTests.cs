// <copyright file="PackagedHostCatalogTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Checks the actual packaged host projects select their admitted package and audit context.</summary>
public sealed class PackagedHostCatalogTests
{
    /// <summary>Evaluates each real producer role without inherited dependency selections.</summary>
    /// <param name="role">The exact packaged host project identity.</param>
    /// <returns>The independently evaluated dependency and audit assertions.</returns>
    [Theory]
    [InlineData("Hexalith.Builds.Module.AppHost")]
    [InlineData("Hexalith.Builds.Module.EventStoreHost")]
    [InlineData("Hexalith.Builds.Module.UiHost")]
    public async Task ActualHostUsesTheRetainedSdkAndOpenApiPairAsync(string role)
    {
        string root = CompositionTestFiles.RepositoryRoot();
        string project = Path.Combine(root, "src", "hosts", role, role + ".csproj");
        string temporary = CompositionTestFiles.CreateDirectory();
        try
        {
            string observation = Path.Combine(temporary, "observe.targets");
            XElement pins = new(
                "_HostPins",
                new XAttribute("Include", "@(PackageVersion)"),
                new XAttribute("Condition", "$([System.String]::Copy('%(PackageVersion.Identity)').StartsWith('Hexalith.EventStore.')) or '%(PackageVersion.Identity)' == 'Microsoft.AspNetCore.OpenApi' or '%(PackageVersion.Identity)' == 'Microsoft.OpenApi'"));
            new XDocument(new XElement(
                "Project",
                new XElement(
                    "Target",
                    new XAttribute("Name", "ObserveHostCohort"),
                    new XAttribute("Returns", "@(_HostPins)"),
                    new XElement("ItemGroup", pins)))).Save(observation);
            ProcessStartInfo start = CompositionProcess.CreateStartInfo(
                "dotnet",
                [
                    "msbuild", project, "-nologo", "-noAutoResponse", "-nodeReuse:false", "-p:Configuration=Release", "-p:UseHexalithProjectReferences=false",
                    "-p:CustomAfterMicrosoftCommonTargets=" + observation, "-target:ObserveHostCohort",
                    "-getProperty:HexalithEventStoreVersion,HexalithBuildsPackagedHostSdk42CatalogView,NuGetAudit,RestoreEnablePackagePruning,CentralPackageTransitivePinningEnabled", "-getTargetResult:ObserveHostCohort",
                ],
                root,
                null);
            foreach (string name in start.Environment.Keys.Where(name => name.StartsWith("Hexalith", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                _ = start.Environment.Remove(name);
            }

            CompositionProcessResult result = await CompositionProcess.RunAsync(start, TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
            using JsonDocument document = JsonDocument.Parse(result.Output);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            properties.GetProperty("HexalithEventStoreVersion").GetString().ShouldBe("4.2.0");
            properties.GetProperty("HexalithBuildsPackagedHostSdk42CatalogView").GetString().ShouldBe("true");
            properties.GetProperty("NuGetAudit").GetString().ShouldBe("true");
            properties.GetProperty("RestoreEnablePackagePruning").GetString().ShouldBe("false");
            properties.GetProperty("CentralPackageTransitivePinningEnabled").GetString().ShouldBe("true");
            Dictionary<string, string> packages = document.RootElement.GetProperty("TargetResults").GetProperty("ObserveHostCohort").GetProperty("Items").EnumerateArray()
                .ToDictionary(item => item.GetProperty("Identity").GetString()!, item => item.GetProperty("Version").GetString()!, StringComparer.Ordinal);
            KeyValuePair<string, string>[] sdk = [.. packages.Where(item => item.Key.StartsWith("Hexalith.EventStore.", StringComparison.Ordinal))];
            sdk.Length.ShouldBe(13);
            sdk.ShouldAllBe(item => item.Value == "4.2.0");
            packages["Microsoft.AspNetCore.OpenApi"].ShouldBe("10.0.15");
            packages["Microsoft.OpenApi"].ShouldBe("2.13.0");
        }
        finally
        {
            CompositionTestFiles.Delete(temporary);
        }
    }
}
