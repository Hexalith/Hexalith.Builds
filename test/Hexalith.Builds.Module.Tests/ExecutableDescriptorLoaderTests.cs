// <copyright file="ExecutableDescriptorLoaderTests.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

using System.Diagnostics;
using System.Text.Json;

using Hexalith.Builds.ModuleTool.Cli;
using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.Runtime;

using Shouldly;

using Xunit;

/// <summary>
/// Checks executable descriptor validation and the credential-free child boundary.
/// </summary>
public sealed class ExecutableDescriptorLoaderTests
{
    private static readonly string[] _pureDomainClasses = ["pure-domain"];

    /// <summary>
    /// Proves that a built descriptor executes in a child without inherited runner environment.
    /// </summary>
    /// <returns>A task that completes after the child boundary is checked.</returns>
    [Fact]
    public async Task LoadAsyncExecutesBuiltDescriptorInCredentialFreeChild()
    {
        string root = CreateRoot();
        const string variable = "HEXALITH_DESCRIPTOR_TEST_SECRET_982f";
        string? original = Environment.GetEnvironmentVariable(variable);
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "domain"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "domain", "Domain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            const string json = """
                {"schema":"hexalith.module-descriptor.v1","moduleId":"module-a","domainServiceProject":"domain/Domain.csproj"}
                """;
            string source = "namespace Hexalith; public static class ModuleDescriptorV1 { public static string Describe() => "
                + "System.Environment.GetEnvironmentVariable(\"" + variable + "\") is null ? "
                + JsonSerializer.Serialize(json) + " : \"{}\"; }";
            string assembly = await BuildDescriptorAsync(root, "descriptor", source, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string relativeAssembly = Path.GetRelativePath(root, assembly).Replace('\\', '/');
            ModuleManifest manifest = CreateManifest(relativeAssembly);
            Environment.SetEnvironmentVariable(variable, "credential-must-not-enter-child");

            ExecutableDescriptorLoadResult result = await ExecutableDescriptorLoader.LoadAsync(
                manifest,
                manifestPath,
                typeof(ModuleCommandApplication).Assembly.Location,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsValid.ShouldBeTrue(string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.RuleId)));
            result.Modules.Single().ModuleId.ShouldBe("module-a");
            result.Modules.Single().DomainServiceProject.ShouldBe(Path.Combine(root, "domain", "Domain.csproj"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, original);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Proves two built descriptors and exact FrontComposer marker bindings load through child processes.
    /// </summary>
    /// <returns>A task that completes after every descriptor is validated.</returns>
    [Fact]
    public async Task LoadAsyncAcceptsTwoModulesAndExactUiMarkers()
    {
        string root = CreateRoot();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "domain"));
            _ = Directory.CreateDirectory(Path.Combine(root, "ui"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "domain", "DomainA.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(root, "domain", "DomainB.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            await File.WriteAllTextAsync(
                Path.Combine(root, "ui", "Ui.csproj"), "<Project />", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string markerAAssembly = await BuildDescriptorAsync(
                root,
                "marker-a",
                "namespace Acme; public sealed class MarkerA {}",
                TestContext.Current.CancellationToken,
                "MarkerA.cs").ConfigureAwait(true);
            string markerBAssembly = await BuildDescriptorAsync(
                root,
                "marker-b",
                "namespace Acme; public sealed class MarkerB {}",
                TestContext.Current.CancellationToken,
                "MarkerB.cs").ConfigureAwait(true);
            string markerAPath = Path.GetRelativePath(root, markerAAssembly).Replace('\\', '/');
            string markerBPath = Path.GetRelativePath(root, markerBAssembly).Replace('\\', '/');
            string moduleAJson = JsonSerializer.Serialize(new
            {
                schema = "hexalith.module-descriptor.v1",
                moduleId = "module-a",
                domainServiceProject = "domain/DomainA.csproj",
                uiAssembly = markerAPath,
                uiMarkerType = "Acme.MarkerA",
            });
            string moduleBJson = JsonSerializer.Serialize(new
            {
                schema = "hexalith.module-descriptor.v1",
                moduleId = "module-b",
                domainServiceProject = "domain/DomainB.csproj",
                uiAssembly = markerBPath,
                uiMarkerType = "Acme.MarkerB",
            });
            string uiJson = JsonSerializer.Serialize(new
            {
                schema = "hexalith.ui-descriptor.v1",
                uiProject = "ui/Ui.csproj",
                moduleUiMarkers = new[]
                {
                    new { moduleId = "module-a", uiAssembly = markerAPath, uiMarkerType = "Acme.MarkerA" },
                    new { moduleId = "module-b", uiAssembly = markerBPath, uiMarkerType = "Acme.MarkerB" },
                },
            });
            string moduleA = await BuildDescriptorAsync(
                root, "module-a", DescribeSource("ModuleDescriptorV1", moduleAJson), TestContext.Current.CancellationToken).ConfigureAwait(true);
            string moduleB = await BuildDescriptorAsync(
                root, "module-b", DescribeSource("ModuleDescriptorV1", moduleBJson), TestContext.Current.CancellationToken).ConfigureAwait(true);
            string ui = await BuildDescriptorAsync(
                root, "ui-descriptor", DescribeSource("UiDescriptorV1", uiJson), TestContext.Current.CancellationToken, "UiDescriptorV1.cs").ConfigureAwait(true);
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);
            ModuleManifest manifest = CreateManifest(Path.GetRelativePath(root, moduleA).Replace('\\', '/')) with
            {
                Modules =
                [
                    new ModuleDescriptor("module-a", Path.GetRelativePath(root, moduleA).Replace('\\', '/'), [], "module-a", "module-a", "module-a"),
                    new ModuleDescriptor("module-b", Path.GetRelativePath(root, moduleB).Replace('\\', '/'), ["module-a"], "module-b", "module-b", "module-b"),
                ],
                Ui = new UiDescriptor(Path.GetRelativePath(root, ui).Replace('\\', '/')),
            };

            ExecutableDescriptorLoadResult result = await ExecutableDescriptorLoader.LoadAsync(
                manifest,
                manifestPath,
                typeof(ModuleCommandApplication).Assembly.Location,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsValid.ShouldBeTrue(string.Join(" | ", result.Diagnostics.Select(diagnostic => diagnostic.RuleId)));
            result.Modules.Select(module => module.ModuleId).ShouldBe(["module-a", "module-b"]);
            result.Ui.ShouldNotBeNull().ModuleUiMarkers.Select(marker => marker.UiMarkerType).ShouldBe(["Acme.MarkerA", "Acme.MarkerB"]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Routes an approved executable descriptor to the live prerequisite probe and fails closed when Dapr is absent.
    /// </summary>
    /// <returns>A task that completes after the public command result is checked.</returns>
    [Fact]
    public async Task PublicRunWithApprovedDescriptorChecksLivePrerequisites()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("The fake Docker CLI uses a POSIX shell script.");
        }

        string root = CreateRoot();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "domain"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "domain", "Domain.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            const string descriptorJson = """
                {"schema":"hexalith.module-descriptor.v1","moduleId":"module-a","domainServiceProject":"domain/Domain.csproj"}
                """;
            string assembly = await BuildDescriptorAsync(
                root,
                "module-a",
                DescribeSource("ModuleDescriptorV1", descriptorJson),
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            _ = Directory.CreateDirectory(Path.Combine(root, "fixtures"));
            await File.WriteAllTextAsync(
                Path.Combine(root, "fixtures", "full.json"),
                "{}",
                TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifestPath = Path.Combine(root, "manifest.json");
            string relativeAssembly = Path.GetRelativePath(root, assembly).Replace('\\', '/');
            string manifestJson = JsonSerializer.Serialize(new
            {
                schema = "hexalith.module-manifest.v1",
                id = "descriptor-command-fixture",
                modules = new[]
                {
                    new
                    {
                        id = "module-a",
                        descriptorAssembly = relativeAssembly,
                        dependencies = Array.Empty<string>(),
                        domain = "module-a",
                        applicationId = "module-a",
                        resourceId = "module-a",
                    },
                },
                platform = new
                {
                    eventStoreVersion = "3.106.0",
                    daprRuntimeVersion = "1.18.2",
                    daprSdkVersion = "1.18.8",
                    frontComposerVersion = "4.5.0",
                },
                profiles = new
                {
                    full = new { fixture = "fixtures/full.json", classes = _pureDomainClasses },
                },
            });
            await File.WriteAllTextAsync(manifestPath, manifestJson, TestContext.Current.CancellationToken).ConfigureAwait(true);
            CompositionEngineOptions options = new(Path.Combine(root, "missing-apphost.dll"), typeof(ModuleCommandApplication).Assembly.Location)
            {
                DockerCommand = CompositionTestFiles.CreateStubDocker(root),
                DaprHome = Path.Combine(root, "missing-dapr"),
                StateDirectory = Path.Combine(root, "state"),
                WorkspaceRoot = Path.Combine(root, "workspaces"),
            };
            StringWriter output = new();
            await using (output.ConfigureAwait(true))
            {
                int exitCode = await ModuleCommandApplication.InvokeAsync(
                    ["run", "--manifest", manifestPath, "--output", "json"],
                    output,
                    TextWriter.Null,
                    TestContext.Current.CancellationToken,
                    typeof(ModuleCommandApplication).Assembly.Location,
                    options).ConfigureAwait(true);

                exitCode.ShouldBe((int)ToolExitCode.PrerequisiteUnavailable, output.ToString());
                output.ToString().ShouldContain("HXR011");
                output.ToString().ShouldNotContain("HXR003");
                output.ToString().ShouldNotContain("HXD");
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rejects a non-assembly file before the runtime can start.
    /// </summary>
    /// <returns>A task that completes after the malformed child result is checked.</returns>
    [Fact]
    public async Task LoadAsyncRejectsInvalidAssembly()
    {
        string root = CreateRoot();
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "invalid.dll"), "not a PE file", TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);

            ExecutableDescriptorLoadResult result = await ExecutableDescriptorLoader.LoadAsync(
                CreateManifest("invalid.dll"),
                manifestPath,
                typeof(ModuleCommandApplication).Assembly.Location,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXD002");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Terminates a descriptor that does not return within ten seconds.
    /// </summary>
    /// <returns>A task that completes after the timeout control.</returns>
    [Fact]
    public async Task LoadAsyncRejectsTimedOutChild()
    {
        string root = CreateRoot();
        try
        {
            const string source = "namespace Hexalith; public static class ModuleDescriptorV1 { public static string Describe() { System.Threading.Thread.Sleep(20000); return \"{}\"; } }";
            string assembly = await BuildDescriptorAsync(root, "slow", source, TestContext.Current.CancellationToken).ConfigureAwait(true);
            string manifestPath = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifestPath, "{}", TestContext.Current.CancellationToken).ConfigureAwait(true);

            ExecutableDescriptorLoadResult result = await ExecutableDescriptorLoader.LoadAsync(
                CreateManifest(Path.GetRelativePath(root, assembly).Replace('\\', '/')),
                manifestPath,
                typeof(ModuleCommandApplication).Assembly.Location,
                TestContext.Current.CancellationToken).ConfigureAwait(true);

            result.IsValid.ShouldBeFalse();
            result.Diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXD001");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rejects duplicate keys, unknown fields, schema drift, identity changes, and path escapes.
    /// </summary>
    /// <param name="json">The returned descriptor document.</param>
    /// <param name="ruleId">The required diagnostic rule.</param>
    [Theory]
    [InlineData("{\"schema\":\"hexalith.module-descriptor.v1\",\"schema\":\"hexalith.module-descriptor.v1\",\"moduleId\":\"module-a\",\"domainServiceProject\":\"domain/Domain.csproj\"}", "HXD002")]
    [InlineData("{\"schema\":\"hexalith.module-descriptor.v2\",\"moduleId\":\"module-a\",\"domainServiceProject\":\"domain/Domain.csproj\"}", "HXD002")]
    [InlineData("{\"schema\":\"hexalith.module-descriptor.v1\",\"moduleId\":\"module-b\",\"domainServiceProject\":\"domain/Domain.csproj\"}", "HXD004")]
    [InlineData("{\"schema\":\"hexalith.module-descriptor.v1\",\"moduleId\":\"module-a\",\"domainServiceProject\":\"../Domain.csproj\"}", "HXM004")]
    [InlineData("{\"schema\":\"hexalith.module-descriptor.v1\",\"moduleId\":\"module-a\",\"domainServiceProject\":\"domain/Domain.csproj\",\"port\":5050}", "HXD002")]
    public void ReadModuleRejectsUnapprovedDescriptorShape(string json, string ruleId)
    {
        string root = CreateRoot();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "domain"));
            File.WriteAllText(Path.Combine(root, "domain", "Domain.csproj"), "<Project />");
            List<ToolDiagnostic> diagnostics = [];

            ExecutableModuleDescriptor? module = DescriptorDocumentValidator.ReadModule(
                json,
                root,
                "module-a",
                "modules[0].descriptorAssembly",
                diagnostics);

            module.ShouldBeNull();
            diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain(ruleId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Rejects a UI marker that differs from the module descriptor binding.
    /// </summary>
    [Fact]
    public void ReadUiRejectsMarkerMismatch()
    {
        string root = CreateRoot();
        try
        {
            _ = Directory.CreateDirectory(Path.Combine(root, "ui"));
            File.WriteAllText(Path.Combine(root, "ui", "Ui.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(root, "ui", "Marker.dll"), string.Empty);
            List<ToolDiagnostic> diagnostics = [];
            const string json = """
                {"schema":"hexalith.ui-descriptor.v1","uiProject":"ui/Ui.csproj","moduleUiMarkers":[{"moduleId":"module-a","uiAssembly":"ui/Marker.dll","uiMarkerType":"Acme.OtherMarker"}]}
                """;
            ExecutableModuleDescriptor module = new(
                "module-a", Path.Combine(root, "domain", "Domain.csproj"), null, Path.Combine(root, "ui", "Marker.dll"), "Acme.MarkerA");

            ExecutableUiDescriptor? ui = DescriptorDocumentValidator.ReadUi(
                json, root, [module], "ui.descriptorAssembly", diagnostics);

            ui.ShouldBeNull();
            diagnostics.Select(diagnostic => diagnostic.RuleId).ShouldContain("HXD005");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> BuildDescriptorAsync(
        string root,
        string name,
        string source,
        CancellationToken cancellationToken,
        string sourceFileName = "ModuleDescriptorV1.cs")
    {
        string projectDirectory = Path.Combine(root, name);
        _ = Directory.CreateDirectory(projectDirectory);
        string project = Path.Combine(projectDirectory, name + ".csproj");
        await File.WriteAllTextAsync(
            project,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>",
            cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(
            Path.Combine(projectDirectory, sourceFileName), source, cancellationToken).ConfigureAwait(false);
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add(project);
        start.ArgumentList.Add("--configuration");
        start.ArgumentList.Add("Debug");
        start.ArgumentList.Add("-v:q");
        using Process process = Process.Start(start)!;
        Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardErrorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task exitTask = process.WaitForExitAsync(cancellationToken);
        await Task.WhenAll(standardOutputTask, standardErrorTask, exitTask).ConfigureAwait(false);
        string standardOutput = await standardOutputTask.ConfigureAwait(false);
        string standardError = await standardErrorTask.ConfigureAwait(false);
        process.ExitCode.ShouldBe(0, standardOutput + standardError);
        return Path.Combine(projectDirectory, "bin", "Debug", "net10.0", name + ".dll");
    }

    private static string DescribeSource(string typeName, string json) =>
        "namespace Hexalith; public static class " + typeName + " { public static string Describe() => " + JsonSerializer.Serialize(json) + "; }";

    private static ModuleManifest CreateManifest(string descriptorAssembly) => new(
        "hexalith.module-manifest.v1",
        "descriptor-test",
        [new ModuleDescriptor("module-a", descriptorAssembly, [], "module-a", "module-a", "module-a")],
        new PlatformPins("3.106.0", "1.18.2", "1.18.8", "4.5.0"),
        null,
        new Dictionary<string, ModuleProfile>(StringComparer.Ordinal));

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "hexalith-descriptor-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        return root;
    }
}
