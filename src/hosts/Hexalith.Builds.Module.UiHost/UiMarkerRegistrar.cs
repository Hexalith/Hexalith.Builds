// <copyright file="UiMarkerRegistrar.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.UiHost;

using System.Reflection;
using System.Runtime.Loader;

using Hexalith.FrontComposer.Shell.Extensions;

/// <summary>
/// Registers run-plan-named module UI markers through the FrontComposer <c>AddHexalithDomain&lt;T&gt;</c> seam.
/// </summary>
internal static class UiMarkerRegistrar
{
    /// <summary>
    /// The configuration section that lists the marker bindings.
    /// </summary>
    public const string SectionName = "Hexalith:G4:UiMarkers";

    private static readonly HashSet<string> _resolvedAssemblies = new(StringComparer.Ordinal);

    private static readonly MethodInfo _addDomain = typeof(ServiceCollectionExtensions)
        .GetMethod(nameof(ServiceCollectionExtensions.AddHexalithDomain), BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("The FrontComposer AddHexalithDomain<T> seam is unavailable.");

    /// <summary>
    /// Loads every configured marker type and registers it with FrontComposer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The host configuration.</param>
    /// <returns>The registered marker types.</returns>
    /// <exception cref="InvalidOperationException">A binding is invalid or no marker is named.</exception>
    public static IReadOnlyList<Type> RegisterConfiguredMarkers(IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        List<Type> registered = [];
        foreach (IConfigurationSection section in configuration.GetSection(SectionName).GetChildren())
        {
            Type markerType = ResolveMarker(section["Assembly"], section["Type"]);
            _ = _addDomain.MakeGenericMethod(markerType).Invoke(null, [services]);
            registered.Add(markerType);
        }

        return registered.Count > 0
            ? registered
            : throw new InvalidOperationException("The run plan did not name any module UI marker.");
    }

    /// <summary>
    /// Loads one marker type from a validated, built assembly.
    /// </summary>
    /// <param name="assemblyPath">The absolute path of the built marker assembly.</param>
    /// <param name="typeName">The full CLR name of the marker type.</param>
    /// <returns>The public, non-generic marker class.</returns>
    /// <exception cref="InvalidOperationException">The binding does not name a built assembly and public marker class.</exception>
    private static Type ResolveMarker(string? assemblyPath, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath)
            || !Path.IsPathFullyQualified(assemblyPath)
            || !assemblyPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(assemblyPath)
            || string.IsNullOrWhiteSpace(typeName))
        {
            throw new InvalidOperationException("A configured UI marker binding does not name a built assembly and type.");
        }

        Type markerType;
        try
        {
            RegisterDependencyResolution(assemblyPath);
            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
            markerType = assembly.GetType(typeName, throwOnError: true, ignoreCase: false)!;
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException
            or TypeLoadException or ArgumentException or IOException or ReflectionTypeLoadException)
        {
            throw new InvalidOperationException("A configured UI marker assembly or type could not be loaded.", exception);
        }

        return markerType is { IsClass: true, IsPublic: true, ContainsGenericParameters: false }
            ? markerType
            : throw new InvalidOperationException("A configured UI marker is not a public non-generic class.");
    }

    /// <summary>
    /// Resolves the private dependencies of a marker assembly from its own build output (its <c>.deps.json</c>
    /// and directory), so a marker whose dependencies the host does not ship still loads.
    /// </summary>
    /// <param name="assemblyPath">The absolute marker assembly path.</param>
    private static void RegisterDependencyResolution(string assemblyPath)
    {
        lock (_resolvedAssemblies)
        {
            if (!_resolvedAssemblies.Add(assemblyPath))
            {
                return;
            }
        }

        AssemblyDependencyResolver resolver = new(assemblyPath);
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            resolver.ResolveAssemblyToPath(name) is string path ? context.LoadFromAssemblyPath(path) : null;
    }
}