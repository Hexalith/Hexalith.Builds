// <copyright file="IsolatedBuildReferenceContract.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Runtime;

using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;

/// <summary>Admits source-bound, nonshipping build-order fixture graphs in managed builds.</summary>
internal static partial class IsolatedBuildReferenceContract
{
    private static readonly string[] _fields = ["schema", "owner", "projects", "roots", "framework", "propertyPrefix", "properties", "props", "targets"];
    private static readonly string[] _reservedPrefixes = ["NuGet", "Restore", "MSBuild", "Enable", "Disable", "Run", "Skip", "Is", "Analysis", "CodeAnalysis", "Enforce", "Check", "Validate", "ContinuousIntegration", "Deterministic", "Debug", "Optimize", "Sign", "PublicSign", "DelaySign", "Repository", "SourceLink", "NoStdLib", "Warning", "Error", "Output", "Base", "Intermediate", "Default", "Assembly", "Satellite", "Compiler", "Emit", "Lang", "Nullable", "Feature", "Hexalith", "Custom", "AfterMicrosoft", "Directory", "Configuration", "Platform", "Target", "Warnings", "NoWarn", "TreatWarnings", "Use", "Version", "Package", "Build", "Import", "Exclude", "Generate"];

    /// <summary>Canonicalizes only the explicitly declared managed build-order boundary.</summary>
    /// <param name="mapping">The authenticated source/catalog mapping.</param>
    /// <param name="reference">The evaluated build-order reference.</param>
    /// <param name="owner">The actual owner project.</param>
    /// <param name="adapterState">Actual test/pack/publish/default-compile flags.</param>
    /// <param name="framework">The actual owner framework.</param>
    /// <param name="parentCommonTargets">The actual inherited common-targets chain.</param>
    /// <param name="canonical">The checked global properties.</param>
    /// <returns>Whether the closed source-bound contract was established.</returns>
    /// <exception cref="InvalidDataException">A contract refusal is caught and returned as a diagnostic.</exception>
    internal static bool TryCanonicalize(SourceMapping mapping, ITaskItem reference, string owner, string adapterState, string framework, string parentCommonTargets, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            if (mapping.ManagedAuthority is not { Kind: "accepted-delivery" or "candidate-qualification" } authority
                || mapping.Mode != WorkspaceMode.Package || adapterState != "false;false;false;false"
                || !reference.GetMetadata("ReferenceOutputAssembly").Equals("false", StringComparison.OrdinalIgnoreCase)
                || Git(mapping.Root, "rev-parse", "HEAD").Trim() != authority.ModuleHead
                || Git(mapping.Root, "write-tree").Trim() != Git(mapping.Root, "rev-parse", "HEAD^{tree}").Trim()
                || Git(mapping.EffectiveBuildsRoot, "write-tree").Trim() != Git(mapping.EffectiveBuildsRoot, "rev-parse", "HEAD^{tree}").Trim()
                || Git(mapping.EffectiveBuildsRoot, "rev-parse", "HEAD").Trim() != mapping.EffectiveCatalogCommit)
            {
                throw new InvalidDataException("Managed authority, owner shape, build-order metadata or Git identity differs.");
            }

            string contractPath = reference.GetMetadata("HexalithIsolatedBuildContract");
            string contractFile = Resolve(mapping.Root, contractPath);
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(contractFile));
            JsonElement contract = document.RootElement;
            if (!Closed(contract, _fields) || contract.GetProperty("schema").GetString() != "hexalith.isolated-build.v1"
                || contract.GetProperty("framework").GetString() != framework || !Framework().IsMatch(framework)
                || !Same(Resolve(mapping.Root, contract.GetProperty("owner").GetString()!), owner))
            {
                throw new InvalidDataException("Isolated declaration schema, framework or owner differs.");
            }

