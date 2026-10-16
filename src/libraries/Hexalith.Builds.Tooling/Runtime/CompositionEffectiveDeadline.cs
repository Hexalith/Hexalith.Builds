// <copyright file="CompositionEffectiveDeadline.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// The one finite startup deadline for a complete environment.
/// </summary>
/// <param name="TimeoutSeconds">The deadline length in seconds, measured from the start request.</param>
/// <param name="IsOverride">True when a module declared the deadline; false for the default.</param>
/// <param name="Justification">The declaration justification, when <paramref name="IsOverride"/> is true.</param>
/// <param name="ModuleId">The module that declared the selected override.</param>
public sealed record CompositionEffectiveDeadline(
    double TimeoutSeconds,
    bool IsOverride,
    string? Justification,
    string? ModuleId)
{
    /// <summary>Gets the deadline as a finite time span.</summary>
    public TimeSpan Timeout => TimeSpan.FromSeconds(TimeoutSeconds);
}
