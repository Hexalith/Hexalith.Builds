// <copyright file="RetainedDiagnosticWriter.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using System.Text;

/// <summary>Decouples retained children from parent pipes while retaining only output observation metadata.</summary>
/// <param name="sink">The process-owned metadata sink.</param>
internal sealed class RetainedDiagnosticWriter(TextWriter sink) : TextWriter
{
    /// <summary>The maximum retained observation count per AppHost process.</summary>
    public const int MaximumObservations = 128;

    private int _observations;

    /// <inheritdoc />
    public override Encoding Encoding => Encoding.UTF8;

    /// <inheritdoc />
    public override void Write(char value) => Observe();

    /// <inheritdoc />
    public override void Write(string? value) => Observe();

    /// <inheritdoc />
    public override void WriteLine(string? value) => Observe();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            sink.Dispose();
        }

        base.Dispose(disposing);
    }

    private void Observe()
    {
        if (Volatile.Read(ref _observations) < MaximumObservations && Interlocked.Increment(ref _observations) <= MaximumObservations)
        {
            sink.WriteLine("apphost.output.observed");
        }
    }
}
