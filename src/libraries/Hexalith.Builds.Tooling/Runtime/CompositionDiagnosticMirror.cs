// <copyright file="CompositionDiagnosticMirror.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;

/// <summary>
/// Retains only the stream on which AppHost output was observed.
/// </summary>
public static class CompositionDiagnosticMirror
{
    /// <summary>
    /// Attaches metadata-only output observers to a process before it starts.
    /// </summary>
    /// <param name="process">The process whose redirected streams are observed.</param>
    /// <param name="logPath">The optional diagnostic log path.</param>
    /// <param name="preservedDirectory">The optional directory outside the run workspace that keeps the same tokens after teardown.</param>
    public static void Attach(Process process, string? logPath, string? preservedDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(logPath) && string.IsNullOrWhiteSpace(preservedDirectory))
        {
            return;
        }

        string? path = string.IsNullOrWhiteSpace(logPath) ? null : Path.GetFullPath(logPath);
        string? preserved = string.IsNullOrWhiteSpace(preservedDirectory)
            ? null
            : Path.Combine(Path.GetFullPath(preservedDirectory), "observed.txt");
        if (path is not null)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        if (preserved is not null)
        {
            _ = Directory.CreateDirectory(Path.GetDirectoryName(preserved)!);
        }

        object gate = new();

        void Record(string stream, DataReceivedEventArgs args)
        {
            if (args.Data is null)
            {
                return;
            }

            lock (gate)
            {
                try
                {
                    string token = "apphost." + stream + ".observed" + Environment.NewLine;
                    if (path is not null)
                    {
                        File.AppendAllText(path, token);
                    }

                    if (preserved is not null)
                    {
                        File.AppendAllText(preserved, token);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Optional diagnostic retention cannot affect lifecycle control.
                }
            }
        }

        process.OutputDataReceived += (_, args) => Record("stdout", args);
        process.ErrorDataReceived += (_, args) => Record("stderr", args);
    }
}
