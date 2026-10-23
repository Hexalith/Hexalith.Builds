// <copyright file="CompositionResourceClaim.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>A durable exclusive resource claim owned by one run.</summary>
/// <param name="ResourceId">The actual exclusive resource identity.</param>
/// <param name="OwnerRunId">The blocking runner identity.</param>
public sealed record CompositionResourceClaim(string ResourceId, string OwnerRunId);
