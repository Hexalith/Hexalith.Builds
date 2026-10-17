// <copyright file="IncompleteTestObservationProvider.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Hexalith.Builds.Tooling.Runtime;

/// <summary>Returns missing observations for the handler's fail-closed regression.</summary>
/// <param name="runId">The owning run.</param>
/// <param name="moduleId">The selected declaration owner.</param>
internal sealed class IncompleteTestObservationProvider(string runId, string moduleId) : ICompositionTestObservationProvider
{
    /// <inheritdoc/>
    public Task<CompositionTestObservation> ObserveAsync(IServiceProvider services, CancellationToken cancellationToken) =>
        Task.FromResult(new CompositionTestObservation { RunId = runId, ModuleId = moduleId, ProfileHash = string.Empty, ProfileJson = string.Empty });
}
