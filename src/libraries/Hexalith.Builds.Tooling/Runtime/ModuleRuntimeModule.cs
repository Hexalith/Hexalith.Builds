// <copyright file="ModuleRuntimeModule.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Captures one module's deterministic identity in a run-scoped topology plan.
/// </summary>
/// <param name="Id">The module identity.</param>
/// <param name="Domain">The deterministic domain identity.</param>
/// <param name="ApplicationId">The deterministic application identity.</param>
/// <param name="ResourceId">The deterministic resource identity.</param>
public sealed record ModuleRuntimeModule(string Id, string Domain, string ApplicationId, string ResourceId);