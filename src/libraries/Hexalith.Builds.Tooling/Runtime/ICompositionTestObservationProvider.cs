// <copyright file="ICompositionTestObservationProvider.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The selected composition's source of actual, scoped test access and observed identities.</summary>
public interface ICompositionTestObservationProvider
{
    /// <summary>Observes the started resources; declared identities alone never satisfy this boundary.</summary>
    /// <param name="services">The running composition services.</param>
    /// <param name="cancellationToken">The existing startup bound.</param>
    /// <returns>Actual measurements, with absent fields remaining unavailable.</returns>
    Task<CompositionTestObservation> ObserveAsync(IServiceProvider services, CancellationToken cancellationToken);
}
