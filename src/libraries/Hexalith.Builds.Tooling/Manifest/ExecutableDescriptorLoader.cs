// <copyright file="ExecutableDescriptorLoader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Manifest;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Loads executable descriptor results through a bounded, credential-free child process.
/// </summary>
public static class ExecutableDescriptorLoader
{
    private const int _maximumResultCharacters = 1_048_576;

    private static readonly TimeSpan _childTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Loads every descriptor after manifest validation and before runtime mutation.
    /// </summary>
    /// <param name="manifest">The strictly validated manifest.</param>
    /// <param name="manifestPath">The manifest path used to resolve the checkout root.</param>
    /// <param name="childEntryAssemblyPath">The runner CLI assembly containing the private child dispatch.</param>
    /// <param name="cancellationToken">The invocation cancellation token.</param>
    /// <returns>Validated bindings or metadata-only diagnostics.</returns>
    public static async Task<ExecutableDescriptorLoadResult> LoadAsync(
        ModuleManifest manifest,
        string manifestPath,
        string childEntryAssemblyPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(childEntryAssemblyPath);

        List<ToolDiagnostic> diagnostics = [];
        List<ExecutableModuleDescriptor> modules = [];
        string repositoryRoot = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        if (!Path.IsPathFullyQualified(childEntryAssemblyPath) || !File.Exists(childEntryAssemblyPath))
        {
            diagnostics.Add(Diagnostic("HXD001", "runtime", "Supply a readable runner child entry assembly."));
            return Result(modules, null, diagnostics);
        }

        for (int index = 0; index < manifest.Modules.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModuleDescriptor module = manifest.Modules[index];
            string field = $"modules[{index}].descriptorAssembly";
            string? assemblyPath = ResolveAssembly(module.DescriptorAssembly, repositoryRoot, field, diagnostics);
            if (assemblyPath is null)
            {
                return Result(modules, null, diagnostics);
            }

            (int exitCode, string? json, bool unavailable) = await RunChildAsync(
                childEntryAssemblyPath, repositoryRoot, ["module", assemblyPath], cancellationToken).ConfigureAwait(false);
            if (unavailable || exitCode != 0 || json is null)
            {
                diagnostics.Add(Diagnostic(
                    unavailable ? "HXD001" : "HXD002",
                    field,
                    unavailable ? "Restore the runner child process and retry." : "Export the approved module descriptor entrypoint and JSON schema."));
                return Result(modules, null, diagnostics);
            }

            ExecutableModuleDescriptor? loaded = DescriptorDocumentValidator.ReadModule(
                json, repositoryRoot, module.Id, field, diagnostics);
            if (loaded is null)
            {
                return Result(modules, null, diagnostics);
            }

            if (loaded.UiAssembly is not null && loaded.UiMarkerType is not null
                && !await VerifyMarkerAsync(childEntryAssemblyPath, repositoryRoot, loaded.UiAssembly, loaded.UiMarkerType, field, diagnostics, cancellationToken)
                    .ConfigureAwait(false))
            {
                return Result(modules, null, diagnostics);
            }

            modules.Add(loaded);
        }

        ExecutableUiDescriptor? ui = null;
        if (manifest.Ui is not null)
        {
            const string field = "ui.descriptorAssembly";
            string? assemblyPath = ResolveAssembly(manifest.Ui.DescriptorAssembly, repositoryRoot, field, diagnostics);
            if (assemblyPath is null)
            {
                return Result(modules, null, diagnostics);
            }

            (int exitCode, string? json, bool unavailable) = await RunChildAsync(
                childEntryAssemblyPath, repositoryRoot, ["ui", assemblyPath], cancellationToken).ConfigureAwait(false);
            if (unavailable || exitCode != 0 || json is null)
            {
                diagnostics.Add(Diagnostic(
                    unavailable ? "HXD001" : "HXD002",
                    field,
                    unavailable ? "Restore the runner child process and retry." : "Export the approved UI descriptor entrypoint and JSON schema."));
                return Result(modules, null, diagnostics);
            }

            ui = DescriptorDocumentValidator.ReadUi(json, repositoryRoot, modules, field, diagnostics);
            if (ui is null)
            {
                return Result(modules, null, diagnostics);
            }
        }
        else if (modules.Any(module => module.UiAssembly is not null))
        {
            diagnostics.Add(Diagnostic("HXD005", "ui", "Declare a UI descriptor for module UI markers."));
        }

        return Result(modules, ui, diagnostics);
    }

