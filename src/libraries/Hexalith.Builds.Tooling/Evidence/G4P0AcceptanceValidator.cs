// <copyright file="G4P0AcceptanceValidator.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Evidence;

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Filesystem;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.RunEvidence;
using Hexalith.Builds.Tooling.Runtime;
using Hexalith.Builds.Tooling.TestReports;

/// <summary>
/// Validates the final G-4 P0 acceptance record fail-closed; local candidate evidence never validates as acceptance.
/// </summary>
internal static class G4P0AcceptanceValidator
{
    private const string _schema = "hexalith.g4-p0-acceptance.v1";

    private const long _maximumRecordBytes = 4 * 1024 * 1024;

    private static readonly string[] _requiredRunKeys = ["cancelled", "persisted-mtp", "persisted-vstest", "prerequisite-unavailable"];

    private static readonly string[] _requiredRoles = ["Builds Owner", "Platform Owner", "Test Architect"];

    private static readonly string[] _packageIds = ["Hexalith.Builds.Evidence.Cli", "Hexalith.Builds.Module.Cli"];

    /// <summary>
    /// Validates one JSON acceptance record and every artifact it binds.
    /// </summary>
    /// <param name="path">The JSON record path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A deterministic, metadata-only command result.</returns>
    public static async Task<ToolCommandResult> ValidateAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        byte[] bytes;
        string repositoryRoot;
        try
        {
            string fullPath = Path.GetFullPath(path);
            FileInfo file = new(fullPath);
            if (!file.Exists || file.Length > _maximumRecordBytes)
            {
                return Fail("HXE200", "record");
            }

            bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            repositoryRoot = ManifestPathValidator.FindRepositoryRoot(fullPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return Fail("HXE200", "record");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes);
            return await ValidateRecordAsync(document.RootElement, repositoryRoot, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IOException or UnauthorizedAccessException)
        {
            return Fail("HXE200", "record");
        }
    }

    private static async Task<ToolCommandResult> ValidateRecordAsync(JsonElement root, string repositoryRoot, CancellationToken cancellationToken)
    {
        if (!NoDuplicateProperties(root) || !NoSecretMetadata(root))
        {
            return Fail("HXE200", "record");
        }

        if (!ExactFields(root, "schema", "status", "sourceRevision", "platformPins", "fixture", "packages", "runs", "cleanup", "rollback", "approvals")
            || !StringEquals(root, "schema", _schema))
        {
            return Fail("HXE201", "schema");
        }

        if (!StringEquals(root, "status", "accepted")
            || !TryString(root, "sourceRevision", out string revision)
            || !IsLowerHex(revision, 40))
        {
            return Fail("HXE202", "status");
        }

        JsonElement pins = root.GetProperty("platformPins");
        if (!ExactFields(pins, "eventStoreVersion", "daprRuntimeVersion", "daprSdkVersion", "frontComposerVersion")
            || !StringEquals(pins, "eventStoreVersion", SupportedPlatformPins.EventStoreVersion)
            || !StringEquals(pins, "daprRuntimeVersion", SupportedPlatformPins.DaprRuntimeVersion)
            || !StringEquals(pins, "daprSdkVersion", SupportedPlatformPins.DaprSdkVersion)
            || !StringEquals(pins, "frontComposerVersion", SupportedPlatformPins.FrontComposerVersion))
        {
            return Fail("HXE202", "platformPins");
        }

        JsonElement fixture = root.GetProperty("fixture");
        if (!ExactFields(fixture, "manifestPath", "manifestSha256")
            || !TryArtifact(repositoryRoot, fixture, "manifestPath", "manifestSha256", out _))
        {
            return Fail("HXE203", "fixture");
        }

        if (!TryPackageVersion(repositoryRoot, root.GetProperty("packages"), out string version))
        {
            return Fail("HXE204", "packages");
        }

        JsonElement runs = root.GetProperty("runs");
        if (runs.ValueKind != JsonValueKind.Array)
        {
            return Fail("HXE205", "runs");
        }

        HashSet<string> runKeys = new(StringComparer.Ordinal);
        List<(string Key, string RunId, DateTimeOffset CompletedUtc)> identities = [];
        foreach (JsonElement run in runs.EnumerateArray())
        {
            if (!TryString(run, "key", out string key) || !runKeys.Add(key))
            {
                return Fail("HXE205", "runs");
            }

            ToolCommandResult? runFailure = await ValidateRunAsync(run, key, repositoryRoot, revision, version, pins, fixture, identities, cancellationToken).ConfigureAwait(false);
            if (runFailure is not null)
            {
                return runFailure;
            }
        }

        if (!runKeys.SetEquals(_requiredRunKeys)
            || identities.Select(identity => identity.RunId).Distinct(StringComparer.Ordinal).Count() != identities.Count)
        {
            return Fail("HXE205", "runs");
        }

        string[] persistedRunIds = [.. identities.Where(identity => identity.Key.StartsWith("persisted-", StringComparison.Ordinal)).Select(identity => identity.RunId)];
        if (!await CompletedCleanupAsync(repositoryRoot, root.GetProperty("cleanup"), revision, version, pins, persistedRunIds, cancellationToken).ConfigureAwait(false))
        {
            return Fail("HXE207", "cleanup");
        }

        // The rollback drill has no machine-checkable artifact contract before Stage 7, so it stays an attested, hash-bound control.
        if (!CompletedControl(repositoryRoot, root.GetProperty("rollback"), "rollback"))
        {
            return Fail("HXE207", "rollback");
        }

        bool approved = ValidApprovals(root.GetProperty("approvals"), revision, identities.Max(identity => identity.CompletedUtc));
        return !approved
            ? Fail("HXE208", "approvals")
            : new ToolCommandResult(
                "passed",
                ToolOutcome.Passed(),
                [new ToolDiagnostic("HXI210", ToolPhase.Evidence, ToolFailureCategory.None, "The G-4 P0 acceptance record is complete and consistent.", "record")]);
    }

