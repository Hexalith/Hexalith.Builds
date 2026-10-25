// <copyright file="CompositionProcessEnvironmentReader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

using Microsoft.Win32.SafeHandles;

/// <summary>Reads exact process environment entries on supported non-Linux hosts without retaining their contents.</summary>
internal static class CompositionProcessEnvironmentReader
{
    private const int _maximumBytes = 1024 * 1024;

    /// <summary>Discovers owned processes with bounded environment reads and explicit observation uncertainty.</summary>
    /// <param name="runId">The exact runner-issued owner tag.</param>
    /// <param name="cancellationToken">The existing scan allowance.</param>
    /// <returns>Verified matching processes and whether discovery was complete.</returns>
    internal static (IReadOnlyList<int> ProcessIds, bool Verified) Find(string runId, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return ([], false);
        }

        bool verified = true;
        List<int> matches = [];
        string expected = CompositionEnvironment.RunId + "=" + runId;
        HashSet<int> foreignWindowsProcesses = OperatingSystem.IsWindows() ? ForeignWindowsProcesses() : [];
        Process[] processes = Process.GetProcesses();
        try
        {
            foreach (int processId in processes.Select(process => process.Id))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (processId == Environment.ProcessId || processId <= 4)
                {
                    continue;
                }

                try
                {
                    byte[]? environment = null;
                    if (OperatingSystem.IsWindows())
                    {
                        environment = ReadWindows(processId, foreignWindowsProcesses);
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        environment = ReadMacOS(processId);
                    }

                    if (environment is not null && ContainsEntry(environment, expected, OperatingSystem.IsWindows()))
                    {
                        matches.Add(processId);
                    }
                }
                catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // An accessible process whose environment could not be inspected makes absence unverified.
                    verified = false;
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return ([.. matches.Order()], verified);
    }