    private static ToolDiagnostic Diagnostic(string ruleId, string field, string hint)
    {
        string message = ruleId switch
        {
            "HXD001" => "The descriptor child process is unavailable.",
            "HXD005" => "The executable UI marker binding is invalid.",
            _ => "The executable descriptor is invalid.",
        };
        return new ToolDiagnostic(
            ruleId,
            ruleId == "HXD001" ? ToolPhase.Prerequisite : ToolPhase.Manifest,
            ruleId == "HXD001" ? ToolFailureCategory.PrerequisiteUnavailable : ToolFailureCategory.Manifest,
            message,
            field,
            hint);
    }

    private static string DotnetHostPath()
    {
        string? current = Environment.ProcessPath;
        if (current is not null && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        string runtimeDirectory = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        string candidate = Path.GetFullPath(Path.Combine(runtimeDirectory, "..", "..", "..", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    private static ExecutableDescriptorLoadResult Result(
        IReadOnlyList<ExecutableModuleDescriptor> modules,
        ExecutableUiDescriptor? ui,
        IEnumerable<ToolDiagnostic> diagnostics) => new(
            modules,
            ui,
            [.. diagnostics.OrderBy(diagnostic => diagnostic.RuleId, StringComparer.Ordinal)
                .ThenBy(diagnostic => diagnostic.Field, StringComparer.Ordinal)]);

    private static string? ResolveAssembly(
        string relativePath,
        string repositoryRoot,
        string field,
        List<ToolDiagnostic> diagnostics)
    {
        if (!relativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Diagnostic("HXD003", field, "Point to a built repository-relative .dll."));
            return null;
        }

        return ManifestPathValidator.ValidateExistingFile(relativePath, repositoryRoot, field, diagnostics);
    }

    private static async Task<(int ExitCode, string? Json, bool Unavailable)> RunChildAsync(
        string childEntryAssemblyPath,
        string repositoryRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new()
        {
            FileName = DotnetHostPath(),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.Environment.Clear();
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(childEntryAssemblyPath);
        start.ArgumentList.Add("--descriptor-child");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = new() { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                return (-1, null, true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or InvalidOperationException)
        {
            return (-1, null, true);
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_childTimeout);
        Task<string?> output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        Task discardError = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            string? json = await output.ConfigureAwait(false);
            await discardError.ConfigureAwait(false);
            return (process.ExitCode, json, false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return (-1, null, true);
        }
        catch (IOException)
        {
            Kill(process);
            return (-1, null, true);
        }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        StringBuilder result = new();
        char[] buffer = new char[4096];
        while (true)
        {
            int read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return result.ToString();
            }

            if (result.Length + read > _maximumResultCharacters)
            {
                return null;
            }

            _ = result.Append(buffer, 0, read);
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The child has already exited or the OS has already disposed of it.
        }
    }

    private static async Task<bool> VerifyMarkerAsync(
        string childEntryAssemblyPath,
        string repositoryRoot,
        string assemblyPath,
        string markerType,
        string field,
        List<ToolDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        (int exitCode, _, bool unavailable) = await RunChildAsync(
            childEntryAssemblyPath, repositoryRoot, ["marker", assemblyPath, markerType], cancellationToken).ConfigureAwait(false);
        if (exitCode == 0 && !unavailable)
        {
            return true;
        }

        diagnostics.Add(Diagnostic(
            unavailable ? "HXD001" : "HXD005",
            field,
            unavailable ? "Restore the runner child process and retry." : "Return a loadable public module UI marker type."));
        return false;
    }
}