    private static async Task<ToolCommandResult?> ValidateRunAsync(
        JsonElement run,
        string key,
        string repositoryRoot,
        string revision,
        string version,
        JsonElement pins,
        JsonElement fixture,
        List<(string Key, string RunId, DateTimeOffset CompletedUtc)> identities,
        CancellationToken cancellationToken)
    {
        string field = "runs." + key;
        if (!ExactFields(run, "key", "command", "exitCode", "finalStatus", "evidencePath", "evidenceSha256", "reportPath", "reportSha256", "reportPlatform")
            || !TryString(run, "command", out string command)
            || !TryString(run, "finalStatus", out string finalStatus)
            || !TryInt(run, "exitCode", out int exitCode)
            || !TryArtifact(repositoryRoot, run, "evidencePath", "evidenceSha256", out string evidencePath))
        {
            return Fail("HXE205", field);
        }

        if (new FileInfo(evidencePath).Length > _maximumRecordBytes)
        {
            return Fail("HXE205", field);
        }

        byte[] evidenceBytes = await File.ReadAllBytesAsync(evidencePath, cancellationToken).ConfigureAwait(false);
        if (!ModuleRunEvidenceArtifactValidator.TryValidate(evidenceBytes, out ModuleRunEvidenceArtifactSummary? summary)
            || summary is null
            || !string.Equals(command, "dotnet tool run " + summary.Command, StringComparison.Ordinal)
            || !string.Equals(summary.FinalStatus, finalStatus, StringComparison.Ordinal)
            || summary.ExitCode != exitCode
            || !string.Equals(summary.ManifestPath, fixture.GetProperty("manifestPath").GetString(), StringComparison.Ordinal)
            || !string.Equals(summary.ManifestHash, fixture.GetProperty("manifestSha256").GetString(), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("HXE205", field);
        }

        using JsonDocument evidence = JsonDocument.Parse(evidenceBytes);
        JsonElement artifact = evidence.RootElement;
        if (!BoundToRevision(artifact, revision, version, pins))
        {
            return Fail("HXE205", field);
        }

        identities.Add((key, artifact.GetProperty("runId").GetString()!, artifact.GetProperty("timestamps").GetProperty("completedUtc").GetDateTimeOffset()));
        string? nativePlatform = DeclaredNativePlatform(repositoryRoot, artifact.GetProperty("invocation"));
        bool nativeTestRun = nativePlatform is not null && summary.Command.StartsWith("hexalith-module test ", StringComparison.Ordinal);
        JsonElement outcome = artifact.GetProperty("outcome");

        // An unsupported profile (HXR029) also reports a prerequisite category, but it is not a missing prerequisite.
        bool prerequisiteControl = exitCode == 2
            && finalStatus == "unavailable"
            && nativeTestRun
            && StringEquals(outcome, "phase", nameof(ToolPhase.Prerequisite))
            && StringEquals(outcome, "category", nameof(ToolFailureCategory.PrerequisiteUnavailable))
            && !StringEquals(outcome, "ruleId", "HXR029");
        bool cancelledControl = exitCode == 130 && finalStatus == "cancelled" && nativeTestRun && StringEquals(outcome, "ruleId", "HXC130");
        return key switch
        {
            "persisted-vstest" or "persisted-mtp" => await ValidatePersistedRunAsync(run, key, field, exitCode, finalStatus, summary, artifact, nativePlatform, repositoryRoot, cancellationToken).ConfigureAwait(false),
            "prerequisite-unavailable" => ValidateControlRun(run, field, prerequisiteControl),
            "cancelled" => ValidateControlRun(run, field, cancelledControl),
            _ => Fail("HXE205", field),
        };
    }

    private static async Task<ToolCommandResult?> ValidatePersistedRunAsync(
        JsonElement run,
        string key,
        string field,
        int exitCode,
        string finalStatus,
        ModuleRunEvidenceArtifactSummary summary,
        JsonElement artifact,
        string? nativePlatform,
        string repositoryRoot,
        CancellationToken cancellationToken)
    {
        JsonElement modules = artifact.GetProperty("topology").GetProperty("modules");
        string[] moduleIds = modules.ValueKind == JsonValueKind.Array
            ? [.. modules.EnumerateArray().Select(module => module.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)]
            : [];
        if (exitCode != 0
            || finalStatus != "completed"
            || !summary.TestsReported
            || summary.TestsPassed <= 0
            || summary.TestsFailed != 0
            || nativePlatform is null
            || moduleIds.Length < 2
            || !StringArrayEquals(artifact, "persistedAssertions", PersistedProfileEvidence.Assertions(moduleIds))
            || !StringArrayEquals(artifact, "expectedSequences", PersistedProfileEvidence.Sequences(moduleIds)))
        {
            return Fail("HXE205", field);
        }

        string platform = key["persisted-".Length..];
        if (!string.Equals(nativePlatform, platform, StringComparison.Ordinal)
            || !StringEquals(run, "reportPlatform", platform)
            || !TryString(run, "reportPath", out string reportPath)
            || !string.Equals(reportPath, NativeTestExecutor.RetainedReportPath(run.GetProperty("evidencePath").GetString()!, platform), StringComparison.Ordinal)
            || !TryArtifact(repositoryRoot, run, "reportPath", "reportSha256", out string resolvedReport)
            || !artifact.GetProperty("artifactHashes").TryGetProperty(reportPath, out JsonElement boundHash)
            || !string.Equals(boundHash.GetString(), run.GetProperty("reportSha256").GetString(), StringComparison.OrdinalIgnoreCase))
        {
            return Fail("HXE206", field + ".report");
        }

        NativeTestReportLoadResult native = await NativeTestReportLoader.LoadAsync(resolvedReport, cancellationToken).ConfigureAwait(false);
        JsonElement counts = artifact.GetProperty("testCounts");
        return native.Report is { } report
            && counts.GetProperty("total").GetInt32() == report.Total
            && counts.GetProperty("passed").GetInt32() == report.Passed
            && counts.GetProperty("failed").GetInt32() == report.Failed
            && counts.GetProperty("skipped").GetInt32() == report.Skipped
                ? null
                : Fail("HXE206", field + ".report");
    }

    private static ToolCommandResult? ValidateControlRun(JsonElement run, string field, bool expectedOutcome) =>
        expectedOutcome ? ValidateNoReport(run, field) : Fail("HXE205", field);

    private static ToolCommandResult? ValidateNoReport(JsonElement run, string field) =>
        run.GetProperty("reportPath").ValueKind == JsonValueKind.Null
        && run.GetProperty("reportSha256").ValueKind == JsonValueKind.Null
        && run.GetProperty("reportPlatform").ValueKind == JsonValueKind.Null
            ? null
            : Fail("HXE206", field + ".report");

    private static async Task<bool> CompletedCleanupAsync(
        string root,
        JsonElement cleanup,
        string revision,
        string version,
        JsonElement pins,
        IReadOnlyCollection<string> persistedRunIds,
        CancellationToken cancellationToken)
    {
        if (!CompletedControl(root, cleanup, "hexalith-module down")
            || !TryArtifact(root, cleanup, "artifactPath", "artifactSha256", out string artifactPath)
            || new FileInfo(artifactPath).Length > _maximumRecordBytes)
        {
            return false;
        }

        byte[] bytes = await File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
        if (!ModuleRunEvidenceArtifactValidator.TryValidate(bytes, out ModuleRunEvidenceArtifactSummary? summary)
            || summary?.Command.StartsWith("hexalith-module down ", StringComparison.Ordinal) != true
            || !string.Equals(cleanup.GetProperty("command").GetString(), "dotnet tool run " + summary.Command, StringComparison.Ordinal)
            || summary.FinalStatus != "completed"
            || summary.ExitCode != 0)
        {
            return false;
        }

        using JsonDocument evidence = JsonDocument.Parse(bytes);
        return BoundToRevision(evidence.RootElement, revision, version, pins)
            && persistedRunIds.Contains(evidence.RootElement.GetProperty("runId").GetString(), StringComparer.Ordinal);
    }

    private static string? DeclaredNativePlatform(string root, JsonElement invocation)
    {
        if (!TryArtifact(root, invocation, "fixturePath", "fixtureHash", out string fixturePath)
            || new FileInfo(fixturePath).Length > _maximumRecordBytes)
        {
            return null;
        }

        using JsonDocument fixture = JsonDocument.Parse(File.ReadAllBytes(fixturePath));
        return fixture.RootElement.ValueKind == JsonValueKind.Object
            && fixture.RootElement.TryGetProperty("nativeTests", out JsonElement nativeTests)
            && TryString(nativeTests, "platform", out string platform)
            && platform is PersistedProfileNativeTests.VsTest or PersistedProfileNativeTests.MicrosoftTestingPlatform
                ? platform
                : null;
    }

    private static bool PackageIdentityMatches(string path, string id, string version)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(path);
            ZipArchiveEntry[] nuspecs = [.. archive.Entries.Where(entry =>
                !entry.FullName.Contains('/', StringComparison.Ordinal)
                && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))];
            if (nuspecs.Length != 1 || nuspecs[0].Length > _maximumRecordBytes)
            {
                return false;
            }

