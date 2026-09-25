// <copyright file="NativeTestExecutor.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text;
using System.Xml;
using System.Xml.Linq;

using Hexalith.Builds.Tooling.Diagnostics;
using Hexalith.Builds.Tooling.Manifest;
using Hexalith.Builds.Tooling.TestReports;

/// <summary>
/// Runs a product-owned native test project against a ready run and binds only its native TRX result.
/// </summary>
internal static class NativeTestExecutor
{
    /// <summary>The native report file name requested from either platform.</summary>
    private const string _reportFileName = "native.trx";

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Executes the declared native tests with the run handoff and evaluates their native report.
    /// </summary>
    /// <param name="session">The ready run session.</param>
    /// <param name="tests">The declared native tests.</param>
    /// <param name="manifestPath">The manifest path that anchors repository-relative paths.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A result that passes only for a zero native exit and a passing native report.</returns>
    public static async Task<NativeTestExecutionResult> ExecuteAsync(
        CompositionRunSession session,
        PersistedProfileNativeTests tests,
        string manifestPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        string root = ManifestPathValidator.FindRepositoryRoot(Path.GetFullPath(manifestPath));
        List<ToolDiagnostic> pathDiagnostics = [];
        string? project = ManifestPathValidator.ValidateExistingFile(tests.Project, root, "profile.nativeTests.project", pathDiagnostics);
        if (project is null)
        {
            return Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        string results = Path.Combine(session.Plan.Workspace, "native-tests");
        CompositionWorkspace.CreatePrivateDirectory(results);
        string token = CompositionTokenFactory.Create(
            session.SigningKey,
            new CompositionTokenRequest(
                "g4-native-tests",
                [session.Plan.TenantNamespace],
                [.. session.Plan.Modules.Select(module => module.Domain)],
                ["command:submit", "query:read"],
                false,
                _timeout + TimeSpan.FromMinutes(5)),
            DateTimeOffset.UtcNow);
        CompositionProcessResult process = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo(
                "dotnet",
                CreateArguments(tests.Platform, project, results),
                Path.GetDirectoryName(project),
                NativeTestHandoff.Create(session.Plan, session.Readiness, token)),
            _timeout,
            cancellationToken).ConfigureAwait(false);
        string reportPath = Path.Combine(results, _reportFileName);
        string[] handoffSecrets = [token, session.SigningKey];
        byte[]? reportBytes = File.Exists(reportPath)
            ? await File.ReadAllBytesAsync(reportPath, cancellationToken).ConfigureAwait(false)
            : null;

        // A secret-bearing report stays raw so Evaluate rejects it; any other report is redacted before it is parsed, hashed, or retained.
        if (reportBytes is not null
            && !ContainsRetainedSecret(reportBytes, handoffSecrets)
            && NativeTestReportRedactor.Redact(reportBytes) is { } redacted)
        {
            reportBytes = redacted;
            await File.WriteAllBytesAsync(reportPath, reportBytes, cancellationToken).ConfigureAwait(false);
        }

        NativeTestReportLoadResult report = await NativeTestReportLoader.LoadAsync(reportPath, cancellationToken).ConfigureAwait(false);
        return Evaluate(process, report, reportBytes, handoffSecrets);
    }

    /// <summary>
    /// Creates the native <c>dotnet test</c> arguments for a platform.
    /// </summary>
    /// <param name="platform">The declared platform.</param>
    /// <param name="projectPath">The test project path.</param>
    /// <param name="resultsDirectory">The private results directory.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The platform is not supported.</exception>
    internal static IReadOnlyList<string> CreateArguments(string platform, string projectPath, string resultsDirectory) => platform switch
    {
        PersistedProfileNativeTests.VsTest =>
            ["test", projectPath, "--logger", "trx;LogFileName=" + _reportFileName, "--results-directory", resultsDirectory],
        PersistedProfileNativeTests.MicrosoftTestingPlatform =>
            ["test", "--project", projectPath, "--results-directory", resultsDirectory, "--report-xunit-trx", "--report-xunit-trx-filename", _reportFileName],
        _ => throw new ArgumentOutOfRangeException(nameof(platform), platform, "Unsupported native test platform."),
    };

    /// <summary>
    /// Gets the repository-relative path of the report retained beside an evidence artifact.
    /// </summary>
    /// <param name="evidencePath">The repository-relative evidence path ending in <c>.json</c>.</param>
    /// <param name="platform">The native test platform.</param>
    /// <returns>The retained report path.</returns>
    internal static string RetainedReportPath(string evidencePath, string platform) =>
        evidencePath[..^".json".Length] + "." + platform + ".trx";

