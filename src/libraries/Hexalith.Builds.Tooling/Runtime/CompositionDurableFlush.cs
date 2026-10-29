// <copyright file="CompositionDurableFlush.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

/// <summary>Verifies that publication bytes reach storage before their temporary file can be renamed.</summary>
internal static class CompositionDurableFlush
{
    /// <summary>Flushes managed buffers and checks Linux fsync failures within the original publication allowance.</summary>
    /// <param name="stream">The held temporary publication file.</param>
    /// <param name="cancellationToken">The existing publication allowance.</param>
    /// <exception cref="IOException">Storage could not confirm the durable flush.</exception>
    internal static void Flush(FileStream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux())
        {
            stream.Flush(flushToDisk: true);
            return;
        }

        // Check libc directly: the Linux runtime PAL can return a positive value for a failed fsync.
        stream.Flush();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FSync(stream.SafeFileHandle) == 0)
            {
                return;
            }

            if (Marshal.GetLastPInvokeError() != 4)
            {
                throw new IOException("Publication storage could not confirm a durable flush.");
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "The checked Linux fsync uses the existing held safe handle without generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(SafeFileHandle descriptor);
}