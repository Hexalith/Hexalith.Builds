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

    /// <summary>Gets the import that records project-selected roots before consumer targets.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The pre-targets props path.</returns>
    public static string BeforeTargetsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.before-targets.props");

    /// <summary>Gets the late import that overrides consumer Directory.Build.targets properties.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The late props path.</returns>
    public static string LatePropsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.late.props");

    /// <summary>Gets the absolute path of the mapping targets import.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The targets path.</returns>
    public static string TargetsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.targets");

    /// <summary>Gets the final dependency-boundary validation import.</summary>
    /// <param name="workspace">The run workspace.</param>
    /// <returns>The final targets path.</returns>
    public static string FinalTargetsPath(string workspace) => Path.Combine(workspace, "source-mapping", "SourceMapping.final.targets");

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
        string props = CreateProps(mapping, workspace, initializeObserved: true);
        await File.WriteAllTextAsync(Path.Combine(directory, "SourceMapping.values.props"), props, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(BeforeTargetsPath(workspace), CreateBeforeTargetsProps(mapping, workspace), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(LatePropsPath(workspace), CreateProps(mapping, workspace, initializeObserved: false), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, "SourceMapping.values.targets"), CreateTargets(mapping, workspace), cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(FinalTargetsPath(workspace), CreateFinalTargets(mapping, workspace), cancellationToken).ConfigureAwait(false);
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
            ["CustomBeforeDirectoryBuildProps"] = TargetsPath(workspace),
            ["CustomAfterMicrosoftCommonProps"] = PropsPath(workspace),
            ["CustomBeforeDirectoryBuildTargets"] = BeforeTargetsPath(workspace),
            ["CustomAfterDirectoryBuildTargets"] = LatePropsPath(workspace),
            ["AfterMicrosoftNETSdkTargets"] = FinalTargetsPath(workspace),
            ["HEXALITH_SOURCE_MAPPING_HASH"] = mapping.ContentHash,
            ["Configuration"] = mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release",
        };
    }

    private static string CreateProps(SourceMapping mapping, string workspace, bool initializeObserved)
    {
        StringBuilder xml = new();
        _ = xml.AppendLine("<Project>");
        if (!initializeObserved)
        {
            _ = xml.Append("<PropertyGroup Condition=\"").Append(ConsumerProjectCondition(mapping, workspace)).AppendLine("\">");
            foreach (string name in MappedSelectionProperties(mapping))
            {
                _ = xml.Append("<_HXWPreLate").Append(name).Append(">$(").Append(name).Append(")</_HXWPreLate").Append(name).AppendLine(">");
            }

            _ = xml.AppendLine("</PropertyGroup>");
        }

        _ = xml.AppendLine("<PropertyGroup>");
        _ = xml.Append("<HexalithSourceMappingApplies Condition=\"")
            .Append(ConsumerProjectCondition(mapping, workspace))
            .AppendLine("\">true</HexalithSourceMappingApplies>");
        _ = xml.AppendLine("</PropertyGroup><PropertyGroup Condition=\"'$(HexalithSourceMappingApplies)' == 'true'\">");
        Property(xml, "HexalithSourceMappingMode", mapping.Mode == WorkspaceMode.Source ? "source" : "package");
        Property(xml, "HexalithSourceMappingHash", mapping.ContentHash);
        Property(xml, "HexalithSourceMappingRoot", mapping.Root);
        if (initializeObserved)
        {
            Property(xml, "_HXWObservedHexalithSourceMappingRoot", mapping.Root);
        }

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
                string selectedRoot = entry.Path ?? Path.Combine(mapping.Root, "references", entry.Identity);
                Property(xml, "Hexalith" + suffix + "Root", selectedRoot);
                if (initializeObserved)
                {
                    Property(xml, "_HXWObservedHexalith" + suffix + "Root", selectedRoot);
                }

                Property(xml, "Hexalith" + suffix + "FromSource", entry.Origin == "source" ? "true" : "false");
                if (string.Equals(entry.Identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
                {
                    Property(xml, "HexalithCommonsHttpFromSource", entry.Origin == "source" ? "true" : "false");
                    Property(xml, "HexalithCommonsServiceDefaultsFromSource", entry.Origin == "source" ? "true" : "false");
                }

                if (string.Equals(entry.Identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
                {
                    string tenantsBasePath = entry.Origin == "source" ? Path.Combine(entry.Path!, "src") : string.Empty;
                    Property(xml, "HexalithTenantsBasePath", tenantsBasePath);
                    if (initializeObserved)
                    {
                        Property(xml, "_HXWObservedHexalithTenantsBasePath", tenantsBasePath);
                    }
                }
            }
        }

        _ = xml.AppendLine("</PropertyGroup></Project>");
        return xml.ToString();
    }

    private static string CreateBeforeTargetsProps(SourceMapping mapping, string workspace)
    {
        StringBuilder xml = new();
        _ = xml.Append("<Project><PropertyGroup Condition=\"").Append(ConsumerProjectCondition(mapping, workspace)).Append("\">");
        foreach (string name in MappedSelectionProperties(mapping))
        {
            _ = xml.Append("<_HXWObserved").Append(name).Append(">$(").Append(name).Append(")</_HXWObserved").Append(name).Append('>');
        }

        _ = xml.AppendLine("</PropertyGroup></Project>");
        return xml.ToString();
    }

    private static IEnumerable<string> MappedSelectionProperties(SourceMapping mapping)
    {
        yield return "HexalithSourceMappingRoot";
        yield return "Configuration";
        yield return "UseHexalithProjectReferences";
        yield return "UseNuGetDeps";
        foreach (string identity in mapping.Entries.Select(entry => entry.Identity))
        {
            yield return "Hexalith" + SourceMapping.PropertySuffix(identity) + "Root";
            yield return "Hexalith" + SourceMapping.PropertySuffix(identity) + "FromSource";
            if (string.Equals(identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
            {
                yield return "HexalithTenantsBasePath";
            }

            if (string.Equals(identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
            {
                yield return "HexalithCommonsHttpFromSource";
                yield return "HexalithCommonsServiceDefaultsFromSource";
            }
        }
    }

    private static string CreateTargets(SourceMapping mapping, string workspace)
    {
        if (!mapping.HasGit)
        {
            return "<Project />";
        }

        string condition = ConsumerProjectCondition(mapping, workspace);
        StringBuilder guard = new();
        GuardProperty(guard, "HexalithSourceMappingApplies", "true");
        GuardProperty(guard, "HexalithSourceMappingHasGit", "true");
        GuardProperty(guard, "HexalithSourceMappingMode", mapping.Mode == WorkspaceMode.Source ? "source" : "package");
        GuardProperty(guard, "HexalithSourceMappingHash", mapping.ContentHash);
        GuardProperty(guard, "HexalithSourceMappingRoot", mapping.Root);
        GuardProperty(guard, "_HXWObservedHexalithSourceMappingRoot", mapping.Root);
        GuardProperty(guard, "HexalithSourceMappingActiveModule", mapping.ActiveModule);
        GuardProperty(guard, "Configuration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release");
        GuardProperty(guard, "UseHexalithProjectReferences", mapping.Mode == WorkspaceMode.Source ? "true" : "false");
        GuardProperty(guard, "UseNuGetDeps", mapping.Mode == WorkspaceMode.Package ? "true" : "false");
        GuardObservedProperty(guard, "_HXWObservedConfiguration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release");
        GuardObservedProperty(guard, "_HXWObservedUseHexalithProjectReferences", mapping.Mode == WorkspaceMode.Source ? "true" : "false");
        GuardObservedProperty(guard, "_HXWObservedUseNuGetDeps", mapping.Mode == WorkspaceMode.Package ? "true" : "false");
        GuardProperty(guard, "CustomBeforeDirectoryBuildProps", TargetsPath(workspace));
        GuardProperty(guard, "CustomBeforeDirectoryBuildTargets", BeforeTargetsPath(workspace));
        GuardProperty(guard, "CustomAfterDirectoryBuildTargets", LatePropsPath(workspace));
        GuardPropertyContains(guard, "AfterMicrosoftNETSdkTargets", FinalTargetsPath(workspace));
        foreach (SourceMappingEntry entry in mapping.Entries)
        {
            string fromSource = entry.Origin == "source" ? "true" : "false";
            string rootProperty = "Hexalith" + SourceMapping.PropertySuffix(entry.Identity) + "Root";
            string selectedRoot = entry.Path ?? Path.Combine(mapping.Root, "references", entry.Identity);
            GuardProperty(guard, rootProperty, selectedRoot);
            GuardProperty(guard, "_HXWObserved" + rootProperty, selectedRoot);
            GuardProperty(guard, "Hexalith" + SourceMapping.PropertySuffix(entry.Identity) + "FromSource", fromSource);
            GuardObservedProperty(guard, "_HXWObservedHexalith" + SourceMapping.PropertySuffix(entry.Identity) + "FromSource", fromSource);
            if (string.Equals(entry.Identity, "Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
            {
                string tenantsBasePath = entry.Origin == "source" ? Path.Combine(entry.Path!, "src") : string.Empty;
                GuardProperty(guard, "HexalithTenantsBasePath", tenantsBasePath);
                GuardProperty(guard, "_HXWObservedHexalithTenantsBasePath", tenantsBasePath);
            }

            if (string.Equals(entry.Identity, "Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
            {
                GuardProperty(guard, "HexalithCommonsHttpFromSource", fromSource);
                GuardProperty(guard, "HexalithCommonsServiceDefaultsFromSource", fromSource);
                GuardObservedProperty(guard, "_HXWObservedHexalithCommonsHttpFromSource", fromSource);
                GuardObservedProperty(guard, "_HXWObservedHexalithCommonsServiceDefaultsFromSource", fromSource);
            }
        }

        return "<Project InitialTargets=\"HexalithVerifySourceMappingSelection;HexalithValidateSourceProjectsBeforeRestore\">"
            + "<Target Name=\"HexalithVerifySourceMappingSelection\" Condition=\"" + condition + "\">" + guard + "</Target>"
            + "<Target Name=\"HexalithReconcileSourceMapping\" BeforeTargets=\"Restore;_GenerateRestoreGraphProjectEntry;_GenerateRestoreProjectPathItemsPerFramework;_GenerateProjectRestoreGraphPerFramework;CollectPackageReferences;AssignProjectConfiguration;_SplitProjectReferencesByFileExistence;_GetProjectReferenceTargetFrameworkProperties;ResolveReferences;ResolvePackageAssets\" Condition=\"" + condition + "\">"
            + guard
            + "<SourceMappingReconciliationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" Projects=\"@(ProjectReference)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" Configuration=\"$(Configuration)\" Platform=\"$(Platform)\" TargetFramework=\"$(TargetFramework)\" ManagePackageVersionsCentrally=\"$(ManagePackageVersionsCentrally)\">"
            + "<Output TaskParameter=\"SelectedProjects\" ItemName=\"_HXWSelectedProjects\" /><Output TaskParameter=\"SelectedPackages\" ItemName=\"_HXWSelectedPackages\" />"
            + "</SourceMappingReconciliationTask><ItemGroup><ProjectReference Remove=\"@(ProjectReference)\" /><PackageReference Remove=\"@(PackageReference)\" />"
            + "<ProjectReference Include=\"@(_HXWSelectedProjects)\" /><PackageReference Include=\"@(_HXWSelectedPackages)\" /></ItemGroup></Target>"
            + "<Target Name=\"HexalithValidateSourceProjectsBeforeRestore\" DependsOnTargets=\"HexalithReconcileSourceMapping\" BeforeTargets=\"Restore;_GenerateRestoreGraphProjectEntry;_GenerateRestoreProjectPathItemsPerFramework;_GenerateProjectRestoreGraphPerFramework;CollectPackageReferences;AssignProjectConfiguration;_SplitProjectReferencesByFileExistence;PrepareProjectReferences;ResolveProjectReferences\" Condition=\"" + condition + "\">"
            + guard
            + "<SourceMappingValidationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" AssetsFile=\"$(ProjectAssetsFile)\" Projects=\"@(ProjectReference)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" ProjectsOnly=\"true\" /></Target>"
            + "<Target Name=\"HexalithValidateSourceMapping\" DependsOnTargets=\"ResolvePackageAssets\" BeforeTargets=\"ResolveReferences;CoreCompile\" Condition=\"" + condition + "\">"
            + guard
            + "<SourceMappingValidationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" AssetsFile=\"$(ProjectAssetsFile)\" Projects=\"@(ProjectReference)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" /></Target></Project>";
    }

    private static string CreateFinalTargets(SourceMapping mapping, string workspace)
    {
        if (!mapping.HasGit)
        {
            return "<Project />";
        }

        string condition = ConsumerProjectCondition(mapping, workspace);
        StringBuilder guard = new();
        GuardProperty(guard, "HexalithSourceMappingApplies", "true");
        GuardProperty(guard, "HexalithSourceMappingMode", mapping.Mode == WorkspaceMode.Source ? "source" : "package");
        GuardProperty(guard, "HexalithSourceMappingHash", mapping.ContentHash);
        GuardProperty(guard, "HexalithSourceMappingRoot", mapping.Root);
        GuardProperty(guard, "Configuration", mapping.Mode == WorkspaceMode.Source ? "Debug" : "Release");
        GuardProperty(guard, "UseHexalithProjectReferences", mapping.Mode == WorkspaceMode.Source ? "true" : "false");
        GuardProperty(guard, "UseNuGetDeps", mapping.Mode == WorkspaceMode.Package ? "true" : "false");
        foreach (SourceMappingEntry entry in mapping.Entries)
        {
            string suffix = SourceMapping.PropertySuffix(entry.Identity);
            string fromSource = entry.Origin == "source" ? "true" : "false";
            GuardProperty(guard, "Hexalith" + suffix + "Root", entry.Path ?? Path.Combine(mapping.Root, "references", entry.Identity));
            GuardProperty(guard, "Hexalith" + suffix + "FromSource", fromSource);
            if (entry.Identity.Equals("Hexalith.Commons", StringComparison.OrdinalIgnoreCase))
            {
                GuardProperty(guard, "HexalithCommonsHttpFromSource", fromSource);
                GuardProperty(guard, "HexalithCommonsServiceDefaultsFromSource", fromSource);
            }

            if (entry.Identity.Equals("Hexalith.Tenants", StringComparison.OrdinalIgnoreCase))
            {
                GuardProperty(guard, "HexalithTenantsBasePath", entry.Origin == "source" ? Path.Combine(entry.Path!, "src") : string.Empty);
            }
        }

        return "<Project><Target Name=\"HexalithValidateFinalSourceProjects\" BeforeTargets=\"ResolveProjectReferences;_ResolveProjectReferences;GetTargetPath\" Condition=\"" + condition + "\">"
            + guard
            + "<SourceMappingValidationTask MappingFile=\"$(MSBuildThisFileDirectory)mapping.json\" MappingHash=\"$(HexalithSourceMappingHash)\" AssetsFile=\"$(ProjectAssetsFile)\" Projects=\"@(ProjectReference);@(ProjectReferenceWithConfiguration);@(_MSBuildProjectReferenceExistent)\" Packages=\"@(PackageReference)\" PackageVersions=\"@(PackageVersion)\" ProjectsOnly=\"true\" /></Target></Project>";
    }

    private static string ConsumerProjectCondition(SourceMapping mapping, string workspace)
    {
        StringComparison comparison = FilesystemPathRules.Comparison(workspace);
        string comparisonName = comparison == StringComparison.OrdinalIgnoreCase ? "OrdinalIgnoreCase" : "Ordinal";
        string hostPrefix = Path.Combine(workspace, "hosts") + Path.DirectorySeparatorChar;
        StringBuilder condition = new("!$([System.String]::Copy('$(MSBuildProjectFullPath)').StartsWith('");
        _ = condition.Append(ConditionLiteral(hostPrefix)).Append("', System.StringComparison.").Append(comparisonName).Append("))");
        foreach (string buildsRoot in mapping.Entries.Where(entry => entry.Identity.Equals("Hexalith.Builds", StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Path ?? Path.Combine(mapping.Root, "references", "Hexalith.Builds")))
        {
            foreach (string host in new[] { "Hexalith.Builds.Module.EventStoreHost", "Hexalith.Builds.Module.UiHost" })
            {
                string project = Path.Combine(buildsRoot, "src", "hosts", host, host + ".csproj");
                _ = condition.Append(" and !$([System.String]::Copy('$(MSBuildProjectFullPath)').Equals('")
                    .Append(ConditionLiteral(project)).Append("', System.StringComparison.").Append(comparisonName).Append("))");
            }
        }

        return condition.ToString();
    }

    private static void GuardProperty(StringBuilder xml, string name, string value) =>
        _ = xml.Append("<Error Condition=\"'$(").Append(name).Append(")' != '").Append(ConditionLiteral(value))
            .Append("'\" Text=\"HXW006: Source mapping property '").Append(name).AppendLine("' differs from the selected mapping.\" />");

    private static void GuardObservedProperty(StringBuilder xml, string name, string value) =>
        _ = xml.Append("<Error Condition=\"'$(").Append(name).Append(")' != '' and '$(").Append(name)
            .Append(")' != '").Append(ConditionLiteral(value)).Append("'\" Text=\"HXW006: Consumer-selected mapping property '")
            .Append(name).AppendLine("' differs from the selected mapping.\" />");

    private static void GuardPropertyContains(StringBuilder xml, string name, string value) =>
        _ = xml.Append("<Error Condition=\"!$([System.String]::Copy('$(").Append(name)
            .Append(")').Contains('").Append(ConditionLiteral(value)).Append("', System.StringComparison.Ordinal))\" Text=\"HXW006: Source mapping property '")
            .Append(name).AppendLine("' does not include the selected mapping import.\" />");

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
