// <copyright file="CompositionProcessStartFailureKind.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Identifies the finite failure classes caught while starting a helper.</summary>
public enum CompositionProcessStartFailureKind
{
    /// <summary>The process start operation returned false.</summary>
    ReturnedFalse = 0,

    /// <summary>The native process launch failed.</summary>
    Win32 = 1,

    /// <summary>The process start operation encountered an input/output failure.</summary>
    InputOutput = 2,

    /// <summary>The process start operation was invalid.</summary>
    InvalidOperation = 3,

    /// <summary>The platform does not support the process start operation.</summary>
    PlatformNotSupported = 4,
}