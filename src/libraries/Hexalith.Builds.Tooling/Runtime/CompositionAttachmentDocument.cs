// <copyright file="CompositionAttachmentDocument.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>The durable attachment records of one owner environment.</summary>
/// <param name="Schema">The document schema identity.</param>
/// <param name="OwnerRunId">The owning run.</param>
/// <param name="Attachments">The accepted attachments in acceptance order.</param>
public sealed record CompositionAttachmentDocument(string Schema, string OwnerRunId, IReadOnlyList<CompositionAttachment> Attachments)
{
    /// <summary>The supported document schema identity.</summary>
    public const string SupportedSchema = "hexalith.g4-run-attachments.v1";
}
