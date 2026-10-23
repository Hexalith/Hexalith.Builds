// <copyright file="RecordReference.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.ReleaseRecords;

/// <summary>
/// Names exact verified content, rather than trusting a record identifier alone.
/// </summary>
/// <param name="RecordId">The referenced record identifier.</param>
/// <param name="Digest">The canonical signed-content SHA-256 digest.</param>
public sealed record RecordReference(
    string RecordId,
    string Digest);