// <copyright file="DaprDirectEndpointSubscriber.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleHosts.AppHost;

using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;

/// <summary>
/// Makes the ports assigned to Dapr CLI resources the ports on which daprd actually listens.
/// </summary>
internal sealed class DaprDirectEndpointSubscriber : IDistributedApplicationEventingSubscriber
{
    /// <inheritdoc/>
    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        _ = eventing.Subscribe<BeforeStartEvent>((startup, _) =>
        {
            ExecutableResource[] sidecars = [.. startup.Model.Resources
                .OfType<ExecutableResource>()
                .Where(resource => resource.Name.EndsWith("-dapr-cli", StringComparison.Ordinal))];
            if (sidecars.Length == 0)
            {
                throw new InvalidOperationException("Dapr CLI resources were not created before endpoint configuration.");
            }

            foreach (ExecutableResource sidecar in sidecars)
            {
                foreach (EndpointAnnotation endpoint in sidecar.Annotations.OfType<EndpointAnnotation>().Where(endpoint => endpoint.Name is "http" or "grpc" or "metrics"))
                {
                    endpoint.TargetPort = endpoint.Port;
                    endpoint.IsProxied = false;
                }
            }

            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }
}
