// <copyright file="NativeBuildFileStatus.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

/// <summary>Reads file type and hard-link count from the held Linux file descriptor.</summary>
[StructLayout(LayoutKind.Explicit, Size = 256)]
internal readonly struct NativeBuildFileStatus
{
    [FieldOffset(0)]
    private readonly uint _mask;

    [FieldOffset(16)]
    private readonly uint _links;

    [FieldOffset(28)]
    private readonly ushort _mode;

    /// <summary>Checks the actual file held by the nonblocking open.</summary>
    /// <param name="descriptor">The held file descriptor.</param>
    /// <returns>Whether the held artifact is a single-link regular file.</returns>
    internal static bool IsRegular(SafeFileHandle descriptor)
    {
        const uint typeAndLinks = 0x1 | 0x4;
        const int emptyPath = 0x1000;
        return !OperatingSystem.IsLinux() || (Statx(descriptor, string.Empty, emptyPath, typeAndLinks, out NativeBuildFileStatus status) == 0
            && (status._mask & typeAndLinks) == typeAndLinks && status._links == 1 && (status._mode & 0xf000) == 0x8000);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1054:Use LibraryImportAttribute instead of DllImportAttribute", Justification = "The held descriptor and fixed Linux ABI do not require generated unsafe code.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2101:Specify marshaling for P/Invoke string arguments", Justification = "The statx call passes only the empty UTF-8 path.")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    [DllImport("libc", EntryPoint = "statx", BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int Statx(SafeFileHandle descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out NativeBuildFileStatus status);
}
