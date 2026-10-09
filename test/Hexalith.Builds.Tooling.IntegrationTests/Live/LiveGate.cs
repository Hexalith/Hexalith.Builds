// <copyright file="LiveGate.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.IntegrationTests.Live;

using Hexalith.Builds.Tooling.Runtime;

using Xunit;

/// <summary>
/// Gates the live G-4 composition lane behind an explicit opt-in.
/// </summary>
internal static class LiveGate
{
    /// <summary>
    /// The opt-in environment variable.
    /// </summary>
    public const string Variable = "HEXALITH_G4_LIVE";

    /// <summary>
    /// The explicit skip reason. A skipped live test is never passing evidence.
    /// </summary>
    public static readonly string SkipReason =
        $"Live G-4 composition is opt-in: set HEXALITH_G4_LIVE=1 and HEXALITH_DAPR_HOME to a verified Dapr CLI {CompositionToolchainPins.DaprCliVersion} / runtime {CompositionToolchainPins.DaprRuntimeVersion} home with Docker available, and install an Aspire CLI {CompositionToolchainPins.AspireAppHostSdkVersion}. A skipped live test is not passing evidence.";

    /// <summary>
    /// Gets a value indicating whether the live lane is enabled.
    /// </summary>
    public static bool IsEnabled => string.Equals(Environment.GetEnvironmentVariable(Variable), "1", StringComparison.Ordinal);

    /// <summary>
    /// Skips the current test with an explicit reason unless the live lane is enabled.
    /// </summary>
    public static void SkipUnlessEnabled()
    {
        if (!IsEnabled)
        {
            Assert.Skip(SkipReason);
        }
    }
}