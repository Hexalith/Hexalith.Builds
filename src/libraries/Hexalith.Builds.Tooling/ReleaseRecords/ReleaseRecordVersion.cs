// <copyright file="ReleaseRecordVersion.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>Selects the signed wire schema to validate and encode.</summary>
public enum ReleaseRecordVersion
{
    /// <summary>The original operational contracts.</summary>
    V1 = 1,

    /// <summary>The staging-evidence attempt and promotion decision action contracts.</summary>
    V2 = 2,
}