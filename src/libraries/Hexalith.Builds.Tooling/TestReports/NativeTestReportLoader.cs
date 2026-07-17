// <copyright file="NativeTestReportLoader.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.TestReports;

using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Loads native TRX reports emitted by VSTest and Microsoft Testing Platform without replacing their outcomes.
/// </summary>
public static class NativeTestReportLoader
{
    /// <summary>
    /// Loads a native test report and rejects missing, invalid, zero-match, all-skipped, and failed results.
    /// </summary>
    /// <param name="reportPath">The report path.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A normalized report or a stable non-passing diagnostic.</returns>
    public static async Task<NativeTestReportLoadResult> LoadAsync(string reportPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportPath);

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(reportPath);
        }
        catch (ArgumentException)
        {
            return Failed("HXT001", "report");
        }
        catch (NotSupportedException)
        {
            return Failed("HXT001", "report");
        }

        if (!File.Exists(fullPath))
        {
            return Failed("HXT001", "report");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Failed("HXT001", "report");
        }
        catch (UnauthorizedAccessException)
        {
            return Failed("HXT001", "report");
        }

        XDocument document;
        try
        {
            MemoryStream stream = new(bytes, writable: false);
            await using (stream.ConfigureAwait(false))
            {
                document = XDocument.Load(stream, LoadOptions.None);
            }
        }
        catch (XmlException)
        {
            return Failed("HXT002", "report");
        }

        XElement? counters = document.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Counters", StringComparison.Ordinal));
        return counters is null || !TryReadCounters(counters, out int total, out int passed, out int failed, out int skipped)
            ? Failed("HXT002", "counters")
            : CreateResult(fullPath, bytes, total, passed, failed, skipped);
    }

    private static NativeTestReportLoadResult CreateResult(
        string fullPath,
        byte[] bytes,
        int total,
        int passed,
        int failed,
        int skipped) =>
        (total, passed, failed, skipped) switch
        {
            (0, _, _, _) => Failed("HXT003", "total"),
            (_, _, > 0, _) => Failed("HXT005", "failed"),
            (_, 0, _, _) => Failed("HXT004", "skipped"),
            (_, _, _, var skippedCount) when skippedCount >= total => Failed("HXT004", "skipped"),
            _ when passed + skipped != total => Failed("HXT002", "counters"),
            _ => new NativeTestReportLoadResult(
                new NativeTestReport(
                    Path.GetFileName(fullPath),
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    total,
                    passed,
                    failed,
                    skipped),
                null),
        };

    private static NativeTestReportLoadResult Failed(string ruleId, string field) => new(
        null,
        new ToolDiagnostic(
            ruleId,
            ToolPhase.Test,
            ToolFailureCategory.ProductOrTest,
            MessageFor(ruleId),
            field,
            HintFor(ruleId)));

    private static string HintFor(string ruleId) => ruleId switch
    {
        "HXT001" => "Retain the native TRX report emitted by the selected test platform.",
        "HXT002" => "Supply a readable native TRX report with complete counters.",
        "HXT003" => "Use a filter that matches at least one test.",
        "HXT004" => "Execute at least one non-skipped matching test.",
        "HXT005" => "Resolve failed native test results before recording a pass.",
        _ => "Correct the native test report before recording a pass.",
    };

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXT001" => "A native test report is missing or unreadable.",
        "HXT002" => "A native test report is invalid or has inconsistent counters.",
        "HXT003" => "The selected test filter matched zero tests.",
        "HXT004" => "All matching tests were skipped or not executed.",
        "HXT005" => "The native test report contains failed test results.",
        _ => "The native test report cannot represent a pass.",
    };

    private static bool TryReadCounters(
        XElement counters,
        out int total,
        out int passed,
        out int failed,
        out int skipped)
    {
        total = 0;
        passed = 0;
        failed = 0;
        skipped = 0;
        if (!TryReadCounter(counters, "total", true, out total) ||
            !TryReadCounter(counters, "passed", true, out passed) ||
            !TryReadCounter(counters, "failed", true, out int failedCount) ||
            !TryReadCounter(counters, "error", false, out int errors) ||
            !TryReadCounter(counters, "timeout", false, out int timeouts) ||
            !TryReadCounter(counters, "aborted", false, out int aborted) ||
            !TryReadCounter(counters, "notExecuted", false, out int notExecuted) ||
            !TryReadCounter(counters, "inconclusive", false, out int inconclusive))
        {
            return false;
        }

        failed = failedCount + errors + timeouts + aborted;
        skipped = notExecuted + inconclusive;

        if (total < 0 || passed < 0 || failed < 0 || skipped < 0 || passed + failed + skipped > total)
        {
            return false;
        }

        skipped += total - passed - failed - skipped;
        return true;
    }

    private static bool TryReadCounter(XElement counters, string name, bool required, out int value)
    {
        value = 0;
        XAttribute? attribute = counters.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal));
        return attribute is null
            ? !required
            : int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}