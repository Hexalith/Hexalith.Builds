// <copyright file="PlatformIdentityFinding.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

/// <summary>One condition that makes a Platform identity unsuitable for package mode.</summary>
/// <param name="RuleId">The HXW rule.</param>
/// <param name="Message">The actionable condition.</param>
/// <param name="Path">The related path.</param>
/// <param name="Hint">Optional remediation.</param>
public sealed record PlatformIdentityFinding(string RuleId, string Message, string Path, string? Hint = null);
