// <copyright file="CompositionRunSession.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Diagnostics;

/// <summary>
/// An in-memory handle to one ready run. It is the only holder of the per-run signing key.
/// </summary>
public sealed class CompositionRunSession
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CompositionRunSession"/> class.
    /// </summary>
    /// <param name="plan">The run plan.</param>
    /// <param name="readiness">The validated readiness document.</param>
    /// <param name="signingKey">The per-run signing key.</param>
    /// <param name="appHost">The AppHost process.</param>
    internal CompositionRunSession(CompositionRunPlan plan, CompositionReadiness readiness, string signingKey, Process appHost)
    {
        Plan = plan;
        Readiness = readiness;
        SigningKey = signingKey;
        AppHost = appHost;
    }

    /// <summary>
    /// Gets the run identity.
    /// </summary>
    public string RunId => Plan.RunId;

    /// <summary>
    /// Gets the metadata-only run plan.
    /// </summary>
    public CompositionRunPlan Plan { get; internal set; }

    /// <summary>
    /// Gets the validated readiness document.
    /// </summary>
    public CompositionReadiness Readiness { get; internal set; }

    /// <summary>
    /// Gets the per-run development signing key. It is never serialized or written to retained output.
    /// </summary>
    public string SigningKey { get; }

    /// <summary>Gets or sets the immutable validated pre-launch handoff held outside child-writable storage.</summary>
    internal System.Text.Json.JsonElement? EnvironmentBindings { get; set; }

    /// <summary>Gets or sets the first runner-observed stopping origin, independently of outcome publication.</summary>
    internal long? StoppingTimestamp { get; set; }

    /// <summary>Gets or sets the first runner-held causal observation for retry after callback failure.</summary>
    internal NativeTestExecutionResult? FirstObservedOutcome { get; set; }

    /// <summary>
    /// Gets a value indicating whether this process executes locally, read from the process environment when the session
    /// becomes ready with the same rule <c>ExecutionContext()</c> uses to record the owner's context. Attachment admission
    /// never accepts another context, so only a local selected-test session keeps its AppHost until withdrawal is durably
    /// recorded.
    /// </summary>
    internal bool LocalExecution { get; init; } = true;

    /// <summary>
    /// Gets the AppHost process.
    /// </summary>
    internal Process AppHost { get; }

    /// <inheritdoc />
    public override string ToString() => $"CompositionRunSession {{ RunId = {RunId}, SigningKey = [redacted] }}";
}