            string requested = reference.GetMetadata("FullPath");
            string[] projects = Strings(contract.GetProperty("projects"));
            string[] roots = Strings(contract.GetProperty("roots"));
            if (!projects.Any(path => Same(Resolve(mapping.Root, path), requested)) || roots.Length == 0
                || roots.Any(path => !Safe(path)) || roots.Distinct(StringComparer.OrdinalIgnoreCase).Count() != roots.Length
                || projects.Any(path => !Inside(path, roots)) || !Inside(contractPath, roots)
                || !Inside(contract.GetProperty("owner").GetString()!, roots)
                || !Inside(contract.GetProperty("props").GetString()!, roots)
                || !Inside(contract.GetProperty("targets").GetString()!, roots))
            {
                throw new InvalidDataException("Isolated project/import paths fall outside declared roots.");
            }

            // HEAD binds every declared source/import byte, including the
            // fixture's own props, targets, project sources and lock files.
            HashSet<string> tracked = VerifyTrackedRoots(mapping, roots);
            if (!tracked.Contains(contractPath) || !tracked.Contains(contract.GetProperty("owner").GetString()!)
                || !tracked.Contains(contract.GetProperty("props").GetString()!) || !tracked.Contains(contract.GetProperty("targets").GetString()!)
                || projects.Any(path => !tracked.Contains(path)))
            {
                throw new InvalidDataException("Isolated declaration, owner, child or import is not tracked.");
            }

            string prefix = contract.GetProperty("propertyPrefix").GetString()!;
            if (!PropertyPrefix().IsMatch(prefix) || _reservedPrefixes.Any(value => prefix.StartsWith(value, StringComparison.OrdinalIgnoreCase) || value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Isolated option prefix overlaps producer/SDK policy.");
            }

            Dictionary<string, string> expected = new(StringComparer.OrdinalIgnoreCase)
            {
                ["Configuration"] = "Release",
                ["TargetFramework"] = framework,
                ["UseHexalithProjectReferences"] = "false",
                ["UseNuGetDeps"] = "true",
                ["RestoreLockedMode"] = "true",
                ["CustomBeforeDirectoryBuildProps"] = string.Empty,
                ["CustomBeforeDirectoryBuildTargets"] = string.Empty,
                ["CustomAfterDirectoryBuildTargets"] = string.Empty,
                ["AfterMicrosoftNETSdkTargets"] = string.Empty,
                ["CustomAfterMicrosoftCommonProps"] = Resolve(mapping.Root, contract.GetProperty("props").GetString()!),
                ["CustomAfterMicrosoftCommonTargets"] = Resolve(mapping.Root, contract.GetProperty("targets").GetString()!),
                ["HexalithBuildsRoot"] = mapping.EffectiveBuildsRoot,
                ["HexalithIsolatedParentCommonTargets"] = parentCommonTargets,
                ["WarningsNotAsErrors"] = string.Empty,
                ["ShouldUnsetParentConfigurationAndPlatform"] = "false",
            };
            JsonElement properties = contract.GetProperty("properties");
            if (properties.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Isolated options must be an object.");
            }

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (!property.Name.StartsWith(prefix, StringComparison.Ordinal) || !PropertyName().IsMatch(property.Name)
                    || property.Value.ValueKind != JsonValueKind.String || !Literal(property.Value.GetString()!)
                    || !expected.TryAdd(property.Name, property.Value.GetString()!))
                {
                    throw new InvalidDataException("Isolated options exceed the declared property namespace.");
                }
            }

            Dictionary<string, string> supplied = [with(StringComparer.OrdinalIgnoreCase)];
            foreach (string assignment in reference.GetMetadata("AdditionalProperties").Split(';'))
            {
                int equals = assignment.IndexOf('=', StringComparison.Ordinal);
                if (equals <= 0 || !supplied.TryAdd(assignment[..equals], assignment[(equals + 1)..]))
                {
                    throw new InvalidDataException("Isolated properties contain malformed or duplicate assignments.");
                }
            }

            if (supplied.Count != expected.Count || expected.Any(pair => !supplied.TryGetValue(pair.Key, out string? value) || value != pair.Value))
            {
                throw new InvalidDataException("Isolated properties differ from the exact declared build-order contract.");
            }

