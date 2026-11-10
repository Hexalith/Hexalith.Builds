// <copyright file="AttachedSuiteRequest.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;

/// <summary>The hold options of a <c>test --attach-to</c> request; the owner run identity is the command's run identity.</summary>
public sealed record AttachedSuiteRequest
{
    /// <summary>Gets the raw hold request, or null when the request omitted one.</summary>
    public string? HoldSeconds { get; init; }

    /// <summary>Gets a runner-issued original request or attached run identity being retried or reconnected.</summary>
    public string? PresentedRequestId { get; init; }

    /// <summary>Gets the attaching process's execution context; null reads the actual process environment.</summary>
    public JsonElement? AttacherContext { get; init; }
}
