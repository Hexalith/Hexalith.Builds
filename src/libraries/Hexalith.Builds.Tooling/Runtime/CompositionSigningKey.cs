// <copyright file="CompositionSigningKey.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Security.Cryptography;

/// <summary>
/// Generates the per-run development signing key.
/// </summary>
/// <remarks>
/// The key is never written to a plan, state, readiness, evidence, or log document. It is passed only
/// through the AppHost child-process environment and held in memory by the run session.
/// </remarks>
public static class CompositionSigningKey
{
    /// <summary>
    /// Creates a fresh 384-bit key encoded as Base64 text.
    /// </summary>
    /// <returns>The signing key text.</returns>
    public static string Create() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
}