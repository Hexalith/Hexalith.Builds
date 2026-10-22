// <copyright file="RecordValidationException.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Reports a fixed metadata-only validation failure without echoing record contents.
/// </summary>
public sealed class RecordValidationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="RecordValidationException"/> class.</summary>
    public RecordValidationException()
        : this("invalid-record")
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RecordValidationException"/> class.</summary>
    /// <param name="code">The stable failure code.</param>
    public RecordValidationException(string code)
        : base($"Release record rejected ({code}).") => Code = code;

    /// <summary>Initializes a new instance of the <see cref="RecordValidationException"/> class.</summary>
    /// <param name="code">The stable failure code.</param>
    /// <param name="innerException">The underlying exception.</param>
    public RecordValidationException(string code, Exception innerException)
        : base($"Release record rejected ({code}).", innerException) => Code = code;

    /// <summary>Gets the stable failure code.</summary>
    public string Code { get; }
}