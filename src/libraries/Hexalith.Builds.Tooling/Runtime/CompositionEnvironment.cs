// <copyright file="CompositionEnvironment.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>
/// Names shared by the runner and the Builds-owned AppHost.
/// </summary>
public static class CompositionEnvironment
{
    /// <summary>The AppHost environment variable naming the run-plan file.</summary>
    public const string PlanPath = "HEXALITH_G4_PLAN";

    /// <summary>The AppHost environment variable carrying the per-run signing key.</summary>
    public const string SigningKey = "HEXALITH_G4_SIGNING_KEY";

    /// <summary>The environment variable that tags every run process with its run identity.</summary>
    public const string RunId = "HEXALITH_G4_RUN_ID";

    /// <summary>The Dapr CLI environment variable selecting the verified runtime directory.</summary>
    public const string DaprRuntimePath = "DAPR_RUNTIME_PATH";

    /// <summary>The container label key that tags every run container with its run identity.</summary>
    public const string ContainerRunLabel = "hexalith.g4.run";

    /// <summary>The token issuer used for per-run development identities.</summary>
    public const string TokenIssuer = "hexalith-g4-runner";

    /// <summary>The token audience accepted by the EventStore host.</summary>
    public const string TokenAudience = "hexalith-eventstore";

    /// <summary>The AppHost environment variable that mirrors resource logs into the diagnostic AppHost log.</summary>
    public const string ResourceLogs = "HEXALITH_G4_RESOURCE_LOGS";

    /// <summary>The AppHost environment variable that keeps a public run alive after stdin closes.</summary>
    public const string PersistAfterParentExit = "HEXALITH_G4_PERSIST_AFTER_PARENT_EXIT";

    /// <summary>The AppHost stop command read from standard input.</summary>
    public const string StopCommand = "stop";
}
