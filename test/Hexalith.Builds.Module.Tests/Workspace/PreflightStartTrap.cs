// <copyright file="PreflightStartTrap.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Microsoft.Extensions.Hosting;

/// <summary>Fails immediately if validation reaches application startup.</summary>
internal sealed class PreflightStartTrap : IHostedService
{
    /// <summary>Gets the number of attempted starts.</summary>
    internal int Starts { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Starts++;
        throw new InvalidOperationException("Validation must return before starting services.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
