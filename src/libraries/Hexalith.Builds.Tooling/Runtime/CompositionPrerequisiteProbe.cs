// <copyright file="CompositionPrerequisiteProbe.cs" company="ITANEO">
// Copyright (c) ITANEO (https://www.itaneo.com). All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

namespace Hexalith.Builds.Tooling.Runtime;

using System.Text.RegularExpressions;

using Hexalith.Builds.Tooling.Diagnostics;

/// <summary>
/// Verifies the local composition prerequisites by executing them before any resource starts.
/// </summary>
/// <remarks>
/// Docker must answer a server-version query. <c>HEXALITH_DAPR_HOME</c> must contain the Dapr CLI at
/// <c>tools/dapr</c> reporting the selected CLI and runtime versions, and <c>.dapr/bin</c> must contain
/// <c>daprd</c>, <c>placement</c>, and <c>scheduler</c> that each report the selected runtime version when executed.
/// </remarks>
public static partial class CompositionPrerequisiteProbe
{
    /// <summary>
    /// The environment variable naming the verified, isolated Dapr home.
    /// </summary>
    public const string DaprHomeVariable = "HEXALITH_DAPR_HOME";

    private static readonly TimeSpan _probeTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Probes every prerequisite and returns stable <c>HXR01x</c> diagnostics for each unavailable one.
    /// </summary>
    /// <param name="daprHome">The Dapr home, or null to read <see cref="DaprHomeVariable"/>.</param>
    /// <param name="dockerCommand">The Docker CLI command.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The prerequisite decision.</returns>
    public static async Task<CompositionPrerequisiteResult> ProbeAsync(
        string? daprHome,
        string dockerCommand,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dockerCommand);

        List<ToolDiagnostic> diagnostics = [];
        CompositionProcessResult docker = await CompositionProcess.RunAsync(
            CompositionProcess.CreateStartInfo(dockerCommand, ["version", "--format", "{{.Server.Version}}"], null, null),
            _probeTimeout,
            cancellationToken).ConfigureAwait(false);
        if (!docker.Started || docker.ExitCode != 0 || string.IsNullOrWhiteSpace(docker.Output))
        {
            diagnostics.Add(Unavailable(
                "HXR010",
                "The container runtime required for run-scoped resources is unavailable.",
                "docker",
                "Start Docker and make its CLI available before retrying."));
        }

        string? home = string.IsNullOrWhiteSpace(daprHome)
            ? Environment.GetEnvironmentVariable(DaprHomeVariable)
            : daprHome;
        string? verifiedHome = null;
        if (string.IsNullOrWhiteSpace(home) || !Path.IsPathFullyQualified(home) || !Directory.Exists(home))
        {
            diagnostics.Add(Unavailable(
                "HXR011",
                "The isolated Dapr home is not configured.",
                DaprHomeVariable,
                "Set HEXALITH_DAPR_HOME to an absolute directory prepared with the selected Dapr CLI and runtime."));
        }
        else
        {
            verifiedHome = Path.GetFullPath(home);
            if (!await VerifyDaprAsync(verifiedHome, diagnostics, cancellationToken).ConfigureAwait(false))
            {
                verifiedHome = null;
            }
        }

