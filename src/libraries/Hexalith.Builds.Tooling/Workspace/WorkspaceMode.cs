// <copyright file="WorkspaceMode.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Text.Json.Serialization;

/// <summary>The tool-selected dependency mode for one invocation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<WorkspaceMode>))]
public enum WorkspaceMode
{
    /// <summary>Build mapped sources in Debug.</summary>
    [JsonStringEnumMemberName("source")]
    Source = 0,

    /// <summary>Build the active module in Release using packages.</summary>
    [JsonStringEnumMemberName("package")]
    Package = 1,
}
