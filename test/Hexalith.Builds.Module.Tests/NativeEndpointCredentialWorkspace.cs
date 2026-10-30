// <copyright file="NativeEndpointCredentialWorkspace.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests;

/// <summary>Owns temporary contract files without inferring a repository from the test assembly location.</summary>
internal sealed class NativeEndpointCredentialWorkspace : IDisposable
{
    /// <summary>Gets the uniquely owned local directory.</summary>
    internal string Root { get; } = CompositionTestFiles.CreateDirectory();

    /// <inheritdoc/>
    public void Dispose() => CompositionTestFiles.Delete(Root);
}
