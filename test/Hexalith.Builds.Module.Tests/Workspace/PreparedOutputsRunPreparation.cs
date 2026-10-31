// <copyright file="PreparedOutputsRunPreparation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Reports fixed owner preparation outputs so readiness publication can be observed without live services.</summary>
/// <param name="scope">The owner-derived native scope.</param>
/// <param name="endpoints">The metadata-only invocation endpoints.</param>
/// <param name="credentialsPath">The private endpoint credential channel path, or null when none is prepared.</param>
internal sealed class PreparedOutputsRunPreparation(CompositionNativeTestScope? scope, IReadOnlyList<CompositionNativeHttpEndpoint> endpoints, string? credentialsPath) : ICompositionRunPreparation
{
    /// <inheritdoc/>
    public CompositionNativeTestScope? NativeTestScope => scope;

    /// <inheritdoc/>
    public IReadOnlyList<CompositionNativeHttpEndpoint> NativeHttpEndpoints => endpoints;

    /// <inheritdoc/>
    public string? NativeEndpointCredentialsPath => credentialsPath;

    /// <inheritdoc/>
    public Task PrepareAsync(IServiceProvider services, CancellationToken cancellationToken) => Task.CompletedTask;
}
