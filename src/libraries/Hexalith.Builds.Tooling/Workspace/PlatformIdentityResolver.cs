// <copyright file="PlatformIdentityResolver.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Workspace;

using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Runtime;

using NuGet.Versioning;

/// <summary>Reads a Platform identity using only local Git objects and refs.</summary>
internal static partial class PlatformIdentityResolver
{
    private const string _platformReference = "references/Hexalith.Platform";
    private const string _buildsReference = "references/Hexalith.Builds";
    private const string _record = "hexalith-platform.json";

    /// <summary>Gets the CLI informational version without build metadata.</summary>
    internal static string ToolVersion
    {
        get
        {
            Assembly? entry = Assembly.GetEntryAssembly();
            Assembly tool = string.Equals(entry?.GetName().Name, "Hexalith.Builds.Module.Cli", StringComparison.Ordinal)
                ? entry! : typeof(PlatformIdentityResolver).Assembly;
            return (tool.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? tool.GetName().Version?.ToString()
                ?? "unavailable").Split('+', 2)[0];
        }
    }

    /// <summary>Creates the no-Git identity finding.</summary>
    /// <param name="root">The standalone root.</param>
    /// <returns>The finding.</returns>
    internal static PlatformIdentityFinding NoGit(string root) => new(
        "HXW007", $"Platform identity is unavailable because '{root}' is not a Git workspace.", root);

