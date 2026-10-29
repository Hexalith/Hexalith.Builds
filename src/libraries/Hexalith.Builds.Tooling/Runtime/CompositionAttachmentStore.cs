// <copyright file="CompositionAttachmentStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;
using System.Text.Json.Nodes;

/// <summary>Persists accepted attachments beside the owner's diagnostics, serialized by an exclusive lock.</summary>
public static class CompositionAttachmentStore
{
    /// <summary>Gets the attachment document path for an owner.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <returns>The document path.</returns>
    public static string PathFor(string diagnosticsDirectory) => Path.Combine(diagnosticsDirectory, "attachments.json");

    /// <summary>Reads the verified attachments of an owner; absence is an empty list and invalid metadata is an error.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="cancellationToken">The read allowance.</param>
    /// <returns>The attachments.</returns>
    /// <exception cref="IOException">The existing document cannot be verified.</exception>
    public static async Task<IReadOnlyList<CompositionAttachment>> ReadAsync(string diagnosticsDirectory, string ownerRunId, CancellationToken cancellationToken)
    {
        CompositionAttachmentDocument? document = await CompositionDocumentStore.ReadIfExistsAsync<CompositionAttachmentDocument>(PathFor(diagnosticsDirectory), cancellationToken).ConfigureAwait(false);
        return document is null ? [] : Verified(document, ownerRunId);
    }

    /// <summary>Projects the owner's holds as the contract <c>attachments</c> binding.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory, or null when none is known.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="now">The UTC clock reading.</param>
    /// <returns>The populated binding.</returns>
    /// <exception cref="InvalidDataException">The recorded attachments cannot be verified.</exception>
    public static JsonObject ReadContractBinding(string? diagnosticsDirectory, string ownerRunId, DateTimeOffset now)
    {
        JsonArray holds = [];
        if (diagnosticsDirectory is not null && File.Exists(PathFor(diagnosticsDirectory)))
        {
            try
            {
                CompositionAttachmentDocument document = JsonSerializer.Deserialize<CompositionAttachmentDocument>(File.ReadAllBytes(PathFor(diagnosticsDirectory)), CompositionDocumentStore.SerializerOptions)
                    ?? throw new InvalidDataException("Recorded attachments cannot be verified.");
                foreach (CompositionAttachment attachment in Verified(document, ownerRunId))
                {
                    holds.Add(attachment.ToContract(now));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                throw new InvalidDataException("Recorded attachments cannot be verified.", exception);
            }
        }

        return new JsonObject { ["state"] = "populated", ["holds"] = holds };
    }

    /// <summary>Applies one serialized change to an owner's attachments.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="change">Receives the current attachments and returns the result plus the replacement list, or null for no write.</param>
    /// <param name="cancellationToken">The allowance.</param>
    /// <returns>The change result.</returns>
    /// <exception cref="IOException">The lock could not be acquired or the metadata is invalid.</exception>
    public static async Task<T> UpdateAsync<T>(
        string diagnosticsDirectory,
        string ownerRunId,
        Func<IReadOnlyList<CompositionAttachment>, (T Result, IReadOnlyList<CompositionAttachment>? Replacement)> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        CompositionWorkspace.CreatePrivateDirectory(diagnosticsDirectory);
        string lockPath = Path.Combine(diagnosticsDirectory, "attachments.lock");
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(5);
        FileStream? held = null;
        while (held is null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < giveUp)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }

        await using (held.ConfigureAwait(false))
        {
            IReadOnlyList<CompositionAttachment> current = await ReadAsync(diagnosticsDirectory, ownerRunId, cancellationToken).ConfigureAwait(false);
            (T result, IReadOnlyList<CompositionAttachment>? replacement) = change(current);
            if (replacement is not null)
            {
                await CompositionDocumentStore.WriteAsync(PathFor(diagnosticsDirectory), new CompositionAttachmentDocument(CompositionAttachmentDocument.SupportedSchema, ownerRunId, replacement), cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
    }

    /// <summary>Waits until no accepted attachment is live. Deadlines are evaluated against UTC on every poll.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="pollInterval">The longest pause between reads.</param>
    /// <param name="cancellationToken">The wait cancellation.</param>
    /// <returns>The task that completes when every hold has ended or passed its deadline.</returns>
    /// <exception cref="IOException">The attachment metadata cannot be verified.</exception>
    public static async Task WaitForNoLiveHoldsAsync(string diagnosticsDirectory, string ownerRunId, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<CompositionAttachment> live = [.. (await ReadAsync(diagnosticsDirectory, ownerRunId, cancellationToken).ConfigureAwait(false)).Where(item => item.IsLive(now))];
            if (live.Count == 0)
            {
                return;
            }

            TimeSpan untilLastExpiry = live.Max(item => item.ExpiresAtUtc) - now;
            TimeSpan pause = untilLastExpiry < pollInterval ? untilLastExpiry : pollInterval;
            await Task.Delay(pause > TimeSpan.Zero ? pause : TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<CompositionAttachment> Verified(CompositionAttachmentDocument document, string ownerRunId)
    {
        if (document.Attachments is null)
        {
            throw new IOException("Runner attachment metadata cannot be verified.");
        }

        bool valid = document.Schema == CompositionAttachmentDocument.SupportedSchema && document.OwnerRunId == ownerRunId
            && document.Attachments.All(item => item?.IsConsistent() == true && item.OwnerRunId == ownerRunId)
            && document.Attachments.Select(item => item.AttachedRunId).Distinct(StringComparer.Ordinal).Count() == document.Attachments.Count
            && document.Attachments.Select(item => item.OriginalAttachmentRequestId).Distinct(StringComparer.Ordinal).Count() == document.Attachments.Count;
        return valid ? document.Attachments : throw new IOException("Runner attachment metadata cannot be verified.");
    }
}
