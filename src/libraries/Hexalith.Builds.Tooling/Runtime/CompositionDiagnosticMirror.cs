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
    public static void Attach(Process process, string? logPath)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        string path = Path.GetFullPath(logPath);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
                    File.AppendAllText(path, "apphost." + stream + ".observed" + Environment.NewLine);
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
