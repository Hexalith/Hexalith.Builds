// <copyright file="CompositionOwnerTermination.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

/// <summary>Persisted owner observations distinguish native work, environment work and issued access.</summary>
/// <param name="OwnedWork">Native-child and environment observations.</param>
/// <param name="EnvironmentAccess">Issued-access observations.</param>
/// <param name="LeftoverWorkIds">Observed leftovers and discovery uncertainty.</param>
public sealed record CompositionOwnerTermination(
    IReadOnlyList<CompositionTerminationItem> OwnedWork,
    IReadOnlyList<CompositionTerminationItem> EnvironmentAccess,
    IReadOnlyList<string> LeftoverWorkIds)
{
    /// <summary>Gets the latest observed native process identities for exact recovery.</summary>
    public IReadOnlyList<int> NativeProcessIds { get; init; } = [];

    /// <summary>Gets the issued bearer expiry, without retaining the bearer itself.</summary>
    public DateTimeOffset? CredentialExpiresAtUtc { get; init; }

    /// <summary>Gets failed item identities that resolve in the termination lists.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Failures => [.. OwnedWork.Concat(EnvironmentAccess).Where(item => item.Status == "failed").Select(item => item.Id)];

    /// <summary>Advances only observations supported by native execution and exact cleanup.</summary>
    /// <param name="state">The original owner.</param>
    /// <param name="record">The preserved first outcome and cleanup observations.</param>
    /// <param name="native">The actual native launch and stop observations, when available.</param>
    /// <returns>The owner termination observations.</returns>
    internal static CompositionOwnerTermination Observe(CompositionRunState state, ModuleTestRunRecord record, NativeTestExecutionResult? native)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CompositionOwnerTermination? prior = native is null ? record.Termination : ObserveNative(state.RunId, record.Termination, native);
        string childId = "native:" + state.RunId;
        string environmentId = "environment:" + state.RunId;
        string accessId = "access:" + state.RunId;
        bool cleaned = record.CleanupDisposition == "complete" && record.RemainingResources.IsEmpty;
        bool attempted = record.CleanupDisposition is "incomplete" or "complete";
        bool issued = state.EnvironmentWorkIssued || state.Status == CompositionRunStatus.Ready || state.AppHostProcessId is not null || record.Resources.Count > 0 || record.RemainingResources.ResourceIds.Count > 0;
        CompositionTerminationItem? recordedChild = prior?.OwnedWork.FirstOrDefault(item => item.Id == childId);
        bool historyUnknown = recordedChild is null && (state.Status != CompositionRunStatus.Starting || record.ExecutionTiming is not null);
        CompositionTerminationItem child = recordedChild
            ?? new(childId, historyUnknown ? "pending" : "not-applicable", now, historyUnknown ? "Original native launch and stopping observations are unavailable." : "No native child was issued.");
        if (child.Status == "pending" && attempted && !cleaned)
        {
            child = new(childId, "failed", now, "Recovery could not verify native process stopping.");
        }
        else if (cleaned && child.Status is "pending" or "failed")
        {
            child = new(childId, "terminated", now, "Exact owner process discovery verified no surviving work.");
        }

        string environmentStatus = (issued, cleaned, attempted) switch
        {
            (false, _, _) => "not-applicable",
            (_, true, _) => "terminated",
            (_, _, true) => "failed",
            _ => "pending",
        };
        CompositionTerminationItem environment = new(environmentId, environmentStatus, now, environmentStatus switch
        {
            "terminated" => "Exact owned cleanup and discovery verified environment deletion.",
            "failed" => "Environment stopping or deletion remains incomplete or unverified.",
            "pending" => "Owned environment work remains available or retained.",
            _ => "No environment work was issued.",
        });
        DateTimeOffset? credentialExpiry = prior?.CredentialExpiresAtUtc;
        bool accessIssued = historyUnknown || credentialExpiry is not null || prior?.EnvironmentAccess.Any(item => item.Status != "not-applicable") == true;
        string accessStatus = (accessIssued, cleaned, attempted) switch
        {
            (false, _, _) => "not-applicable",
            (_, true, _) => "terminated",
            (_, _, true) => "failed",
            _ => "pending",
        };
        CompositionTerminationItem access = new(accessId, accessStatus, now, accessStatus switch
        {
            "terminated" => "Owned environment deletion ended access; private workspace deletion was verified.",
            "failed" => "Environment access ending cannot be verified after incomplete cleanup.",
            "pending" => "Environment access ending has not been verified; a passing test supplies no revocation proof.",
            _ => "No native environment access was issued.",
        });
        IReadOnlyList<string> unresolved = [];
        if (!cleaned && child.Status is "pending" or "failed" && prior is not null)
        {
            string[] recorded = [.. prior.LeftoverWorkIds.Where(id => id.StartsWith("process:", StringComparison.Ordinal))];
            unresolved = recorded.Length > 0 ? recorded : [.. prior.NativeProcessIds.Select(ProcessIdentity)];
        }

        CompositionTerminationItem Stable(CompositionTerminationItem item)
        {
            CompositionTerminationItem? previous = new[] { prior, record.Termination }
                .Where(observed => observed is not null)
                .SelectMany(observed => observed!.OwnedWork.Concat(observed.EnvironmentAccess))
                .FirstOrDefault(observed => observed.Id == item.Id);
            return previous is not null && previous.Status == item.Status ? previous : item;
        }

        return new([Stable(child), Stable(environment)], [Stable(access)], [.. record.RemainingResources.ResourceIds.Concat(unresolved).Distinct(StringComparer.Ordinal)])
        {
            NativeProcessIds = prior?.NativeProcessIds ?? [],
            CredentialExpiresAtUtc = credentialExpiry,
        };
    }

    /// <summary>Accumulates actual native issuance and stopping without creating a terminal suite outcome.</summary>
    /// <param name="runId">The owner run identity.</param>
    /// <param name="prior">Previously observed entries, including successful nonfinal entries.</param>
    /// <param name="native">The current entry's actual observation.</param>
    /// <param name="running">Whether this is a launch observation before stopping.</param>
    /// <returns>The cumulative native work/access ledger.</returns>
    internal static CompositionOwnerTermination ObserveNative(string runId, CompositionOwnerTermination? prior, NativeTestExecutionResult native, bool running = false)
    {
        DateTimeOffset now = native.ObservedAtUtc ?? DateTimeOffset.UtcNow;
        string id = "native:" + runId;
        CompositionTerminationItem? previous = prior?.OwnedWork.FirstOrDefault(item => item.Id == id);
        bool historyIssued = prior?.NativeProcessIds.Count > 0 || prior?.CredentialExpiresAtUtc is not null || previous?.Status is "terminated" or "failed" || prior?.EnvironmentAccess.Any(item => item.Status != "not-applicable") == true;
        bool priorUnverifiable = previous?.Status == "failed" && prior?.NativeProcessIds.Count == 0;
        IReadOnlyList<int> stopped = native.Launched && native.StopObserved && native.SurvivingProcessIds.Count == 0 ? native.ProcessIds : [];
        string[] recordedPrior = [.. prior?.LeftoverWorkIds.Where(item => item.StartsWith("process:", StringComparison.Ordinal)) ?? []];
        if (recordedPrior.Length == 0 && previous?.Status is "pending" or "failed")
        {
            recordedPrior = [.. prior?.NativeProcessIds.Select(ProcessIdentity) ?? []];
        }

        string[] priorUnresolved = previous?.Status is "pending" or "failed"
            ? [.. recordedPrior.Except(stopped.Select(ProcessIdentity), StringComparer.Ordinal)]
            : [];
        string[] currentUnresolved = native.Launched && !native.StopObserved ? [.. native.ProcessIds.Concat(native.SurvivingProcessIds).Distinct().Select(ProcessIdentity)] : [.. native.SurvivingProcessIds.Select(ProcessIdentity)];
        string[] unresolved = [.. priorUnresolved.Concat(currentUnresolved).Distinct(StringComparer.Ordinal)];
        string status = (native.Launched, historyIssued, running, native.StopObserved && unresolved.Length == 0 && !priorUnverifiable) switch
        {
            (false, false, _, _) => "not-applicable",
            (false, true, _, _) => previous?.Status ?? "failed",
            (true, _, true, _) => "pending",
            (true, _, _, true) => "terminated",
            _ => "failed",
        };
        CompositionTerminationItem child = new(id, status, now, status switch
        {
            "not-applicable" => "No native child was launched.",
            "terminated" => "Native process exit or bounded stopping was observed.",
            "pending" => "Native work was launched; stopping is not yet observed.",
            _ => "Native process stopping could not be verified.",
        });
        if (!native.Launched && historyIssued && previous is not null)
        {
            child = previous;
        }

        DateTimeOffset? expiry = prior?.CredentialExpiresAtUtc;
        if (native.Launched && native.CredentialExpiresAtUtc is { } issuedExpiry && (expiry is null || issuedExpiry > expiry))
        {
            expiry = issuedExpiry;
        }

        bool accessApplicable = native.Launched || expiry is not null || prior?.EnvironmentAccess.Any(item => item.Status != "not-applicable") == true;
        CompositionTerminationItem access = new("access:" + runId, accessApplicable ? "pending" : "not-applicable", now, accessApplicable ? "Native access ending has not been verified." : "No native environment access was issued.");
        return new([child], [access], unresolved)
        {
            NativeProcessIds = [.. (prior?.NativeProcessIds ?? []).Concat(native.ProcessIds).Concat(native.SurvivingProcessIds).Distinct()],
            CredentialExpiresAtUtc = expiry,
        };
    }

    private static string ProcessIdentity(int processId) => "process:" + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
}