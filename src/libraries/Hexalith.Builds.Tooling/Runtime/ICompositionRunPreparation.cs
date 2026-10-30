// <copyright file="ICompositionRunPreparation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Owner preparation required after writer activation and before publishing native-test readiness.</summary>
public interface ICompositionRunPreparation
{
    /// <summary>Gets the owner-derived native scope after successful preparation, or null when this composition supplies none.</summary>
    CompositionNativeTestScope? NativeTestScope => null;

    /// <summary>Gets metadata-only invocation endpoints after successful actual preparation.</summary>
    IReadOnlyList<CompositionNativeHttpEndpoint> NativeHttpEndpoints => [];

    /// <summary>Gets the fixed owned private credential file path after successful preparation; values are never readiness metadata.</summary>
    string? NativeEndpointCredentialsPath => null;

    /// <summary>Prepares only the resources and private identity owned by this exact run.</summary>
    /// <param name="services">The actual started composition services.</param>
    /// <param name="cancellationToken">The original startup deadline and run-stop bound.</param>
    /// <returns>A task completed only after the actual preparation is verified.</returns>
    Task PrepareAsync(IServiceProvider services, CancellationToken cancellationToken);
}
