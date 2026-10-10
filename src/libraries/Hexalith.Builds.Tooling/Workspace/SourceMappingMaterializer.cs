// <copyright file="SourceMappingMaterializer.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Reflection;
using System.Text;
using System.Text.Json;

using Hexalith.Builds.Tooling.Filesystem;

/// <summary>Writes the immutable mapping and MSBuild imports into one run workspace.</summary>
public static class SourceMappingMaterializer
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };

    /// <summary>Gets the absolute path of the mapping props import.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The props path.</returns>
    public static string PropsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.props");

    /// <summary>Gets the late import that overrides consumer Directory.Build.targets properties.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The late props path.</returns>
    public static string LatePropsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.late.props");

    /// <summary>Gets the absolute path of the mapping targets import.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The targets path.</returns>
    public static string TargetsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.targets");

    /// <summary>Writes one mapping and its embedded MSBuild imports.</summary>
    /// <param name="mapping">The resolved mapping.</param>
    /// <param name="workspace">The run workspace.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task for materialization.</returns>
    public static async Task WriteAsync(SourceMapping mapping, string workspace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        string directory = Path.Combine(workspace, "source-mapping");
        _ = Directory.CreateDirectory(directory);
        await File.WriteAllBytesAsync(
            Path.Combine(directory, "mapping.json"),
            JsonSerializer.SerializeToUtf8Bytes(mapping, _jsonOptions),
            cancellationToken).ConfigureAwait(false);
        await WriteResourceAsync("SourceMapping.props", PropsPath(workspace), cancellationToken).ConfigureAwait(false);
        await WriteResourceAsync("SourceMapping.targets", TargetsPath(workspace), cancellationToken).ConfigureAwait(false);
        string props = CreateProps(mapping, workspace);
        await File.WriteAllTextAsync(Path.Combine(directory, "SourceMapping.values.props"), props, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(LatePropsPath(workspace), props, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "SourceMapping.values.targets"), CreateTargets(mapping), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Gets the explicit MSBuild import environment for project launches.</summary>
    /// <param name="mapping">The mapping.</param>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>MSBuild import values.</returns>
    public static IReadOnlyDictionary<string, string> Environment(SourceMapping mapping, string workspace)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CustomAfterMicrosoftCommonProps"] = PropsPath(workspace),
            ["CustomAfterDirectoryBuildTargets"] = LatePropsPath(workspace),
            ["AfterMicrosoftNETSdkTargets"] = TargetsPath(workspace),
            ["HEXALITH_SOURCE_MAPPING_HASH"] = mapping.ContentHash,
            ["Configuration"] = mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release",
        };
    }

    private static string CreateProps(SourceMapping mapping, string workspace)
    {
        StringBuilder xml = new();
        _ = xml.AppendLine("<Project><PropertyGroup>");
        string hostRoot = ConditionLiteral(Path.Combine(workspace, "hosts") + Path.DirectorySeparatorChar);
        _ = xml.Append("<HexalithSourceMappingApplies Condition=\"!$([System.String]::Copy('$(MSBuildProjectFullPath)').StartsWith('")
            .Append(hostRoot)
            .Append(FilesystemPathRules.Comparison(workspace) == StringComparison.OrdinalIgnoreCase
                ? "', System.StringComparison.OrdinalIgnoreCase))"
                : "', System.StringComparison.Ordinal))")
            .AppendLine("\">true</HexalithSourceMappingApplies>");
        _ = xml.AppendLine("</PropertyGroup><PropertyGroup Condition=\"'$(HexalithSourceMappingApplies)' == 'true'\">");
        Property(xml, "HexalithSourceMappingMode", mapping.Mode == WorkspaceMode.Source ? "source" : "package");
        Property(xml, "HexalithSourceMappingHash", mapping.ContentHash);
        Property(xml, "HexalithSourceMappingRoot", mapping.Root);
        Property(xml, "HexalithSourceMappingActiveModule", mapping.ActiveModule);
        Property(xml, "HexalithSourceMappingHasGit", mapping.HasGit ? "true" : "false");
        Property(xml, "Configuration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release");
        if (mapping.HasGit)
        {
            Property(xml, "UseHexalithProjectReferences", mapping.Mode == WorkspaceMode.Source ? "true" : "false");
            Property(xml, "UseNuGetDeps", mapping.Mode == WorkspaceMode.Package ? "true" : "false");
            foreach (SourceMappingEntry entry in mapping.Entries)
            {
                string suffix = SourceMapping.PropertySuffix(entry.Identity);
                Property(xml, "Hexalith" + suffix + "Root", entry.Path ?? Path.Combine(mapping.Root, "references", entry.Identity));
                Property(xml, "Hexalith" + suffix + "FromSource", entry.Origin == "source" ? "true" : "false");
                if (string.Equals(entry.Identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
                {
                    Property(xml, "HexalithCommonsHttpFromSource", entry.Origin == "source" ? "true" : "false");
                    Property(xml, "HexalithCommonsServiceDefaultsFromSource", entry.Origin == "source" ? "true" : "false");
                }

                if (string.Equals(entry.Identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
                {
                    Property(xml, "HexalithTenantsBasePath", entry.Origin == "source" ? Path.Combine(entry.Path!, "src") : string.Empty);
                }
            }
        }

        _ = xml.AppendLine("</PropertyGroup></Project>");
        return xml.ToString();
    }

    private static string CreateTargets(SourceMapping mapping)
    {
        if (!mapping.HasGit)
        {
            return "<Project />";
        }

        const string condition = "'$(HexalithSourceMappingApplies)' == 'true' and '$(HexalithSourceMappingHasGit)' == 'true'";
        return "<Project InitialTargets=\"HexalithValidateSourceProjectsBeforeRestore\">"
            + "<Target Name=\"HexalithReconcileSourceMapping\" BeforeTargets=\"Restore;_GenerateRestoreGraphProjectEntry;_GenerateRestoreProjectPathItemsPerFramework;_GenerateProjectRestoreGraphPerFramework;CollectPackageReferences;AssignProjectConfiguration;_SplitProjectReferencesByFileExistence;_GetProjectReferenceTargetFrameworkProperties;ResolveReferences;ResolvePackageAssets\" Condition=\"" + condition + "\">"
            + "<SourceMappingReconciliationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" Projects=\"@(ProjectReference)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" Configuration=\"$(Configuration)\" Platform=\"$(Platform)\" TargetFramework=\"$(TargetFramework)\">"
            + "<Output TaskParameter=\"SelectedProjects\" ItemName=\"_HXWSelectedProjects\" /><Output TaskParameter=\"SelectedPackages\" ItemName=\"_HXWSelectedPackages\" />"
            + "</SourceMappingReconciliationTask><ItemGroup><ProjectReference Remove=\"@(ProjectReference)\" /><PackageReference Remove=\"@(PackageReference)\" />"
            + "<ProjectReference Include=\"@(_HXWSelectedProjects)\" /><PackageReference Include=\"@(_HXWSelectedPackages)\" /></ItemGroup></Target>"
            + "<Target Name=\"HexalithValidateSourceProjectsBeforeRestore\" DependsOnTargets=\"HexalithReconcileSourceMapping\" BeforeTargets=\"Restore;_GenerateRestoreGraphProjectEntry;_GenerateRestoreProjectPathItemsPerFramework;_GenerateProjectRestoreGraphPerFramework;CollectPackageReferences;AssignProjectConfiguration;_SplitProjectReferencesByFileExistence;PrepareProjectReferences;ResolveProjectReferences\" Condition=\"" + condition + "\">"
            + "<SourceMappingValidationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" AssetsFile=\"$(ProjectAssetsFile)\" Projects=\"@(ProjectReference)\" ProjectsOnly=\"true\" /></Target>"
            + "<Target Name=\"HexalithValidateSourceMapping\" DependsOnTargets=\"ResolvePackageAssets\" BeforeTargets=\"ResolveReferences;CoreCompile\" Condition=\"" + condition + "\">"
            + "<SourceMappingValidationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" AssetsFile=\"$(ProjectAssetsFile)\" Projects=\"@(ProjectReference)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" /></Target></Project>";
    }

    private static void Property(StringBuilder xml, string name, string value) => _ = xml.Append('<').Append(name).Append('>').Append(XmlEscape(MsBuildEscape(value))).Append("</").Append(name).AppendLine(">");

    private static string ConditionLiteral(string value) => XmlEscape(MsBuildEscape(value).Replace("'", "%27", StringComparison.Ordinal));

    private static string MsBuildEscape(string value) => value
        .Replace("%", "%25", StringComparison.Ordinal)
        .Replace("$", "%24", StringComparison.Ordinal)
        .Replace("@", "%40", StringComparison.Ordinal)
        .Replace(";", "%3B", StringComparison.Ordinal)
        .Replace("*", "%2A", StringComparison.Ordinal)
        .Replace("?", "%3F", StringComparison.Ordinal);

    private static string XmlEscape(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static async Task WriteResourceAsync(string fileName, string destination, CancellationToken cancellationToken)
    {
        Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Hexalith.Builds.Tooling.Workspace." + fileName)
            ?? throw new InvalidOperationException($"Embedded MSBuild resource '{fileName}' is missing.");
        await using (stream.ConfigureAwait(false))
        {
            using StreamReader reader = new(stream);
            string content = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            content = content.Replace("__TOOLING_ASSEMBLY__", XmlEscape(MsBuildEscape(Assembly.GetExecutingAssembly().Location)), StringComparison.Ordinal);
            await File.WriteAllTextAsync(destination, content, cancellationToken).ConfigureAwait(false);
        }
    }
}