    /// <summary>Matches a complete environment entry, preserving the owner boundary.</summary>
    /// <param name="environment">The native environment block.</param>
    /// <param name="entry">The exact expected entry.</param>
    /// <param name="unicode">Whether the native block uses UTF-16.</param>
    /// <returns>Whether an entire entry matches.</returns>
    internal static bool ContainsEntry(byte[] environment, string entry, bool unicode) => (unicode ? Encoding.Unicode : Encoding.UTF8).GetString(environment).Split('\0').Contains(entry, StringComparer.Ordinal);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[]? ReadWindows(int processId, HashSet<int> foreign)
    {
        using SafeProcessHandle process = OpenProcess(0x410, false, processId);
        if (process.IsInvalid)
        {
            return CanExcludeWindowsProcess(processId, foreign) ? null : throw new IOException("Live process ownership cannot be inspected.");
        }

        try
        {
            byte[] basic = new byte[nint.Size * 6];
            if (NtQueryInformationProcess(process, 0, basic, basic.Length, out _) != 0)
            {
                throw new IOException("Process ownership discovery is unavailable.");
            }

            bool target32 = nint.Size == 4;
            nint peb = Pointer(basic, nint.Size, target32);
            if (nint.Size == 8)
            {
                byte[] wow64 = new byte[8];
                if (NtQueryInformationProcess(process, 26, wow64, wow64.Length, out _) != 0)
                {
                    throw new IOException("Process ownership discovery is unavailable.");
                }

                nint wow64Peb = Pointer(wow64, 0, false);
                if (wow64Peb != 0)
                {
                    target32 = true;
                    peb = wow64Peb;
                }
            }

            int pointerSize = target32 ? 4 : 8;
            nint parameters = Pointer(ReadMemory(process, peb + (target32 ? 0x10 : 0x20), pointerSize), 0, target32);
            if (parameters == 0)
            {
                return [];
            }

            // The native RTL process parameters keep the environment pointer after the two UNICODE_STRING paths.
            nint address = Pointer(ReadMemory(process, parameters + (target32 ? 0x48 : 0x80), pointerSize), 0, target32);
            if (address == 0)
            {
                return [];
            }

            List<byte> bytes = [];
            while (bytes.Count < _maximumBytes)
            {
                int count = Math.Min(4096 - (int)((long)address & 4095), _maximumBytes - bytes.Count);
                byte[] page = ReadMemory(process, address, count);
                bytes.AddRange(page);
                for (int index = Math.Max(0, bytes.Count - page.Length - 2); index + 3 < bytes.Count; index += 2)
                {
                    if (bytes[index] == 0 && bytes[index + 1] == 0 && bytes[index + 2] == 0 && bytes[index + 3] == 0)
                    {
                        return [.. bytes.Take(index + 2)];
                    }
                }

                address += count;
            }

            throw new IOException("Process ownership observation exceeds its bound.");
        }
        catch (IOException) when (CanExcludeWindowsProcess(processId, foreign))
        {
            return null;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static byte[] ReadMemory(SafeProcessHandle process, nint address, int count)
    {
        byte[] bytes = new byte[count];
        return ReadProcessMemory(process, address, bytes, count, out nint read) && read == count
            ? bytes
            : throw new IOException("Process ownership memory could not be observed.");
    }

    private static bool Exited(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException)
        {
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool CanExcludeWindowsProcess(int processId, HashSet<int> foreign) => foreign.Contains(processId) || Exited(processId);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static HashSet<int> ForeignWindowsProcesses()
    {
        HashSet<int> foreign = [];
        using WindowsIdentity current = WindowsIdentity.GetCurrent();
        if (current.User is not { } owner || !WtsEnumerateProcesses(0, 0, 1, out nint buffer, out int count))
        {
            return foreign;
        }

        try
        {
            if (count is < 0 or > 65536)
            {
                return foreign;
            }

            int stride = 8 + (2 * nint.Size);
            for (int index = 0; index < count; index++)
            {
                nint entry = buffer + (index * stride);
                nint sid = Marshal.ReadIntPtr(entry, 8 + nint.Size);
                if (sid != 0 && new SecurityIdentifier(sid) != owner)
                {
                    _ = foreign.Add(Marshal.ReadInt32(entry, 4));
                }
            }
        }
        finally
        {
            WtsFreeMemory(buffer);
        }

        return foreign;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static bool CanExcludeMacOSProcess(int processId)
    {
        if (Exited(processId))
        {
            return true;
        }

        byte[] info = new byte[136];
        if (ProcPidInfo(processId, 3, 0, info, info.Length) < 44)
        {
            return false;
        }

        uint uid = GetUserId();
        uint effective = GetEffectiveUserId();
        return new[] { BitConverter.ToUInt32(info, 20), BitConverter.ToUInt32(info, 28), BitConverter.ToUInt32(info, 36) }
            .All(owner => owner != uid && owner != effective);
    }

    private static nint Pointer(byte[] bytes, int offset, bool target32) => target32 ? (nint)BitConverter.ToUInt32(bytes, offset) : (nint)BitConverter.ToInt64(bytes, offset);

    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static byte[]? ReadMacOS(int processId)
    {
        byte[] bytes = new byte[_maximumBytes];
        nuint length = (nuint)bytes.Length;
        if (Sysctl([1, 49, processId], 3, bytes, ref length, 0, 0) != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            return error == 3 || CanExcludeMacOSProcess(processId) ? null : throw new IOException("Live process ownership cannot be inspected.");
        }

        int end = checked((int)length);
        if (end < 5)
        {
            throw new IOException("Process ownership observation is incomplete.");
        }

        int arguments = BitConverter.ToInt32(bytes, 0);
        int cursor = 4;
        while (cursor < end && bytes[cursor] != 0)
        {
            cursor++;
        }

        while (cursor < end && bytes[cursor] == 0)
        {
            cursor++;
        }

        // KERN_PROCARGS2 is argc, executable path, padding, argc argument strings, then exact environment entries.
        for (int argument = 0; argument < arguments && cursor < end; argument++)
        {
            while (cursor < end && bytes[cursor++] != 0)
            {
                // Skip this argument without interpreting its contents as environment entries.
            }
        }

        return arguments >= 0 && cursor < end ? bytes[cursor..end] : throw new IOException("Process ownership observation is incomplete.");
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native process inspection does not require enabling generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(int access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native process inspection does not require enabling generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, [Out] byte[] information, int length, out int returned);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native process inspection does not require enabling generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(SafeProcessHandle process, nint address, [Out] byte[] bytes, nint count, out nint read);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native process inspection does not require enabling generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "sysctl", SetLastError = true)]
    private static extern int Sysctl(int[] name, uint nameLength, [Out] byte[] output, ref nuint outputLength, nint input, nuint inputLength);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native ownership queries do not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libproc", EntryPoint = "proc_pidinfo", SetLastError = true)]
    private static extern int ProcPidInfo(int processId, int flavor, ulong argument, [Out] byte[] buffer, int size);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native ownership queries do not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "getuid", SetLastError = true)]
    private static extern uint GetUserId();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native ownership queries do not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
    private static extern uint GetEffectiveUserId();

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native ownership queries do not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", EntryPoint = "WTSEnumerateProcessesW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WtsEnumerateProcesses(nint server, int reserved, int version, out nint processes, out int count);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "Bounded native ownership queries do not require generated unsafe code.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("wtsapi32.dll", EntryPoint = "WTSFreeMemory")]
    private static extern void WtsFreeMemory(nint buffer);
}