            using ProjectCollection collection = new(expected);
            Project project = collection.LoadProject(requested);
            if (!project.GetPropertyValue("IsPackable").Equals("false", StringComparison.OrdinalIgnoreCase)
                || !project.GetPropertyValue("IsPublishable").Equals("false", StringComparison.OrdinalIgnoreCase)
                || project.GetPropertyValue("WarningsNotAsErrors").Length != 0
                || project.GetPropertyValue("RestoreLockedMode") != "true"
                || project.GetPropertyValue("TargetFramework") != framework
                || !project.Imports.Any(import => Same(import.ImportedProject.FullPath, expected["CustomAfterMicrosoftCommonProps"]))
                || !project.Imports.Any(import => Same(import.ImportedProject.FullPath, expected["CustomAfterMicrosoftCommonTargets"]))
                || (parentCommonTargets.Length > 0 && !project.Imports.Any(import => Same(import.ImportedProject.FullPath, parentCommonTargets))))
            {
                throw new InvalidDataException("Isolated child evaluation changes shipping/restore/warning/framework policy or drops required imports.");
            }

            HashSet<string>? restored = null;
            foreach (string import in project.Imports.Select(import => import.ImportedProject.FullPath))
            {
                string imported = Path.GetRelativePath(mapping.Root, import).Replace('\\', '/');
                string catalogRelative = Path.GetRelativePath(mapping.EffectiveBuildsRoot, import).Replace('\\', '/');
                if (Safe(catalogRelative))
                {
                    VerifyFile(mapping.EffectiveBuildsRoot, catalogRelative, Git(mapping.EffectiveBuildsRoot, "rev-parse", "HEAD:" + catalogRelative).Trim());
                }
                else if (Safe(imported) && !tracked.Contains(imported))
                {
                    restored ??= RestoredBuildImportContract.Verify(project, expected, mapping.EffectiveBuildsRoot, mapping.Root, tracked);
                    if (!restored.Contains(import))
                    {
                        throw new InvalidDataException("Isolated child imports module source outside the authenticated roots.");
                    }
                }
            }

            if (!VerifyTrackedRoots(mapping, roots).SetEquals(tracked))
            {
                throw new InvalidDataException("Isolated source inventory changed during verification.");
            }

