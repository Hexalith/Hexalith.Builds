// <copyright file="NativeBuildHandoff.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>Names the producer receipt and an independently selected exact SHA-256 digest.</summary>
/// <param name="ReceiptPath">The receipt path.</param>
/// <param name="Sha256">The expected lowercase SHA-256 digest.</param>
public sealed record NativeBuildHandoff(string ReceiptPath, string Sha256);