        return new CompositionPrerequisiteResult(
            diagnostics.Count == 0 ? verifiedHome : null,
            [.. diagnostics.OrderBy(diagnostic => diagnostic.RuleId, StringComparer.Ordinal)]);
    }

    /// <summary>
    /// Parses the CLI and runtime versions reported by <c>dapr --version</c>.
    /// </summary>
    /// <param name="output">The command output.</param>
    /// <returns>The CLI and runtime versions, each null when absent.</returns>
    public static (string? Cli, string? Runtime) ParseDaprVersions(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        Match cli = CliVersionRegex().Match(output);
        Match runtime = RuntimeVersionRegex().Match(output);
        return (cli.Success ? cli.Groups["v"].Value : null, runtime.Success ? runtime.Groups["v"].Value : null);
    }

    /// <summary>
    /// Parses the version announced by a Dapr control-plane service start line.
    /// </summary>
    /// <param name="line">The start line.</param>
    /// <returns>The version, or null.</returns>
    public static string? ParseServiceVersion(string? line)
    {
        if (line is null)
        {
            return null;
        }

        Match match = ServiceVersionRegex().Match(line);
        return match.Success ? match.Groups["v"].Value : null;
    }

    private static async Task<bool> VerifyDaprAsync(string home, List<ToolDiagnostic> diagnostics, CancellationToken cancellationToken)
    {
        string cli = Path.Combine(home, "tools", OperatingSystem.IsWindows() ? "dapr.exe" : "dapr");
        Dictionary<string, string> environment = new(StringComparer.Ordinal) { [CompositionEnvironment.DaprRuntimePath] = home };
        CompositionProcessResult cliResult = File.Exists(cli)
            ? await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(cli, ["--version"], home, environment),
                _probeTimeout,
                cancellationToken).ConfigureAwait(false)
            : new CompositionProcessResult(false, -1, string.Empty, false);
        (string? cliVersion, string? runtimeVersion) = ParseDaprVersions(cliResult.Output);
        if (!cliResult.Started || cliResult.ExitCode != 0
            || !string.Equals(cliVersion, CompositionToolchainPins.DaprCliVersion, StringComparison.Ordinal)
            || !string.Equals(runtimeVersion, CompositionToolchainPins.DaprRuntimeVersion, StringComparison.Ordinal))
        {
            diagnostics.Add(Unavailable(
                "HXR012",
                "The isolated Dapr CLI does not report the selected CLI and runtime versions.",
                "tools/dapr",
                "Install Dapr CLI 1.18.0 with runtime 1.18.2 in the isolated Dapr home."));
            return false;
        }

        string bin = Path.Combine(home, ".dapr", "bin");
        string daprd = Path.Combine(bin, OperatingSystem.IsWindows() ? "daprd.exe" : "daprd");
        CompositionProcessResult daprdResult = File.Exists(daprd)
            ? await CompositionProcess.RunAsync(
                CompositionProcess.CreateStartInfo(daprd, ["--version"], home, null),
                _probeTimeout,
                cancellationToken).ConfigureAwait(false)
            : new CompositionProcessResult(false, -1, string.Empty, false);
        bool verified = daprdResult.Started && daprdResult.ExitCode == 0
            && string.Equals(daprdResult.Output.Trim(), CompositionToolchainPins.DaprRuntimeVersion, StringComparison.Ordinal);
        verified = verified && await VerifyServiceAsync(home, bin, "placement", [], cancellationToken).ConfigureAwait(false);
        verified = verified && await VerifyServiceAsync(
            home,
            bin,
            "scheduler",
            ["--etcd-client-port", "0", "--etcd-data-dir", Path.Combine(Path.GetTempPath(), "hexalith-g4-probe-" + Guid.NewGuid().ToString("N"))],
            cancellationToken).ConfigureAwait(false);
        if (!verified)
        {
            diagnostics.Add(Unavailable(
                "HXR013",
                "The isolated Dapr runtime binaries do not report the selected runtime version.",
                ".dapr/bin",
                "Initialize the isolated Dapr home with runtime 1.18.2 (daprd, placement, scheduler)."));
        }

        return verified;
    }

    private static async Task<bool> VerifyServiceAsync(
        string home,
        string bin,
        string service,
        IReadOnlyList<string> extraArguments,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(bin, OperatingSystem.IsWindows() ? service + ".exe" : service);
        if (!File.Exists(path))
        {
            return false;
        }

        string workingDirectory = Path.Combine(Path.GetTempPath(), "hexalith-g4-probe-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(workingDirectory);
        try
        {
            string? line = await CompositionProcess.ReadFirstLineContainingAsync(
                CompositionProcess.CreateStartInfo(
                    path,
                    ["--port", "0", "--healthz-port", "0", "--enable-metrics=false", .. extraArguments],
                    workingDirectory,
                    new Dictionary<string, string>(StringComparer.Ordinal) { [CompositionEnvironment.DaprRuntimePath] = home }),
                "-- version ",
                _probeTimeout,
                cancellationToken).ConfigureAwait(false);
            return string.Equals(ParseServiceVersion(line), CompositionToolchainPins.DaprRuntimeVersion, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(workingDirectory);
            foreach (string argument in extraArguments.Where(argument => argument.Contains("hexalith-g4-probe-", StringComparison.Ordinal)))
            {
                TryDeleteDirectory(argument);
            }
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Probe scratch directories are best-effort temporary data.
        }
    }

    private static ToolDiagnostic Unavailable(string ruleId, string message, string field, string hint) =>
        new(ruleId, ToolPhase.Prerequisite, ToolFailureCategory.PrerequisiteUnavailable, message, field, hint);

    [GeneratedRegex(@"CLI version:\s*(?<v>[0-9A-Za-z.\-+]+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CliVersionRegex();

    [GeneratedRegex(@"Runtime version:\s*(?<v>[0-9A-Za-z.\-+]+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex RuntimeVersionRegex();

    [GeneratedRegex(@"-- version (?<v>[0-9A-Za-z.\-+]+)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ServiceVersionRegex();
}