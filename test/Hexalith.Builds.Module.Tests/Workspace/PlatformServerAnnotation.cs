// <copyright file="PlatformServerAnnotation.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.ModuleTool.Tests.Workspace;

using Aspire.Hosting.ApplicationModel;

/// <summary>Models the name-based Platform contract without an enclosing Platform checkout.</summary>
/// <param name="Binding">The binding exposing the declared readiness endpoint.</param>
internal sealed record PlatformServerAnnotation(object Binding) : IResourceAnnotation;
