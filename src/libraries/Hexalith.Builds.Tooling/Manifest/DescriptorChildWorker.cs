// <copyright file="DescriptorChildWorker.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.Reflection;
using System.Text;

/// <summary>
/// Executes descriptor entrypoints in the runner-owned, credential-free child process.
/// </summary>
public static class DescriptorChildWorker
{
    private const int _maximumResultBytes = 1_048_576;

    /// <summary>
    /// Loads one descriptor or verifies one marker type. This entrypoint is for the private child command only.
    /// </summary>
    /// <param name="arguments">The private child command arguments.</param>
    /// <param name="output">The single-result output stream.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Zero on success, or a sanitized nonzero failure code.</returns>
    public static async Task<int> RunAsync(string[] arguments, TextWriter output, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(output);

        bool isDescriptorInvocation = arguments.Length == 2 && arguments[0] is "module" or "ui";
        bool isMarkerInvocation = arguments.Length == 3 && arguments[0] == "marker";
        if ((!isDescriptorInvocation && !isMarkerInvocation)
            || !Path.IsPathFullyQualified(arguments[1])
            || !arguments[1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(arguments[1]))
        {
            return 2;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            DescriptorAssemblyLoadContext context = new(arguments[1]);
            Assembly assembly = context.LoadFromAssemblyPath(arguments[1]);
            if (arguments[0] == "marker")
            {
                Type? markerType = assembly.GetType(arguments[2], throwOnError: false, ignoreCase: false);
                return markerType is { IsPublic: true, ContainsGenericParameters: false, IsClass: true } ? 0 : 2;
            }

            string expectedTypeName = arguments[0] == "module"
                ? "Hexalith.ModuleDescriptorV1"
                : "Hexalith.UiDescriptorV1";
            Type[] exportedTypes = assembly.GetExportedTypes();
            if (exportedTypes.Length != 1
                || exportedTypes[0].FullName != expectedTypeName
                || exportedTypes[0].ContainsGenericParameters)
            {
                return 2;
            }

            MethodInfo? describe = exportedTypes[0].GetMethod(
                "Describe",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (describe?.ReturnType != typeof(string)
                || exportedTypes[0].GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
                    .Count(method => method.Name == "Describe") != 1)
            {
                return 2;
            }

            TextWriter originalOut = Console.Out;
            TextWriter originalError = Console.Error;
            string? json;
            try
            {
                Console.SetOut(TextWriter.Null);
                Console.SetError(TextWriter.Null);
                json = describe.Invoke(null, null) as string;
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalError);
            }

            if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > _maximumResultBytes)
            {
                return 2;
            }

            await output.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or FileLoadException
            or FileNotFoundException or DllNotFoundException or IOException or UnauthorizedAccessException
            or ReflectionTypeLoadException or TargetInvocationException or TypeLoadException or InvalidOperationException)
        {
            return 2;
        }
    }
}
