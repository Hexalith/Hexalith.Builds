// <copyright file="CompositionRunModule.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// One validated module binding in a run plan.
/// </summary>
/// <param name="ModuleId">The manifest module identity, also used as the Aspire resource name.</param>
/// <param name="Domain">The EventStore domain served by the module.</param>
/// <param name="AppId">The Dapr application identity of the module domain service.</param>
/// <param name="ProjectPath">The absolute, descriptor-validated domain-service project path.</param>
public sealed record CompositionRunModule(string ModuleId, string Domain, string AppId, string ProjectPath);