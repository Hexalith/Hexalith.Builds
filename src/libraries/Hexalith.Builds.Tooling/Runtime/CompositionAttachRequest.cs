// <copyright file="CompositionAttachRequest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

/// <summary>An explicit request to attach a run to a runner-owned environment.</summary>
/// <param name="OwnerRunId">The owner run that identifies the environment.</param>
/// <param name="Mode">The artifact mode the attaching run expects: <c>source</c> or <c>package</c>.</param>
/// <param name="CompositionIdentity">The composition (declaration) content identity the attaching run expects.</param>
public sealed record CompositionAttachRequest(string OwnerRunId, string Mode, string CompositionIdentity)
{
    /// <summary>Gets the raw hold request, or null when the request omitted one.</summary>
    public string? HoldSeconds { get; init; }

    /// <summary>Gets a runner-issued <c>originalAttachmentRequestId</c> or attached run identity being retried.</summary>
    public string? PresentedRequestId { get; init; }

    /// <summary>Gets what the request points at: <c>run-owned</c>, <c>hosted</c> or <c>developer-started</c>.</summary>
    public string Target { get; init; } = "run-owned";

    /// <summary>Gets the optional expected environment identity.</summary>
    public string? EnvironmentId { get; init; }

    /// <summary>Gets expected artifact content identities, which this runner version cannot yet verify.</summary>
    public IReadOnlyList<string> ArtifactIdentities { get; init; } = [];

    /// <summary>Gets the attaching process's execution context; null reads the actual process environment.</summary>
    public JsonElement? AttacherContext { get; init; }
}
