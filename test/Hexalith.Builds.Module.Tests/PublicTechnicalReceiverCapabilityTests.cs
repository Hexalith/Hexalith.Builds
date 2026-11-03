// <copyright file="PublicTechnicalReceiverCapabilityTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Text.Json;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>Exercises the actual compiler capability target against resolved package identities.</summary>
public sealed class PublicTechnicalReceiverCapabilityTests
{
    /// <summary>Only one resolved public Gateway 4.0.4 enables the technical receiver; aliases and near misses do not.</summary>
    /// <param name="version">The actual resolved package version.</param>
    /// <param name="package">The actual resolved package identity.</param>
    /// <param name="count">The number of matching resolved assemblies.</param>
    /// <param name="mapped">Whether the existing mapped-source path is selected.</param>
    /// <param name="expected">Whether the public capability should be enabled.</param>
    /// <returns>The native MSBuild compiler-property assertions.</returns>
    [Theory]
    [InlineData("4.0.4", "Hexalith.EventStore.Gateway", 1, false, true)]
    [InlineData("4.0.3", "Hexalith.EventStore.Gateway", 1, false, false)]
    [InlineData("4.0.5", "Hexalith.EventStore.Gateway", 1, false, false)]
    [InlineData("4.1.0", "Hexalith.EventStore.Gateway", 1, false, false)]
    [InlineData("4.0.4.0", "Hexalith.EventStore.Gateway", 1, false, false)]
    [InlineData("4.0.4+foreign", "Hexalith.EventStore.Gateway", 1, false, false)]
    [InlineData("4.0.4", "Hexalith.EventStore.Gateway.Alias", 1, false, false)]
    [InlineData("4.0.4", "Hexalith.EventStore.Gateway", 0, false, false)]
    [InlineData("4.0.4", "Hexalith.EventStore.Gateway", 2, false, false)]
    [InlineData("4.0.4", "Hexalith.EventStore.Gateway", 1, true, false)]
    public async Task ResolvedGatewayControlsPublicCapabilityAsync(string version, string package, int count, bool mapped, bool expected)
    {
        string root = CompositionTestFiles.CreateDirectory();
        try
        {
            string host = Path.Combine(PlatformManifestTestWorkspace.RepositoryRoot, "src", "hosts", "Hexalith.Builds.Module.EventStoreHost", "Hexalith.Builds.Module.EventStoreHost.csproj");
            XElement target = XDocument.Load(host).Root!.Elements("Target").Single(element => (string?)element.Attribute("Name") == "SelectPublicTechnicalHttpReceiver");
            XElement references = new("ItemGroup");
            for (int index = 0; index < count; index++)
            {
                references.Add(new XElement("ReferencePath", new XAttribute("Include", "resolved-" + index + ".dll"), new XElement("NuGetPackageId", package), new XElement("NuGetPackageVersion", version)));
            }

            string project = Path.Combine(root, "capability.proj");
            new XDocument(new XElement(
                "Project",
                new XElement("PropertyGroup", new XElement("HexalithEventStoreVersion", "4.0.4"), new XElement("BuildsPublicTechnicalHttpReceiver", "true"), new XElement("BuildsCatalogFromMappedSource", mapped ? "true" : "false"), new XElement("DefineConstants", "ORIGINAL")),
                references,
                new XElement("Target", new XAttribute("Name", "ResolveReferences")),
                new XElement(target))).Save(project);
            CompositionProcessResult result = await CompositionProcess.RunAsync(CompositionProcess.CreateStartInfo("dotnet", ["msbuild", project, "-target:SelectPublicTechnicalHttpReceiver", "-getProperty:BuildsPublicTechnicalHttpReceiver,DefineConstants", "-nologo"], root, null), TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken).ConfigureAwait(true);
            result.ExitCode.ShouldBe(0, result.Output + result.ErrorOutput);
            using JsonDocument output = JsonDocument.Parse(result.Output);
            JsonElement properties = output.RootElement.GetProperty("Properties");
            properties.GetProperty("BuildsPublicTechnicalHttpReceiver").GetString().ShouldBe(expected ? "true" : "false");
            string constants = properties.GetProperty("DefineConstants").GetString()!;
            constants.ShouldContain("ORIGINAL");
            constants.Contains("HEXALITH_EVENTSTORE_PUBLIC_TECHNICAL_RECEIVER", StringComparison.Ordinal).ShouldBe(expected);
        }
        finally
        {
            CompositionTestFiles.Delete(root);
        }
    }
}
