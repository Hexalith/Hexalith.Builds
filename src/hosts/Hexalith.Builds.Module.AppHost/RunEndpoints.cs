// <copyright file="RunEndpoints.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Resolves allocated HTTP endpoints of run resources.
/// </summary>
internal static class RunEndpoints
{
    /// <summary>
    /// Gets the allocated HTTP endpoint of a resource, or null when it is not allocated yet.
    /// </summary>
    /// <param name="app">The distributed application.</param>
    /// <param name="name">The resource name.</param>
    /// <returns>The endpoint, or null.</returns>
    public static Uri? TryGet(DistributedApplication app, string name)
    {
        ArgumentNullException.ThrowIfNull(app);
        DistributedApplicationModel model = app.Services.GetRequiredService<DistributedApplicationModel>();
        IResourceWithEndpoints? resource = model.Resources.OfType<IResourceWithEndpoints>()
            .SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        EndpointReference? endpoint = resource?.GetEndpoints()
            .FirstOrDefault(candidate => string.Equals(candidate.EndpointName, "http", StringComparison.Ordinal));
        return endpoint is { IsAllocated: true } ? new Uri(endpoint.Url) : null;
    }

    /// <summary>
    /// Waits until a resource HTTP endpoint is allocated.
    /// </summary>
    /// <param name="app">The distributed application.</param>
    /// <param name="name">The resource name.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The allocated endpoint.</returns>
    public static async Task<Uri> WaitForAsync(DistributedApplication app, string name, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (TryGet(app, name) is Uri endpoint)
            {
                return endpoint;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }
}