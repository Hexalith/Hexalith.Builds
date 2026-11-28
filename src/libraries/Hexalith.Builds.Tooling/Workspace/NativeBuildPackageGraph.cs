// <copyright file="NativeBuildPackageGraph.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Security.Cryptography;
using System.Text.Json;

using NuGet.Packaging;

/// <summary>Checks the package graph and runtime identities of a producer output.</summary>
internal static class NativeBuildPackageGraph
{
    /// <summary>Measures the requested graph against the actual runtime closure and restored package bytes.</summary>
    /// <param name="input">The evaluated producer build selection.</param>
    /// <param name="mapping">The selected package-mode source boundary.</param>
    /// <param name="componentDirectory">The private destination for retained package archives.</param>
    /// <param name="cancellationToken">The operation token.</param>
    /// <returns>Configuration identities and the complete resolved package set.</returns>
    /// <exception cref="InvalidDataException">The restore and runtime package selections differ.</exception>
    internal static (string Framework, string Rid, string AssetsDigest, IReadOnlyDictionary<string, NativeBuildPackageIdentity> Packages) Measure(NativeBuildInput input, SourceMapping mapping, string componentDirectory, CancellationToken cancellationToken)
    {
        using JsonDocument runtime = Read(Path.ChangeExtension(input.Entry, ".runtimeconfig.json"));
        string framework = runtime.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString()
            ?? throw new InvalidDataException("Prepared native build runtime has no target framework.");
        using JsonDocument dependencies = Read(Path.ChangeExtension(input.Entry, ".deps.json"));
        string target = dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()
            ?? throw new InvalidDataException("Prepared native build has no runtime dependency target.");
        string rid = target.Contains('/', StringComparison.Ordinal) ? target[(target.IndexOf('/', StringComparison.Ordinal) + 1)..] : string.Empty;
        byte[] bytes = ReadBytes(input.Assets);
        using JsonDocument assets = JsonDocument.Parse(bytes);
        JsonElement project = assets.RootElement.GetProperty("project");
        JsonElement restore = project.GetProperty("restore");
        if (!restore.TryGetProperty("restoreAuditProperties", out JsonElement audit)
            || audit.GetProperty("enableAudit").GetString() != "true" || audit.GetProperty("auditMode").GetString() != "all"
            || audit.GetProperty("auditLevel").GetString() != "low"
            || (assets.RootElement.TryGetProperty("logs", out JsonElement logs) && logs.EnumerateArray().Any(log => log.GetProperty("code").GetString() is "NU1900" or "NU1905")))
        {
            throw new InvalidDataException("Prepared native build requires a completed transitive NuGet audit at all advisory levels.");
        }

        if (restore.GetProperty("projectPath").GetString() != input.Project
            || !project.GetProperty("frameworks").TryGetProperty(framework, out _)
            || !assets.RootElement.GetProperty("targets").TryGetProperty(framework + (rid.Length == 0 ? string.Empty : "/" + rid), out JsonElement selectedTarget))
        {
            throw new InvalidDataException("Prepared native build project, framework or runtime differs from the restored target.");
        }

        JsonElement libraries = assets.RootElement.GetProperty("libraries");
        JsonElement runtimeLibraries = dependencies.RootElement.GetProperty("libraries");
        string[] folders = [.. assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(item => item.Name)];
        SortedDictionary<string, NativeBuildPackageIdentity> packages = [with(StringComparer.OrdinalIgnoreCase)];
        foreach (string name in selectedTarget.EnumerateObject().Select(item => item.Name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            JsonElement library = libraries.GetProperty(name);
            string type = library.GetProperty("type").GetString()!;
            if (type == "project")
            {
                string reference = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(input.Project)!, library.GetProperty("msbuildProject").GetString()!));
                if (!mapping.ContainsProject(reference)
                    && !RepositoryFile(mapping.PlatformIdentity!.PlatformPath, reference))
                {
                    throw new InvalidDataException("Prepared native build has a source dependency outside the package boundary.");
                }

                continue;
            }

            if (type != "package")
            {
                continue;
            }

            string packagePath = library.GetProperty("path").GetString()!;
            string hash = library.GetProperty("sha512").GetString()!;
            string archiveName = packagePath.Replace('/', '.') + ".nupkg";
            string archive = folders.Select(folder => Path.Combine(NativeBuildInventory.Resolve(folder, packagePath), archiveName)).FirstOrDefault(File.Exists)
                ?? throw new InvalidDataException("Prepared native build is missing a restored package archive.");

            using FileStream original = NativeBuildInventory.OpenFile(archive);
            if (!input.Role.StartsWith("test.", StringComparison.Ordinal) && name.StartsWith("Hexalith.", StringComparison.OrdinalIgnoreCase))
            {
                string targetDirectory = NativeBuildInventory.Resolve(Path.Combine(componentDirectory, "packages"), packagePath);
                Runtime.CompositionWorkspace.CreatePrivateDirectory(targetDirectory);
                archive = Path.Combine(targetDirectory, archiveName);
                using FileStream destination = new(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                original.CopyTo(destination);
            }

            using FileStream stream = NativeBuildInventory.OpenFile(archive);
            string rawHash = Convert.ToBase64String(SHA512.HashData(stream));
            stream.Position = 0;
            using PackageArchiveReader reader = new(stream, leaveStreamOpen: true);
            if (!string.Equals(reader.GetContentHash(cancellationToken), hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Prepared native build restored package content differs from the recorded NuGet hash.");
            }

            packages.Add(name, new NativeBuildPackageIdentity(hash, rawHash));
        }

        foreach (JsonProperty item in runtimeLibraries.EnumerateObject())
        {
            if (item.Value.GetProperty("type").GetString() == "package"
                && (!packages.TryGetValue(item.Name, out NativeBuildPackageIdentity? identity) || item.Value.GetProperty("sha512").GetString() != "sha512-" + identity.ContentHash))
            {
                throw new InvalidDataException("Prepared native build runtime package graph differs from the audited restore.");
            }
        }

        string assetsDigest = NativeBuildInventory.Hash(bytes);
        string evidence = Path.Combine(componentDirectory, "restore-evidence.json");
        using FileStream evidenceOutput = new(evidence, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(evidenceOutput, new
        {
            assetsSha256 = assetsDigest,
            project = new { frameworks = new Dictionary<string, string>(StringComparer.Ordinal) { [framework] = framework }, restoreAuditProperties = audit },
            libraries = packages.ToDictionary(package => package.Key, package => new { type = "package", sha512 = package.Value.ContentHash, archiveSha512 = package.Value.ArchiveSha512 }, StringComparer.Ordinal),
        });
        return (framework, rid, assetsDigest, packages);
    }

    /// <summary>Checks the complete runnable dependency metadata against its sealed receipt.</summary>
    /// <param name="entry">The selected runnable entry.</param>
    /// <param name="component">The immutable recorded output.</param>
    /// <exception cref="InvalidDataException">A runtime dependency or runtime identity differs.</exception>
    internal static void VerifyRuntime(string entry, NativeBuildComponent component)
    {
        using JsonDocument runtime = Read(Path.ChangeExtension(entry, ".runtimeconfig.json"));
        using JsonDocument dependencies = Read(Path.ChangeExtension(entry, ".deps.json"));
        JsonElement graph = dependencies.RootElement;
        string target = graph.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        string rid = target[(target.IndexOf('/', StringComparison.Ordinal) + 1)..];
        if (runtime.RootElement.GetProperty("runtimeOptions").GetProperty("tfm").GetString() != component.Framework
            || (target.Contains('/', StringComparison.Ordinal) ? rid : string.Empty) != component.RuntimeIdentifier)
        {
            throw new InvalidDataException("Prepared native build framework or runtime differs from its runnable output.");
        }

        foreach (JsonProperty library in graph.GetProperty("targets").GetProperty(target).EnumerateObject())
        {
            foreach (string groupName in new[] { "runtime", "native", "resources", "runtimeTargets" })
            {
                if (!library.Value.TryGetProperty(groupName, out JsonElement group))
                {
                    continue;
                }

                foreach (JsonProperty item in group.EnumerateObject())
                {
                    string filename = Path.GetFileName(item.Name);
                    string locale = item.Value.TryGetProperty("locale", out JsonElement language) ? language.GetString()! : string.Empty;
                    if (filename != "_._" && !component.Files.ContainsKey(item.Name)
                        && !component.Files.ContainsKey(locale.Length == 0 ? filename : locale + "/" + filename))
                    {
                        throw new InvalidDataException("Prepared native build runnable output is missing a declared dependency.");
                    }
                }
            }
        }

        foreach (JsonProperty package in graph.GetProperty("libraries").EnumerateObject()
            .Where(item => item.Value.GetProperty("type").GetString() == "package"))
        {
            if (!component.Packages.TryGetValue(package.Name, out NativeBuildPackageIdentity? identity) || package.Value.GetProperty("sha512").GetString() != "sha512-" + identity.ContentHash)
            {
                throw new InvalidDataException("Prepared native build runtime package graph differs from the producer receipt.");
            }
        }
    }

    private static bool RepositoryFile(string root, string file)
    {
        string relative = Path.GetRelativePath(root, file);
        return !relative.StartsWith("references" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !Path.IsPathFullyQualified(relative) && File.Exists(file);
    }

    private static JsonDocument Read(string path) => JsonDocument.Parse(ReadBytes(path));

    private static byte[] ReadBytes(string path)
    {
        using FileStream input = NativeBuildInventory.OpenFile(path);
        if (input.Length is <= 0 or > 32 * 1024 * 1024)
        {
            throw new InvalidDataException("Prepared native build metadata is missing or exceeds its size limit.");
        }

        byte[] bytes = new byte[input.Length];
        input.ReadExactly(bytes);
        return bytes;
    }
}
