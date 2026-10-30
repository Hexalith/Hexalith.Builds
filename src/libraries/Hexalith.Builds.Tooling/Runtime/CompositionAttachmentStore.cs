// <copyright file="CompositionAttachmentStore.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.Json;
using System.Text.Json.Nodes;

using Hexalith.Builds.Tooling.Diagnostics;

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
    public static Task<T> UpdateAsync<T>(
        string diagnosticsDirectory,
        string ownerRunId,
        Func<IReadOnlyList<CompositionAttachment>, (T Result, IReadOnlyList<CompositionAttachment>? Replacement)> change,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(change);
        return UpdateCoreAsync<T>(diagnosticsDirectory, ownerRunId, current => Task.FromResult(change(current)), cancellationToken);
    }

    /// <summary>Durably withdraws every unended attachment, recording earlier expiries at their original deadline.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="reason">The environment withdrawal reason.</param>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The newly withdrawn attachments.</returns>
    public static Task<IReadOnlyList<CompositionAttachment>> WithdrawAsync(string diagnosticsDirectory, string ownerRunId, string reason, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(reason is "owner-cancelled" or "developer-stop", true);

        return UpdateAsync<IReadOnlyList<CompositionAttachment>>(
            diagnosticsDirectory,
            ownerRunId,
            current =>
            {
                List<CompositionAttachment> withdrawn = [];
                List<CompositionAttachment> replacement = [];
                foreach (CompositionAttachment item in current)
                {
                    if (item.EndedAtUtc is not null)
                    {
                        replacement.Add(item);
                        continue;
                    }

                    CompositionAttachment ended = item.ObservedAt(now);
                    if (ended.EndedAtUtc is null)
                    {
                        ended = item with { EndedAtUtc = now < item.AcceptedAtUtc ? item.AcceptedAtUtc : now, EndReason = reason, Outcome = "withdrawn" };
                    }

                    withdrawn.Add(ended);
                    replacement.Add(ended);
                }

                return (withdrawn, withdrawn.Count == 0 ? null : replacement);
            },
            cancellationToken);
    }

    /// <summary>Waits until every hold ends or a first attached failure establishes retention, recording observed expiries.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="pollInterval">The longest pause between reads.</param>
    /// <param name="cancellationToken">The wait cancellation.</param>
    /// <returns>Whether an attached failure established retention.</returns>
    /// <exception cref="IOException">The attachment metadata cannot be verified.</exception>
    public static async Task<bool> WaitForHoldsOrFailureAsync(string diagnosticsDirectory, string ownerRunId, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        while (true)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            IReadOnlyList<CompositionAttachment> observed = await ObserveAsync(diagnosticsDirectory, ownerRunId, now, cancellationToken).ConfigureAwait(false);
            bool failed = observed.Any(item => item.Outcome == "failed");
            CompositionAttachment[] live = [.. observed.Where(item => item.IsLive(now))];
            if (failed || live.Length == 0)
            {
                return failed;
            }

            TimeSpan untilNextExpiry = live.Min(item => item.ExpiresAtUtc) - now;
            TimeSpan pause = untilNextExpiry < pollInterval ? untilNextExpiry : pollInterval;
            await Task.Delay(pause > TimeSpan.Zero ? pause : TimeSpan.FromMilliseconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the withdrawal report before any environment teardown.</summary>
    /// <param name="withdrawn">The durably recorded withdrawals.</param>
    /// <returns>Reports grouped by the actual withdrawal reason.</returns>
    internal static IReadOnlyList<ToolDiagnostic> WithdrawalDiagnostics(IReadOnlyList<CompositionAttachment> withdrawn) => [.. withdrawn.GroupBy(item => item.EndReason).Select(group => new ToolDiagnostic("HXI011", ToolPhase.Cleanup, ToolFailureCategory.None, $"Attached runs withdrawn ({group.Key}): {string.Join(", ", group.Select(item => item.AttachedRunId))}.", group.Key))];

    /// <summary>Records every expiry observed in an owner pass.</summary>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="now">The runner's UTC clock reading.</param>
    /// <param name="cancellationToken">The recording allowance.</param>
    /// <returns>The attachments including durably recorded expiries.</returns>
    internal static Task<IReadOnlyList<CompositionAttachment>> ObserveAsync(string diagnosticsDirectory, string ownerRunId, DateTimeOffset now, CancellationToken cancellationToken) =>
        UpdateAsync<IReadOnlyList<CompositionAttachment>>(
            diagnosticsDirectory,
            ownerRunId,
            current =>
            {
                CompositionAttachment[] updated = [.. current.Select(item => item.ObservedAt(now))];
                return (updated, updated.SequenceEqual(current) ? null : updated);
            },
            cancellationToken);

    /// <summary>Applies one serialized change whose decision may itself read durable state under the lock.</summary>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="diagnosticsDirectory">The owner's durable diagnostics directory.</param>
    /// <param name="ownerRunId">The owner identity.</param>
    /// <param name="change">Receives the current attachments and returns the result plus the replacement list, or null for no write.</param>
    /// <param name="cancellationToken">The allowance.</param>
    /// <returns>The change result.</returns>
    internal static async Task<T> UpdateCoreAsync<T>(
        string diagnosticsDirectory,
        string ownerRunId,
        Func<IReadOnlyList<CompositionAttachment>, Task<(T Result, IReadOnlyList<CompositionAttachment>? Replacement)>> change,
        CancellationToken cancellationToken)
    {
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
            (T result, IReadOnlyList<CompositionAttachment>? replacement) = await change(current).ConfigureAwait(false);
            if (replacement is not null)
            {
                await CompositionDocumentStore.WriteAsync(PathFor(diagnosticsDirectory), new CompositionAttachmentDocument(CompositionAttachmentDocument.SupportedSchema, ownerRunId, replacement), cancellationToken).ConfigureAwait(false);
            }

            return result;
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
        return valid ? [.. document.Attachments.Select(item => item.EndReason == "terminal-outcome" && item.Outcome is null ? item with { Outcome = "unknown" } : item)] : throw new IOException("Runner attachment metadata cannot be verified.");
    }
}
