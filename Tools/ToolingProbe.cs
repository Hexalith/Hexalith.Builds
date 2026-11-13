// <copyright file="ToolingProbe.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>
#:property TargetFramework=net11.0
#:property PublishAot=false
#:property TreatWarningsAsErrors=true

namespace Hexalith.Builds.Qualification;

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

/// <summary>Runs offline tooling reflection in its required runtime, independently of PowerShell's bundled assemblies.</summary>
internal static class ToolingProbe
{
    /// <summary>Validates a rebuilt catalog, inspects packaged resources, or resolves local Aspire paths.</summary>
    /// <param name="args">The operation, tooling assembly path, and optional snapshot root or private workspace.</param>
    /// <returns>Zero for success; three when local Aspire prerequisites are unavailable.</returns>
    /// <exception cref="ArgumentException">The requested operation or argument count is invalid.</exception>
    /// <exception cref="InvalidOperationException">The catalog does not contain the required mutations.</exception>
    public static int Main(string[] args)
    {
        if (args.Length < 2)
        {
            throw new ArgumentException("Provide an operation and a tooling assembly path.");
        }

        string assemblyPath = Path.GetFullPath(args[1]);
        string directory = Path.GetDirectoryName(assemblyPath)!;
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            string dependency = Path.Combine(directory, name.Name + ".dll");
            return File.Exists(dependency) ? context.LoadFromAssemblyPath(dependency) : null;
        };
        Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        Type catalog = assembly.GetType("Hexalith.Builds.Tooling.Manifest.PlatformVersionCatalog", true)!;
        object current = catalog.GetProperty("Current")!.GetValue(null)!;
        Type pins = assembly.GetType("Hexalith.Builds.Tooling.Manifest.SupportedPlatformPins", true)!;

        switch (args[0])
        {
            case "catalog" when args.Length == 3:
                if ((string?)catalog.GetProperty("DaprRuntimeVersion")!.GetValue(current) != "2.3.4"
                    || (string?)catalog.GetProperty("FrontComposerVersion")!.GetValue(current) != "4.6.9"
                    || (string?)catalog.GetProperty("DaprCliVersion")!.GetValue(current) != "2.3.5")
                {
                    throw new InvalidOperationException("The rebuilt current catalog did not reflect the catalog mutation.");
                }

                if ((string?)pins.GetProperty("EventStoreVersion")!.GetValue(null) != "4.2.0"
                    || (string?)pins.GetProperty("DaprRuntimeVersion")!.GetValue(null) != "1.19.1"
                    || (string?)pins.GetProperty("DaprSdkVersion")!.GetValue(null) != "1.19.1"
                    || (string?)pins.GetProperty("FrontComposerVersion")!.GetValue(null) != "5.0.0")
                {
                    throw new InvalidOperationException("The current catalog mutation changed the frozen v1 compatibility pins.");
                }

                foreach (string snapshot in Directory.EnumerateFiles(args[2], "catalog.json", SearchOption.AllDirectories))
                {
                    _ = catalog.GetMethod("Parse")!.Invoke(null, [File.ReadAllText(snapshot)]);
                }

                return 0;
            case "inspect" when args.Length == 2:
                Dictionary<string, string> schemas = new(StringComparer.Ordinal)
                {
                    ["hexalith.module-manifest.v2.json"] = ReadResource(assembly, "Hexalith.Builds.Tooling.Manifest.hexalith.module-manifest.v2.json"),
                    ["hexalith.test-environment.v1.json"] = ReadResource(assembly, "Hexalith.Builds.Tooling.TestEnvironment.hexalith.test-environment.v1.json"),
                    ["hexalith.test-result.v1.json"] = ReadResource(assembly, "Hexalith.Builds.Tooling.TestEnvironment.hexalith.test-result.v1.json"),
                };

                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemas,
                    catalog = ReadResource(assembly, "Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json"),
                    catalogEventStoreVersion = catalog.GetProperty("EventStoreVersion")!.GetValue(current),
                    facadeEventStoreVersion = pins.GetProperty("EventStoreVersion")!.GetValue(null),
                }));
                return 0;
            case "aspire" when args.Length == 3:
                Type paths = assembly.GetType("Hexalith.Builds.Tooling.Runtime.CompositionAspireHostPaths", true)!;
                Type toolchain = assembly.GetType("Hexalith.Builds.Tooling.Runtime.CompositionToolchainPins", true)!;
                object?[] parameters = [
                    paths.GetMethod("DefaultPackagesRoot", Type.EmptyTypes)!.Invoke(null, null),
                    toolchain.GetProperty("AspireAppHostSdkVersion")!.GetValue(null),
                    paths.GetMethod("CurrentRuntimeIdentifier")!.Invoke(null, null),
                    null,
                ];
                object? diagnostic = paths.GetMethod("TryResolve")!.Invoke(null, parameters);
                if (diagnostic is not null)
                {
                    Type type = diagnostic.GetType();
                    Console.Error.WriteLine($"{type.GetProperty("RuleId")!.GetValue(diagnostic)} {type.GetProperty("Message")!.GetValue(diagnostic)}");
                    return 3;
                }

                object environment = paths.GetMethod("CreateEnvironment")!.Invoke(parameters[3], [args[2]])!;
                Console.WriteLine(JsonSerializer.Serialize(environment));
                return 0;
            default:
                throw new ArgumentException("The tooling probe operation or its arguments are invalid.");
        }
    }

    /// <summary>Reads one required embedded resource without changing its text.</summary>
    /// <param name="assembly">The actual tooling assembly under examination.</param>
    /// <param name="name">The exact embedded resource identity.</param>
    /// <returns>The resource text, including its original newline characters.</returns>
    /// <exception cref="InvalidOperationException">The required resource is absent.</exception>
    private static string ReadResource(Assembly assembly, string name)
    {
        using Stream stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("The tooling omitted embedded resource " + name);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
