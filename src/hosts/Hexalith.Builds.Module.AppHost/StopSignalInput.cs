// <copyright file="StopSignalInput.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

/// <summary>Opens the input owned by one stop listener.</summary>
internal static class StopSignalInput
{
    /// <summary>Opens the process standard input for the stop protocol.</summary>
    /// <returns>The stream owned by the listener.</returns>
    /// <exception cref="IOException">The process standard input could not be opened.</exception>
    internal static Stream Open()
    {
        if (!OperatingSystem.IsLinux() || !Console.IsInputRedirected)
        {
            return Console.OpenStandardInput();
        }

        // Reopen only our stdin, nonblocking and close-on-exec. An independent open-file
        // description preserves fd 0's flags and avoids duplicate-pipe async registrations.
        const int nonBlocking = 2048;
        const int closeOnExec = 524288;
        int descriptor = OpenStandardInput("/proc/self/fd/0", nonBlocking | closeOnExec);
        if (descriptor < 0)
        {
            throw new IOException("Could not open the owned stop input.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }

        SafePipeHandle? handle = new(descriptor, ownsHandle: true);
        try
        {
            // UnixConsoleStream uses a blocking read underneath ReadAsync. PipeStream supports
            // cancellation of an actual pending pipe read while its writer remains open.
            AnonymousPipeClientStream input = new(PipeDirection.In, handle);
            handle = null; // Ownership transferred to the returned pipe stream.
            return input;
        }
        catch (IOException)
        {
            // Redirected stdin can also be a regular file or /dev/null, which is not a pipe.
            return Console.OpenStandardInput();
        }
        finally
        {
            handle?.Dispose();
        }
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Opening the single owned stdin descriptor does not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "The fixed procfs path is explicitly marshaled as UTF-8 for Unix open.")]
    [DllImport("libc", EntryPoint = "open", SetLastError = true, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int OpenStandardInput([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
}
