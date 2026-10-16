// <copyright file="CompositionStartupClock.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using Hexalith.Builds.Tooling.Manifest;

/// <summary>
/// Measures the one startup deadline from the start request.
/// </summary>
public static class CompositionStartupClock
{
    /// <summary>
    /// Gets the time remaining until an absolute deadline.
    /// </summary>
    /// <param name="deadlineUtc">The absolute deadline, or null for the schema default of 600 seconds.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The remaining time, or zero when the deadline has passed.</returns>
    public static TimeSpan Remaining(DateTimeOffset? deadlineUtc, DateTimeOffset now)
    {
        if (deadlineUtc is null)
        {
            return TimeSpan.FromSeconds(SupportedPlatformManifestSchemas.DefaultStartupTimeoutSeconds);
        }

        TimeSpan remaining = deadlineUtc.Value - now;
        return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
    }
}