    /// <summary>
    /// Maps a native process result and its report to a fail-closed outcome.
    /// </summary>
    /// <param name="process">The native process result.</param>
    /// <param name="report">The loaded native report.</param>
    /// <param name="reportBytes">The raw report bytes, when present.</param>
    /// <param name="handoffSecrets">Values handed to the tests that must never be retained.</param>
    /// <returns>The outcome; report data is present only when the step passed.</returns>
    internal static NativeTestExecutionResult Evaluate(
        CompositionProcessResult process,
        NativeTestReportLoadResult report,
        byte[]? reportBytes,
        IReadOnlyList<string> handoffSecrets)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(handoffSecrets);

        if (!process.Started)
        {
            return Failed("HXR030", ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, ToolExitCode.PrerequisiteUnavailable);
        }

        if (reportBytes is not null && ContainsRetainedSecret(reportBytes, handoffSecrets))
        {
            return Failed("HXT008", ToolPhase.Evidence, ToolFailureCategory.EvidencePolicy, ToolExitCode.EvidenceSchemaOrPolicy);
        }

        if (process.TimedOut)
        {
            return Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest);
        }

        if (!report.IsValid)
        {
            ToolDiagnostic diagnostic = report.Diagnostic!;
            return new NativeTestExecutionResult(
                new ToolCommandResult(
                    "failed",
                    ToolOutcome.Passed().Fail(diagnostic.Phase, diagnostic.Category, diagnostic.RuleId, ToolExitCode.ProductOrTest),
                    [diagnostic]),
                null,
                null);
        }

        return process.ExitCode != 0 || reportBytes is null
            ? Failed("HXT007", ToolPhase.Test, ToolFailureCategory.ProductOrTest, ToolExitCode.ProductOrTest)
            : new NativeTestExecutionResult(
                new ToolCommandResult(
                    "completed",
                    ToolOutcome.Passed(),
                    [new ToolDiagnostic("HXI005", ToolPhase.Test, ToolFailureCategory.None, "The native test report passed.", "nativeTests")]),
                report.Report,
                reportBytes);
    }

    private static bool ContainsRetainedSecret(byte[] reportBytes, IReadOnlyList<string> handoffSecrets)
    {
        string text = Encoding.UTF8.GetString(reportBytes);
        return handoffSecrets.Any(secret => !string.IsNullOrEmpty(secret) && text.Contains(secret, StringComparison.Ordinal))
            || ReportValues(text).Any(ManifestSecretDetector.ContainsSecret);
    }

    /// <summary>
    /// Splits a report into the individual XML values the credential detector can anchor on.
    /// </summary>
    /// <param name="text">The report text.</param>
    /// <returns>Every attribute and text value, or the whole text when it is not well-formed XML.</returns>
    private static IEnumerable<string> ReportValues(string text)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(text, LoadOptions.None);
        }
        catch (XmlException)
        {
            return [text];
        }

        return document.Descendants()
            .SelectMany(element => element.Attributes().Select(attribute => attribute.Value)
                .Concat(element.Nodes().OfType<XText>().Select(node => node.Value)));
    }

    private static NativeTestExecutionResult Failed(string ruleId, ToolPhase phase, ToolFailureCategory category, ToolExitCode exitCode)
    {
        ToolDiagnostic diagnostic = new(ruleId, phase, category, MessageFor(ruleId), "nativeTests", HintFor(ruleId));
        return new NativeTestExecutionResult(
            new ToolCommandResult(
                exitCode == ToolExitCode.PrerequisiteUnavailable ? "unavailable" : "failed",
                ToolOutcome.Passed().Fail(phase, category, ruleId, exitCode),
                [diagnostic]),
            null,
            null);
    }

    private static string MessageFor(string ruleId) => ruleId switch
    {
        "HXR030" => "The native test process could not be started.",
        "HXT008" => "The native test report contains credential material and was not retained.",
        _ => "The native test process did not complete successfully.",
    };

    private static string HintFor(string ruleId) => ruleId switch
    {
        "HXR030" => "Install the repository-pinned .NET SDK so dotnet test is available.",
        "HXT008" => "Never write handoff tokens or credentials to test output.",
        _ => "Inspect the native test project and its platform configuration locally.",
    };
}