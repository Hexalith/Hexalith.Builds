// <copyright file="WorkspaceMappingException.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>A fail-closed workspace resolution failure.</summary>
public sealed class WorkspaceMappingException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="WorkspaceMappingException"/> class.</summary>
    public WorkspaceMappingException()
        : this(DefaultDiagnostic("Workspace mapping failed."))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceMappingException"/> class.</summary>
    /// <param name="message">The reason.</param>
    public WorkspaceMappingException(string message)
        : this(DefaultDiagnostic(message))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="WorkspaceMappingException"/> class.</summary>
    /// <param name="message">The reason.</param>
    /// <param name="innerException">The cause.</param>
    public WorkspaceMappingException(string message, Exception innerException)
        : base(message, innerException)
        => Diagnostic = DefaultDiagnostic(message);

    /// <summary>Initializes a new instance of the <see cref="WorkspaceMappingException"/> class.</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    public WorkspaceMappingException(ToolDiagnostic diagnostic)
        : base((diagnostic ?? throw new ArgumentNullException(nameof(diagnostic))).Message)
        => Diagnostic = diagnostic;

    /// <summary>Gets the workspace diagnostic.</summary>
    public ToolDiagnostic Diagnostic { get; }

    private static ToolDiagnostic DefaultDiagnostic(string message) => new(
        "HXW000",
        ToolPhase.Topology,
        ToolFailureCategory.TopologyOrLifecycle,
        message,
        "workspace");
}