            canonical = string.Join(';', expected.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidProjectFileException)
        {
            canonical = exception.Message;
            return false;
        }
    }

    private static HashSet<string> VerifyTrackedRoots(SourceMapping mapping, string[] roots)
    {
        string records = Git(mapping.Root, ["ls-tree", "-r", "-z", mapping.ManagedAuthority!.ModuleHead, "--", .. roots]);
        HashSet<string> found = [with(StringComparer.Ordinal)];
        foreach (string record in records.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] pieces = record.Split('\t', 2);
            string[] metadata = pieces[0].Split(' ');
            if (pieces.Length != 2 || metadata.Length != 3 || metadata[0] is not ("100644" or "100755") || metadata[1] != "blob")
            {
                throw new InvalidDataException("Isolated build roots contain nonregular source.");
            }

            VerifyFile(mapping.Root, pieces[1], metadata[2]);
            _ = found.Add(pieces[1]);
        }

        return roots.Any(root => !found.Any(path => path.StartsWith(root + "/", StringComparison.Ordinal)))
            ? throw new InvalidDataException("An isolated source root is untracked or empty.")
            : found;
    }

    private static void VerifyFile(string root, string relative, string objectId)
    {
        byte[] bytes = File.ReadAllBytes(Resolve(root, relative));
        if (bytes.Length > 8 * 1024 * 1024 || objectId.Length is not (40 or 64))
        {
            throw new InvalidDataException("Isolated source file exceeds its bound or has no Git identity.");
        }

        if (ObjectHash(bytes, objectId.Length) == objectId)
        {
            return;
        }

        // Compare canonical Git bytes after only its declared text/EOL
        // conversion. Never execute a clean/smudge filter or an encoding hook.
        string[] attributes = Git(root, "check-attr", "--cached", "-z", "text", "eol", "filter", "working-tree-encoding", "--", relative).Split('\0');
        Dictionary<string, string> selected = [with(StringComparer.Ordinal)];
        for (int index = 0; index + 2 < attributes.Length; index += 3)
        {
            selected[attributes[index + 1]] = attributes[index + 2];
        }

        bool text = selected.GetValueOrDefault("text") is "set" or "auto" || selected.GetValueOrDefault("eol") is "lf" or "crlf"
            || Git(root, "config", "--default", "false", "--get", "core.autocrlf").Trim() is "true" or "input";
        if (!text || selected.GetValueOrDefault("text") == "unset" || bytes.Contains((byte)0)
            || selected.GetValueOrDefault("filter") is not ("unspecified" or "unset")
            || selected.GetValueOrDefault("working-tree-encoding") is not ("unspecified" or "unset")
            || ObjectHash([.. bytes.Where((value, index) => value != 13 || index + 1 >= bytes.Length || bytes[index + 1] != 10)], objectId.Length) != objectId)
        {
            throw new InvalidDataException("Isolated source/import bytes differ from the authenticated revision.");
        }
    }

    private static string ObjectHash(byte[] bytes, int length)
    {
        // Git's SHA-1/SHA-256 object format identifies source; this is not a secret digest.
        using IncrementalHash hash = IncrementalHash.CreateHash(new HashAlgorithmName(length == 40 ? "SHA1" : "SHA256"));
        hash.AppendData(Encoding.UTF8.GetBytes("blob " + bytes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0"));
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static bool Closed(JsonElement value, string[] fields) => value.ValueKind == JsonValueKind.Object
        && value.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal));

    private static string[] Strings(JsonElement value) => value.ValueKind == JsonValueKind.Array
        ? [.. value.EnumerateArray().Select(item => item.GetString()!)]
        : throw new InvalidDataException("Expected isolated path array.");

    private static bool Inside(string path, string[] roots) => Safe(path) && roots.Any(root => path.StartsWith(root + "/", StringComparison.Ordinal));

    private static bool Literal(string value) => value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool Safe(string path) => !string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path) && !path.Contains('\\', StringComparison.Ordinal)
        && !path.Contains(':', StringComparison.Ordinal) && !path.Any(char.IsControl) && path.Split('/').All(part => part is not ("" or "." or ".."));

    private static string Resolve(string root, string relative)
    {
        if (!Safe(relative) || !RepositoryPathResolver.TryResolveExistingFile(root, relative, out string path))
        {
            throw new InvalidDataException("Isolated source/import path is unavailable or escapes the module.");
        }

        for (FileSystemInfo? item = new FileInfo(Path.GetFullPath(Path.Combine(root, relative))); item is not null; item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory)
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("Isolated source/import path traverses a symbolic link.");
            }
        }

        return path;
    }

    private static bool Same(string first, string second) => string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), FilesystemPathRules.Comparison(second));

    private static string Git(string root, params string[] arguments)
    {
#pragma warning disable VSTHRD002 // ITask.Execute is synchronous; bounded Git inspection never captures this context.
        CompositionProcessResult result = GitWorkspaceProcess.RunAsync(root, 4 * 1024 * 1024, CancellationToken.None, arguments).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
        return !result.Started || result.TimedOut || result.OutputTruncated || result.ExitCode != 0
            ? throw new InvalidDataException("Isolated build source identity inspection failed.")
            : result.Output;
    }

    [GeneratedRegex("\\Anet[0-9]+\\.0\\z")]
    private static partial Regex Framework();

    [GeneratedRegex("\\A[A-Za-z][A-Za-z0-9_]{7,}\\z")]
    private static partial Regex PropertyPrefix();

    [GeneratedRegex("\\A[A-Za-z][A-Za-z0-9_]*\\z")]
    private static partial Regex PropertyName();
}
