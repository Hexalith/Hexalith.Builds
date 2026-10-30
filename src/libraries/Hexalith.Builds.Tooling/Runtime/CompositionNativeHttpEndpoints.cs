// <copyright file="CompositionNativeHttpEndpoints.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Reconciles prepared endpoint metadata with actual owning resources and allocated sidecars.</summary>
public static class CompositionNativeHttpEndpoints
{
    /// <summary>Adds only unique metadata bindings backed by observed healthy sidecar HTTP allocations.</summary>
    /// <param name="resources">The actual healthy resource observations.</param>
    /// <param name="endpoints">The owner-prepared metadata; no credentials belong here.</param>
    /// <returns>Resource observations augmented with the explicit invocation endpoints.</returns>
    /// <exception cref="InvalidDataException">An endpoint differs from its actual unique owner or sidecar allocation.</exception>
    public static IReadOnlyList<CompositionResourceReadiness> Bind(IReadOnlyList<CompositionResourceReadiness> resources, IEnumerable<CompositionNativeHttpEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(resources);
        ArgumentNullException.ThrowIfNull(endpoints);
        Dictionary<string, CompositionResourceReadiness> result = resources.ToDictionary(resource => resource.Name, StringComparer.Ordinal);
        foreach (CompositionNativeHttpEndpoint endpoint in endpoints)
        {
            if (!IsInvocationUri(endpoint.Uri) || string.IsNullOrWhiteSpace(endpoint.EndpointName)
                || !result.TryGetValue(endpoint.ResourceId, out CompositionResourceReadiness? owner) || owner.ModuleId != endpoint.ModuleId || owner.State != "Healthy"
                || !result.TryGetValue(endpoint.SidecarResourceId, out CompositionResourceReadiness? sidecar) || sidecar.ModuleId != endpoint.ModuleId || sidecar.State != "Healthy"
                || !sidecar.Endpoints.Any(actual => actual.Name == "http" && actual.Uri.Scheme == endpoint.Uri.Scheme
                    && actual.Uri.IdnHost == endpoint.Uri.IdnHost && actual.Uri.Port == endpoint.Uri.Port)
                || owner.Endpoints.Any(actual => actual.Name == endpoint.EndpointName))
            {
                throw new InvalidDataException("The native invocation endpoint lacks an exact unique owning resource and actual sidecar allocation.");
            }

            result[owner.Name] = owner with { Endpoints = [.. owner.Endpoints, new(endpoint.EndpointName, "http", endpoint.Uri)] };
        }

        return [.. resources.Select(resource => result[resource.Name])];
    }

    /// <summary>Checks a canonical owner-supplied Dapr invocation prefix without inferring its application identity.</summary>
    /// <param name="endpoint">The explicit actual URI.</param>
    /// <returns>Whether the URI is safe for an invocation-prefix-bound private credential.</returns>
    public static bool IsInvocationUri(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (!endpoint.IsAbsoluteUri)
        {
            return false;
        }

        string[] segments = endpoint.AbsolutePath.Split('/');
        return endpoint.IsLoopback && endpoint.Scheme is "http" or "https" && endpoint.UserInfo.Length == 0
            && endpoint.Query.Length == 0 && endpoint.Fragment.Length == 0 && endpoint.OriginalString == endpoint.AbsoluteUri
            && segments.Length >= 6 && segments[0].Length == 0 && segments[1] == "v1.0" && segments[2] == "invoke"
            && segments[3].Length > 0 && segments[3].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
            && segments[3] is not ("." or "..") && segments[4] == "method" && segments[^1].Length == 0
            && segments.Skip(5).SkipLast(1).All(segment => segment.Length > 0 && segment is not ("." or "..")
                && segment.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'));
    }
}