    /// <summary>Creates the finding for a Git workspace without a direct Platform reference.</summary>
    /// <param name="root">The active Git root.</param>
    /// <returns>The finding.</returns>
    internal static PlatformIdentityFinding MissingReference(string root) => new(
        "HXW007",
        $"Git workspace '{root}' has no direct '{_platformReference}' reference.",
        Path.Combine(root, _platformReference.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Converts findings into existing result diagnostics.</summary>
    /// <param name="findings">The identity findings.</param>
    /// <param name="warning">Whether the command is in source mode.</param>
    /// <returns>Result diagnostics.</returns>
    internal static IReadOnlyList<ToolDiagnostic> Diagnostics(IEnumerable<PlatformIdentityFinding> findings, bool warning) =>
        [.. findings.Select(finding => new ToolDiagnostic(
            finding.RuleId,
            ToolPhase.Prerequisite,
            warning ? ToolFailureCategory.None : ToolFailureCategory.PrerequisiteUnavailable,
            finding.Message,
            finding.Path,
            finding.Hint)),
        ];

    /// <summary>Resolves the committed identity and all locally observable conditions.</summary>
    /// <param name="root">The active Git root.</param>
    /// <param name="references">Direct reference declarations.</param>
    /// <param name="recorded">Staged gitlinks.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="toolVersionOverride">Optional version seam for tests.</param>
    /// <returns>The identity and all findings.</returns>
    internal static async Task<(PlatformIdentity? Identity, IReadOnlyList<PlatformIdentityFinding> Findings)> ResolveAsync(
        string root,
        IReadOnlyList<string> references,
        IReadOnlyDictionary<string, string> recorded,
        CancellationToken cancellationToken,
        string? toolVersionOverride = null)
    {
        List<PlatformIdentityFinding> findings = [];
        string head = (await GitAsync(root, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false)).Output.Trim();
        bool hasPlatformReference = references.Contains(_platformReference, StringComparer.Ordinal);
        CompositionProcessResult? marker = await TryGitAsync(findings, root, cancellationToken, "ls-tree", "-z", "HEAD", "--", _record).ConfigureAwait(false);
        bool platformRoot = !hasPlatformReference && marker is not null && IsRegularBlob(marker.Output, _record);
        string platformPath = platformRoot ? root : Path.Combine(root, _platformReference.Replace('/', Path.DirectorySeparatorChar));
        string recordPath = Path.Combine(platformPath, _record);
        if (!hasPlatformReference && !platformRoot)
        {
            findings.Add(MissingReference(root));
        }

        string? identityCommit = platformRoot ? head : null;
        if (hasPlatformReference)
        {
            CompositionProcessResult? tree = await TryGitAsync(findings, root, cancellationToken, "ls-tree", "-z", "HEAD", "--", _platformReference).ConfigureAwait(false);
            if (tree is not null)
            {
                identityCommit = TreeGitlink(tree.Output, _platformReference);
                if (identityCommit is null)
                {
                    findings.Add(new PlatformIdentityFinding("HXW007", $"Platform identity gitlink '{_platformReference}' is missing from HEAD of '{root}'.", platformPath));
                }
            }
        }

        string? compositionCommit = platformRoot ? head : recorded.GetValueOrDefault(_platformReference);
        string? checkoutCommit = await TryCheckoutHeadAsync(findings, platformPath, cancellationToken).ConfigureAwait(false);
        if (hasPlatformReference && checkoutCommit is null)
        {
            findings.Add(new PlatformIdentityFinding("HXW007", $"Platform checkout '{platformPath}' is unavailable for identity {identityCommit ?? "unavailable"}.", platformPath));
        }

        if (hasPlatformReference && checkoutCommit is not null && compositionCommit is not null
            && !string.Equals(checkoutCommit, compositionCommit, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PlatformIdentityFinding("HXW008", $"Platform checkout '{platformPath}' is {checkoutCommit}; staged composition gitlink is {compositionCommit}.", platformPath));
        }

        if (compositionCommit is not null && identityCommit is not null
            && !string.Equals(compositionCommit, identityCommit, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PlatformIdentityFinding("HXW008", $"Platform composition gitlink is {compositionCommit}; committed identity is {identityCommit}.", platformPath));
        }

        string? catalogCommit = null;
        string? toolRange = null;
        string? tag = null;
        if (identityCommit is not null && checkoutCommit is not null)
        {
            CompositionProcessResult? recordEntry = await TryGitAsync(findings, platformPath, cancellationToken, "ls-tree", "-z", identityCommit, "--", _record).ConfigureAwait(false);
            if (recordEntry is not null && !IsRegularBlob(recordEntry.Output, _record))
            {
                findings.Add(new PlatformIdentityFinding("HXW007", $"Platform identity record '{recordPath}' at {identityCommit} is not a regular tracked blob.", recordPath));
            }
            else if (recordEntry is not null)
            {
                CompositionProcessResult? record = await TryGitAsync(findings, platformPath, cancellationToken, "show", identityCommit + ":" + _record).ConfigureAwait(false);
                if (record is not null && !TryReadRecord(record.Output, out toolRange, out string? recordError))
                {
                    findings.Add(new PlatformIdentityFinding("HXW007", $"Platform identity record '{recordPath}' at {identityCommit} is invalid: {recordError}.", recordPath));
                }
            }

            CompositionProcessResult? pin = await TryGitAsync(findings, platformPath, cancellationToken, "ls-tree", "-z", identityCommit, "--", _buildsReference).ConfigureAwait(false);
            if (pin is not null)
            {
                catalogCommit = TreeGitlink(pin.Output, _buildsReference);
                if (catalogCommit is null)
                {
                    findings.Add(new PlatformIdentityFinding("HXW007", $"Platform identity {identityCommit} has no Builds catalog pin at '{_buildsReference}'.", Path.Combine(platformPath, _buildsReference)));
                }
            }

            if (!platformRoot)
            {
                CompositionProcessResult? tags = await TryGitAsync(findings, platformPath, cancellationToken, "tag", "--points-at", identityCommit).ConfigureAwait(false);
                if (tags is not null)
                {
                    tag = tags.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(IsReleaseTag)
                        .Order(StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (tag is null)
                    {
                        findings.Add(new PlatformIdentityFinding("HXW011", $"Platform identity {identityCommit} has no local v<SemVer> release tag.", platformPath, "Fetch Platform tags, then retry."));
                    }
                }
            }
        }

        if (!platformRoot && checkoutCommit is not null)
        {
            CompositionProcessResult? status = await TryGitAsync(findings, platformPath, cancellationToken, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false);
            if (status?.Output.Length > 0)
            {
                findings.Add(new PlatformIdentityFinding("HXW011", $"Platform checkout '{platformPath}' has changes.", platformPath));
            }
        }

        string buildsPath = Path.Combine(root, _buildsReference.Replace('/', Path.DirectorySeparatorChar));
        string? buildsStaged = recorded.GetValueOrDefault(_buildsReference);
        string? buildsHead = await TryCheckoutHeadAsync(findings, buildsPath, cancellationToken).ConfigureAwait(false);
        CompositionProcessResult? buildsStatus = buildsHead is null ? null : await TryGitAsync(findings, buildsPath, cancellationToken, "status", "--porcelain", "-z", "--ignore-submodules=all", "--untracked-files=all").ConfigureAwait(false);
        bool buildsDirty = buildsStatus?.Output.Length > 0;
        if (buildsStaged is null || buildsHead is null || buildsDirty
            || (catalogCommit is not null && !string.Equals(buildsStaged, catalogCommit, StringComparison.OrdinalIgnoreCase))
            || !string.Equals(buildsHead, buildsStaged, StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new PlatformIdentityFinding("HXW009", $"Builds catalog '{buildsPath}' is staged {buildsStaged ?? "absent"}, checked out {buildsHead ?? "absent"}{(buildsDirty ? ", dirty" : string.Empty)}; Platform {identityCommit ?? "unavailable"} pins {catalogCommit ?? "unavailable"}.", buildsPath));
        }

        if (catalogCommit is not null && buildsHead is not null)
        {
            _ = await TryGitAsync(findings, buildsPath, cancellationToken, "show", catalogCommit + ":Props/Directory.Packages.props").ConfigureAwait(false);
        }

        string version = toolVersionOverride ?? ToolVersion;
        if (toolRange is null || !VersionRange.TryParse(toolRange, out VersionRange? range)
            || !NuGetVersion.TryParse(version, out NuGetVersion? parsed) || !range.Satisfies(parsed))
        {
            findings.Add(new PlatformIdentityFinding("HXW010", $"Tool version {version} is outside Platform range {toolRange ?? "unavailable"} at '{recordPath}'.", recordPath));
        }

        return identityCommit is null || compositionCommit is null
            ? (null, findings)
            : (new PlatformIdentity(identityCommit, compositionCommit, checkoutCommit, platformPath, tag, catalogCommit, toolRange, recordPath, platformRoot), findings);
    }

    /// <summary>Checks that an ls-tree entry is a regular tracked blob at the expected path.</summary>
    /// <param name="output">The NUL-terminated ls-tree output.</param>
    /// <param name="path">The expected tree path.</param>
    /// <returns>Whether the entry is a regular blob.</returns>
    internal static bool IsRegularBlob(string output, string path)
    {
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length != 1)
        {
            return false;
        }

        int separator = records[0].IndexOf('\t', StringComparison.Ordinal);
        string[] fields = separator < 0 ? [] : records[0][..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 3 && fields[0] is "100644" or "100755" && fields[1] == "blob"
            && separator >= 0 && records[0][(separator + 1)..] == path;
    }

    private static async Task<CompositionProcessResult?> TryGitAsync(List<PlatformIdentityFinding> findings, string path, CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            return await GitAsync(path, cancellationToken, arguments, allowMissing: false).ConfigureAwait(false);
        }
        catch (WorkspaceMappingException exception)
        {
            findings.Add(new PlatformIdentityFinding("HXW007", exception.Message, path));
            return null;
        }
    }

    private static async Task<string?> TryCheckoutHeadAsync(List<PlatformIdentityFinding> findings, string path, CancellationToken cancellationToken)
    {
        try
        {
            return await CheckoutHeadAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (WorkspaceMappingException exception)
        {
            findings.Add(new PlatformIdentityFinding("HXW007", exception.Message, path));
            return null;
        }
    }

    private static bool TryReadRecord(string text, out string? range, out string? error)
    {
        range = null;
        error = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(text);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out JsonElement schema)
                || schema.ValueKind != JsonValueKind.Number || !IsSchemaVersionOne(schema)
                || root.EnumerateObject().Any(property => property.Name is not ("schemaVersion" or "toolVersionRange"))
                || root.EnumerateObject().Select(property => property.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count())
            {
                error = "expected schemaVersion 1 and only known fields";
                return false;
            }

            if (root.TryGetProperty("toolVersionRange", out JsonElement value))
            {
                if (value.ValueKind != JsonValueKind.String || !VersionRange.TryParse(value.GetString() ?? string.Empty, out _))
                {
                    error = "toolVersionRange is not a NuGet version range";
                    return false;
                }

                range = value.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            error = "invalid JSON";
            return false;
        }
    }

    private static bool IsSchemaVersionOne(JsonElement schema)
    {
        string number = schema.GetRawText();
        if (number.Length > 0 && number[0] == '-')
        {
            return false;
        }

        int exponentSeparator = number.IndexOfAny(['e', 'E']);
        string mantissa = exponentSeparator < 0 ? number : number[..exponentSeparator];
        string exponentText = exponentSeparator < 0 ? "0" : number[(exponentSeparator + 1)..];
        if (!BigInteger.TryParse(exponentText, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out BigInteger exponent))
        {
            return false;
        }

        int decimalPoint = mantissa.AsSpan().IndexOf('.');
        int fractionalDigits = decimalPoint < 0 ? 0 : mantissa.Length - decimalPoint - 1;
        string digits = mantissa.Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0');
        int trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        return digits.TrimEnd('0') == "1" && exponent - fractionalDigits + trailingZeros == BigInteger.Zero;
    }

    private static string? TreeGitlink(string output, string path)
    {
        string[] records = output.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (records.Length != 1)
        {
            return null;
        }

        int separator = records[0].IndexOf('\t', StringComparison.Ordinal);
        string[] fields = separator < 0 ? [] : records[0][..separator].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length == 3 && fields[0] == "160000" && fields[1] == "commit"
            && fields[2].Length is 40 or 64 && fields[2].All(Uri.IsHexDigit)
            && records[0][(separator + 1)..] == path ? fields[2] : null;
    }

    private static async Task<string?> CheckoutHeadAsync(string path, CancellationToken cancellationToken)
    {
        return !SubmoduleInitializer.IsInitialized(path)
            ? null
            : (await GitAsync(path, cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false)).Output.Trim();
    }

    private static bool IsReleaseTag(string candidate)
    {
        if (!ReleaseTag().IsMatch(candidate))
        {
            return false;
        }

        int metadataStart = candidate.IndexOf('+', StringComparison.Ordinal);
        string version = metadataStart < 0 ? candidate : candidate[..metadataStart];
        int prereleaseStart = version.IndexOf('-', StringComparison.Ordinal);
        if (prereleaseStart < 0)
        {
            return true;
        }

        string prerelease = version[(prereleaseStart + 1)..];
        return prerelease.Split('.').All(identifier => identifier.Length < 2 || identifier[0] != '0' || !identifier.All(char.IsAsciiDigit));
    }

#pragma warning disable CA1068 // This private wrapper follows the GitWorkspaceProcess argument order.
    private static Task<CompositionProcessResult> GitAsync(string path, CancellationToken cancellationToken, string command, params string[] arguments)
        => GitAsync(path, cancellationToken, [command, .. arguments], false);

    private static async Task<CompositionProcessResult> GitAsync(string path, CancellationToken cancellationToken, string[] arguments, bool allowMissing)
    {
        CompositionProcessResult result = await GitWorkspaceProcess.RunAsync(path, cancellationToken, arguments).ConfigureAwait(false);
        return !result.Started || result.TimedOut || result.OutputTruncated || (!allowMissing && result.ExitCode != 0)
            ? throw new WorkspaceMappingException(new ToolDiagnostic(
                "HXW007",
                ToolPhase.Prerequisite,
                ToolFailureCategory.PrerequisiteUnavailable,
                $"Local Git inspection failed at '{path}': {string.Join(' ', arguments)}.",
                path))
            : result;
    }
#pragma warning restore CA1068

    [GeneratedRegex("^v(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)\\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$")]
    private static partial Regex ReleaseTag();
}