            using Stream stream = nuspecs[0].Open();
            XElement? metadata = XDocument.Load(stream, LoadOptions.None).Root?.Elements()
                .FirstOrDefault(element => element.Name.LocalName == "metadata");
            return string.Equals(MetadataValue(metadata, "id"), id, StringComparison.Ordinal)
                && string.Equals(MetadataValue(metadata, "version"), version, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException or XmlException)
        {
            return false;
        }
    }

    private static string? MetadataValue(XElement? metadata, string name) =>
        metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == name)?.Value;

    private static bool StringArrayEquals(JsonElement element, string name, IReadOnlyList<string> expected) =>
        element.TryGetProperty(name, out JsonElement array)
        && array.ValueKind == JsonValueKind.Array
        && array.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String)
        && array.EnumerateArray().Select(value => value.GetString()).SequenceEqual(expected, StringComparer.Ordinal);

    private static bool BoundToRevision(JsonElement artifact, string revision, string version, JsonElement pins)
    {
        JsonElement environment = artifact.GetProperty("environment");
        JsonElement platform = artifact.GetProperty("topology").GetProperty("platform");
        return StringEquals(environment, "repositoryRevision", revision)
            && StringEquals(environment, "repositoryDirtyMarker", "clean")
            && StringEquals(environment, "toolVersion", version + "+" + revision)
            && platform.ValueKind == JsonValueKind.Object
            && SamePins(platform, pins);
    }

    private static bool TryPackageVersion(string repositoryRoot, JsonElement packages, out string version)
    {
        version = string.Empty;
        if (packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() != _packageIds.Length)
        {
            return false;
        }

        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (JsonElement package in packages.EnumerateArray())
        {
            if (!ExactFields(package, "id", "version", "feed", "nupkgPath", "nupkgSha256", "snupkgPath", "snupkgSha256")
                || !TryString(package, "id", out string id)
                || !TryString(package, "version", out string packageVersion)
                || !ids.Add(id)
                || (version.Length > 0 && !string.Equals(version, packageVersion, StringComparison.Ordinal))
                || !StringEquals(package, "feed", packageVersion.Contains('-', StringComparison.Ordinal) ? "github-packages" : "nuget.org")
                || !TryArtifact(repositoryRoot, package, "nupkgPath", "nupkgSha256", out string nupkg)
                || !TryArtifact(repositoryRoot, package, "snupkgPath", "snupkgSha256", out string snupkg)
                || !string.Equals(Path.GetFileName(nupkg), id + "." + packageVersion + ".nupkg", StringComparison.Ordinal)
                || !string.Equals(Path.GetFileName(snupkg), id + "." + packageVersion + ".snupkg", StringComparison.Ordinal)
                || !PackageIdentityMatches(nupkg, id, packageVersion)
                || !PackageIdentityMatches(snupkg, id, packageVersion))
            {
                return false;
            }

            version = packageVersion;
        }

        return ids.SetEquals(_packageIds);
    }

    private static bool ValidApprovals(JsonElement approvals, string revision, DateTimeOffset latestRunCompletion)
    {
        if (approvals.ValueKind != JsonValueKind.Array || approvals.GetArrayLength() != _requiredRoles.Length)
        {
            return false;
        }

        HashSet<string> roles = new(StringComparer.Ordinal);
        foreach (JsonElement approval in approvals.EnumerateArray())
        {
            if (!ExactFields(approval, "role", "name", "acceptedAtUtc", "sourceRevision")
                || !TryString(approval, "role", out string role)
                || !roles.Add(role)
                || !TryString(approval, "name", out _)
                || !TryString(approval, "acceptedAtUtc", out string acceptedAt)
                || !DateTimeOffset.TryParseExact(acceptedAt, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset acceptedAtUtc)
                || acceptedAtUtc < latestRunCompletion
                || acceptedAtUtc > DateTimeOffset.UtcNow
                || !StringEquals(approval, "sourceRevision", revision))
            {
                return false;
            }
        }

        return roles.SetEquals(_requiredRoles);
    }

    private static bool CompletedControl(string root, JsonElement control, string commandFragment) =>
        ExactFields(control, "status", "command", "artifactPath", "artifactSha256")
        && StringEquals(control, "status", "passed")
        && TryString(control, "command", out string command)
        && command.Contains(commandFragment, StringComparison.Ordinal)
        && TryArtifact(root, control, "artifactPath", "artifactSha256", out _);

    private static bool ExactFields(JsonElement element, params string[] fields) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(fields.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static ToolCommandResult Fail(string ruleId, string field)
    {
        ToolDiagnostic diagnostic = new(
            ruleId,
            ToolPhase.Evidence,
            ToolFailureCategory.EvidencePolicy,
            MessageFor(ruleId),
            field,
            HintFor(ruleId));
        return new ToolCommandResult(
            "failed",
            ToolOutcome.Passed().Fail(ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ruleId, ToolExitCode.EvidenceSchemaOrPolicy),
            [diagnostic]);
    }

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXE200" => "The acceptance record is unreadable, malformed, duplicated, or carries credential material.",
        "HXE201" => "The acceptance record schema or field set is unsupported.",
        "HXE202" => "The acceptance record is not accepted at an exact revision with the supported platform pins.",
        "HXE203" => "The acceptance record fixture manifest identity does not match current bytes.",
        "HXE204" => "The acceptance record does not bind exactly the two lockstep tool packages and their hashes.",
        "HXE205" => "An acceptance run is missing or inconsistent with its module-run evidence.",
        "HXE206" => "A persisted run is not bound to a passing native test report with matching counts and hash.",
        "HXE207" => "The acceptance record lacks a completed cleanup or rollback control.",
        "HXE208" => "The acceptance record lacks the three dated named-role approvals of the exact revision.",
        _ => "The acceptance record is incomplete or inconsistent.",
    };

    private static string HintFor(string ruleId) => ruleId switch
    {
        "HXE202" => "Record acceptance only after every stage passes at one clean, exact revision.",
        "HXE205" => "Cite clean packaged run evidence whose command, status, exit code, manifest, and revision match the run.",
        "HXE206" => "Retain the native TRX report beside its evidence and cite the hash recorded in the evidence.",
        "HXE208" => "Obtain Builds Owner, Platform Owner, and Test Architect approval of the exact revision.",
        _ => "Correct the acceptance record so every bound artifact matches its declared hash.",
    };

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character => char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsHex(string value, int length) => value.Length == length && value.All(char.IsAsciiHexDigit);

    private static bool NoDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            return element.EnumerateObject().All(property => names.Add(property.Name) && NoDuplicateProperties(property.Value));
        }

        return element.ValueKind != JsonValueKind.Array || element.EnumerateArray().All(NoDuplicateProperties);
    }

    private static bool NoSecretMetadata(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => !ManifestSecretDetector.ContainsSecret(element.GetString()!)
            && !ManifestPathValidator.ContainsPlaceholder(element.GetString()!),
        JsonValueKind.Object => element.EnumerateObject().All(property => NoSecretMetadata(property.Value)),
        JsonValueKind.Array => element.EnumerateArray().All(NoSecretMetadata),
        JsonValueKind.Null or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number => true,
        JsonValueKind.Undefined => false,
        _ => false,
    };

    private static bool SamePins(JsonElement actual, JsonElement expected) =>
        StringEquals(actual, "eventStoreVersion", expected.GetProperty("eventStoreVersion").GetString()!)
        && StringEquals(actual, "daprRuntimeVersion", expected.GetProperty("daprRuntimeVersion").GetString()!)
        && StringEquals(actual, "daprSdkVersion", expected.GetProperty("daprSdkVersion").GetString()!)
        && StringEquals(actual, "frontComposerVersion", expected.GetProperty("frontComposerVersion").GetString()!);

    private static bool StringEquals(JsonElement element, string name, string expected) =>
        TryString(element, name, out string actual) && string.Equals(actual, expected, StringComparison.Ordinal);

    private static bool TryArtifact(string root, JsonElement owner, string pathField, string hashField, out string resolved)
    {
        resolved = string.Empty;
        if (!TryString(owner, pathField, out string path)
            || !TryString(owner, hashField, out string hash)
            || !IsHex(hash, 64)
            || path.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(path)
            || path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..")
            || !RepositoryPathResolver.TryResolveExistingFile(root, path, out resolved))
        {
            return false;
        }

        using FileStream stream = File.OpenRead(resolved);
        return string.Equals(Convert.ToHexString(SHA256.HashData(stream)), hash, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryInt(JsonElement element, string name, out int value)
    {
        value = default;
        return element.TryGetProperty(name, out JsonElement property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static bool TryString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out JsonElement property) || property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString()!;
        return !string.IsNullOrWhiteSpace(value);
    }
}