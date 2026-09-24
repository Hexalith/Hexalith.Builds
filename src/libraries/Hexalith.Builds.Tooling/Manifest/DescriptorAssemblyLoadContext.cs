// <copyright file="DescriptorAssemblyLoadContext.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Reflection;
using System.Runtime.Loader;

/// <summary>
/// Resolves descriptor dependencies only from their build output or the selected runtime.
/// </summary>
/// <param name="assemblyPath">The descriptor assembly path.</param>
internal sealed class DescriptorAssemblyLoadContext(string assemblyPath) : AssemblyLoadContext(isCollectible: false)
{
    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);
    private readonly string _outputDirectory = Path.GetDirectoryName(assemblyPath)!;
    private readonly HashSet<string> _runtimeAssemblyNames = GetRuntimeAssemblyNames();

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (_runtimeAssemblyNames.Contains(assemblyName.Name ?? string.Empty))
        {
            return null;
        }

        string? candidate = _resolver.ResolveAssemblyToPath(assemblyName);
        return candidate is null || !IsDirectOutputFile(candidate)
            ? throw new FileNotFoundException("Descriptor dependency is outside its build output.")
            : LoadFromAssemblyPath(candidate);
    }

    /// <inheritdoc />
    protected override nint LoadUnmanagedDll(string unmanagedDllName) =>
        throw new DllNotFoundException("Descriptor native dependencies are not permitted.");

    private static HashSet<string> GetRuntimeAssemblyNames()
    {
        string? trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        return (trustedAssemblies ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => string.Equals(Path.GetDirectoryName(path), Path.GetDirectoryName(typeof(object).Assembly.Location), StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;
    }

    private bool IsDirectOutputFile(string candidate)
    {
        string fullCandidate = Path.GetFullPath(candidate);
        if (!string.Equals(Path.GetDirectoryName(fullCandidate), _outputDirectory, StringComparison.Ordinal))
        {
            return false;
        }

        FileInfo file = new(fullCandidate);
        FileSystemInfo? target = file.ResolveLinkTarget(returnFinalTarget: true);
        return target is null || string.Equals(Path.GetDirectoryName(target.FullName), _outputDirectory, StringComparison.Ordinal);
    }
}
