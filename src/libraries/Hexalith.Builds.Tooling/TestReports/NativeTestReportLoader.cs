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
        if (counters is null || !TryReadCounters(counters, out int total, out int passed, out int failed, out int skipped))
        {
            return Failed("HXT002", "counters");
        }

        if (total == 0)
        {
            return Failed("HXT003", "total");
        }

        if (failed > 0)
        {
            return Failed("HXT005", "failed");
        }

        if (skipped >= total || passed == 0)
        {
            return Failed("HXT004", "skipped");
        }

        return passed + skipped == total
            ? new NativeTestReportLoadResult(
                new NativeTestReport(
                    Path.GetFileName(fullPath),
                    Convert.ToHexString(SHA256.HashData(bytes)),
                    total,
                    passed,
                    failed,
                    skipped),
                null)
            : Failed("HXT002", "counters");
    }

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
        total = ReadCounter(counters, "total");
        passed = ReadCounter(counters, "passed");
        failed = ReadCounter(counters, "failed")
            + ReadCounter(counters, "error")
            + ReadCounter(counters, "timeout")
            + ReadCounter(counters, "aborted");
        skipped = ReadCounter(counters, "notExecuted") + ReadCounter(counters, "inconclusive");

        if (total < 0 || passed < 0 || failed < 0 || skipped < 0 || passed + failed + skipped > total)
        {
            return false;
        }

        skipped += total - passed - failed - skipped;
        return true;
    }

    private static int ReadCounter(XElement counters, string name)
    {
        XAttribute? attribute = counters.Attributes().FirstOrDefault(attribute =>
            string.Equals(attribute.Name.LocalName, name, StringComparison.Ordinal));
        return attribute is null || !int.TryParse(attribute.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? 0
            : value;
    }
}