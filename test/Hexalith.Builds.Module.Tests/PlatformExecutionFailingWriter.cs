// <copyright file="PlatformExecutionFailingWriter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

/// <summary>Simulates result-output failures at the command ownership boundary.</summary>
internal sealed class PlatformExecutionFailingWriter : StringWriter
{
    /// <inheritdoc />
    public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default) => throw new IOException("Output unavailable.");

    /// <inheritdoc />
    public override Task WriteLineAsync(string? value) => throw new IOException("Output unavailable.");
}
