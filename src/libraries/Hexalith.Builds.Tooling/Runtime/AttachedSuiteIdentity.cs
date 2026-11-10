// <copyright file="AttachedSuiteIdentity.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The issued identity, private paths and narrowed access of one runner-owned attached suite run.</summary>
/// <param name="Hold">The accepted hold whose expiry bounds the run's access.</param>
/// <param name="AttemptId">The attempt identity the live owner issued at pickup.</param>
/// <param name="Workspace">The attachment's private workspace, never shared with the owner or peers.</param>
/// <param name="DiagnosticsDirectory">The attached run's durable diagnostics directory.</param>
internal sealed record AttachedSuiteIdentity(CompositionAttachment Hold, string AttemptId, string Workspace, string DiagnosticsDirectory)
{
    /// <summary>Gets the attached run identity; it is also the child process run tag.</summary>
    internal string RunId => Hold.AttachedRunId;

    /// <summary>Gets the attachment's private descriptor path.</summary>
    internal string DescriptorPath => Path.GetFullPath(Path.Combine(Workspace, "test-environment.json"));

    /// <summary>Gets the attachment's private endpoint credential channel path.</summary>
    internal string CredentialsPath => Path.GetFullPath(Path.Combine(Workspace, NativeTestHandoff.EndpointCredentialsFileName));

    /// <summary>Gets the attachment's private plan snapshot path.</summary>
    internal string PlanPath => Path.Combine(Workspace, "plan.json");

    /// <summary>Gets the attachment's private readiness snapshot path.</summary>
    internal string ReadinessPath => Path.Combine(Workspace, "readiness.json");

    /// <summary>Gets the attached workspace for a hold under its owner's workspace.</summary>
    /// <param name="ownerWorkspace">The owner's run workspace.</param>
    /// <param name="attachedRunId">The attached run.</param>
    /// <returns>The private attachment workspace.</returns>
    internal static string WorkspaceFor(string ownerWorkspace, string attachedRunId) => Path.Combine(Path.GetFullPath(ownerWorkspace), "attachments", attachedRunId);

    /// <summary>Gets the access expiry: the earlier of the execution deadline and the hold expiry.</summary>
    /// <param name="timing">The attached run's execution clock.</param>
    /// <returns>The latest instant the narrowed bearer may be valid.</returns>
    internal DateTimeOffset AccessExpiry(ModuleTestExecutionTiming timing) => timing.DeadlineUtc < Hold.ExpiresAtUtc ? timing.DeadlineUtc : Hold.ExpiresAtUtc;
}
