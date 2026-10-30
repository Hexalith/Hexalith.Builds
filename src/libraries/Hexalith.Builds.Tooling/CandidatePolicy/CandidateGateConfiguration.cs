// <copyright file="CandidateGateConfiguration.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.CandidatePolicy;

/// <summary>Independently enrolled workflow authority; never taken from request JSON.</summary>
/// <param name="WorkflowRef">The reusable Builds workflow reference at an immutable SHA.</param>
/// <param name="WorkflowSha">The enrolled exact reusable workflow commit.</param>
/// <param name="Environment">The protected approval environment.</param>
/// <param name="CallerWorkflowPath">The enrolled repository workflow path.</param>
/// <param name="CallerContentIdentity">SHA-256 of the enrolled caller workflow bytes.</param>
/// <param name="ConsumerJobId">The logical job identity executing the runner.</param>
/// <param name="ConsumerJobName">The GitHub API name of that job.</param>
public sealed record CandidateGateConfiguration(string WorkflowRef, string WorkflowSha, string Environment, string CallerWorkflowPath, string CallerContentIdentity, string ConsumerJobId, string ConsumerJobName);
