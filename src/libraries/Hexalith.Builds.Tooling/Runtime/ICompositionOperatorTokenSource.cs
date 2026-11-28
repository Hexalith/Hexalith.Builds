// <copyright file="ICompositionOperatorTokenSource.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Supplies the operator bearer for the runner-owned writer-protocol cutover when the composition model owns the
/// run's token issuer.
/// </summary>
/// <remarks>
/// A model registers one implementation in the AppHost services. Without one, the cutover keeps minting its
/// in-memory token from the per-run signing key. The runner never receives the source's credentials.
/// </remarks>
public interface ICompositionOperatorTokenSource
{
    /// <summary>Gets the support-safe operator principal name reported when no token can be obtained.</summary>
    string OperatorName { get; }

    /// <summary>Obtains a short-lived bearer whose only grant is the global-administrator role.</summary>
    /// <param name="cancellationToken">The remaining cutover bound.</param>
    /// <returns>The compact bearer token.</returns>
    Task<string> CreateGlobalAdministratorTokenAsync(CancellationToken cancellationToken);
}
