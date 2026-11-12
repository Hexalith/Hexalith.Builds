[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $CatalogPath = '',

    # Catalog conditions may select versions by the actual consuming project identity.
    # This is evaluated in a clean probe, without importing consumer build files.
    [string] $ConsumerProjectName = '',

    [switch] $EmitEvaluation,

    [Parameter(DontShow = $true)]
    [ValidateRange(1, 300)]
    [int] $EvaluationTimeoutSeconds = 60,

    [Parameter(DontShow = $true)]
    [string] $EvaluatorScriptPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$nugetVersionPattern = '^(0|[1-9]\d*)(?:\.(0|[1-9]\d*)){0,3}(?:-(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9]\d*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$'

function Stop-Validation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    [Console]::Error.WriteLine("Central package version validation failed: $Message")
    exit 1
}

function Resolve-ExistingFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    try {
        $resolved = Resolve-Path -LiteralPath $Path -ErrorAction Stop
    }
    catch {
        Stop-Validation "$Description was not found: $Path"
    }

    if (-not (Test-Path -LiteralPath $resolved.ProviderPath -PathType Leaf)) {
        Stop-Validation "$Description is not a file: $Path"
    }

    return $resolved.ProviderPath
}

function Read-StrictUtf8BomFile {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Path
    )

    try {
        $bytes = [IO.File]::ReadAllBytes($Path)
    }
    catch {
        Stop-Validation "central catalog bytes could not be read. $($_.Exception.GetBaseException().Message)"
    }

    if (
        $bytes.Length -lt 3 -or
        $bytes[0] -ne 0xEF -or
        $bytes[1] -ne 0xBB -or
        $bytes[2] -ne 0xBF
    ) {
        Stop-Validation 'central catalog must begin with the UTF-8 BOM bytes EF BB BF.'
    }

    try {
        return [Text.UTF8Encoding]::new($false, $true).GetString($bytes, 3, $bytes.Length - 3)
    }
    catch {
        Stop-Validation "central catalog is not valid strict UTF-8. $($_.Exception.GetBaseException().Message)"
    }
}

function Invoke-CatalogEvaluation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedCatalogPath,

        [AllowEmptyString()]
        [string] $ResolvedEvaluatorScriptPath
    )

    $probeDirectory = ''
    $evaluationPath = $ResolvedCatalogPath
    if (-not [string]::IsNullOrWhiteSpace($ConsumerProjectName)) {
        if ($ConsumerProjectName -notmatch '^[A-Za-z0-9_.-]+$' -or $ConsumerProjectName -in @('.', '..')) {
            Stop-Validation 'consumer project name must be a simple MSBuild project identity.'
        }

        $probeDirectory = Join-Path ([IO.Path]::GetTempPath()) "central-catalog-$([Guid]::NewGuid().ToString('N'))"
        [void] [IO.Directory]::CreateDirectory($probeDirectory)
        $evaluationPath = Join-Path $probeDirectory "$ConsumerProjectName.proj"
        $escapedCatalog = [Security.SecurityElement]::Escape($ResolvedCatalogPath)
        [IO.File]::WriteAllText($evaluationPath, "<Project><Import Project=""$escapedCatalog"" /></Project>")
    }

    # Keep the evaluator inside this authenticated script's source closure. Readers
    # drain concurrently with fixed memory limits; diagnostics never become JSON.
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Hexalith.Builds.CentralCatalog
{
    /// <summary>Captures evaluator channels with bounded storage and deadlines.</summary>
    public static class ProcessRunner
    {
        /// <summary>Runs one exact executable and returns exit status and separate channels.</summary>
        public static string[] Run(string executable, string[] arguments, int timeoutMilliseconds)
        {
            using (var process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(executable)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = new UTF8Encoding(false, true),
                    StandardErrorEncoding = new UTF8Encoding(false, true),
                    CreateNoWindow = true,
                };
                foreach (string argument in arguments)
                {
                    process.StartInfo.ArgumentList.Add(argument);
                }

                if (!process.Start())
                {
                    throw new IOException("The evaluator did not start.");
                }

                try
                {
                    Task<Tuple<string, bool>> stdout = ReadAsync(process.StandardOutput, 8 * 1024 * 1024, false);
                    Task<Tuple<string, bool>> stderr = ReadAsync(process.StandardError, 16 * 1024, true);
                    if (!process.WaitForExit(timeoutMilliseconds))
                    {
                        throw new TimeoutException("The evaluator exceeded its process deadline.");
                    }

                    if (!Task.WhenAll(stdout, stderr).Wait(5000))
                    {
                        throw new TimeoutException("The evaluator streams did not drain within 5 seconds.");
                    }

                    if (stdout.Result.Item2)
                    {
                        throw new InvalidDataException("The evaluator stdout exceeded the 8,388,608-character limit.");
                    }

                    return new[] { process.ExitCode.ToString(), stdout.Result.Item1, stderr.Result.Item1 };
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill(true);
                        if (!process.WaitForExit(5000))
                        {
                            throw new IOException("Evaluator process-tree exit could not be confirmed within 5 seconds.");
                        }
                    }
                }
            }
        }

        private static async Task<Tuple<string, bool>> ReadAsync(StreamReader reader, int limit, bool tail)
        {
            var result = new StringBuilder();
            var buffer = new char[4096];
            bool truncated = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                if (tail)
                {
                    result.Append(buffer, 0, count);
                    if (result.Length > limit)
                    {
                        result.Remove(0, result.Length - limit);
                        truncated = true;
                    }
                }
                else
                {
                    int retained = Math.Min(count, limit - result.Length);
                    result.Append(buffer, 0, retained);
                    truncated |= retained != count;
                }
            }

            return Tuple.Create(result.ToString(), truncated);
        }
    }
}
'@
    try {
        if ([string]::IsNullOrWhiteSpace($ResolvedEvaluatorScriptPath)) {
            $executable = 'dotnet'
            $arguments = @('msbuild', $evaluationPath, '-nologo', '-getItem:PackageVersion')
        }
        else {
            $executable = $pwshExecutable
            $arguments = @('-NoLogo', '-NoProfile', '-File', $ResolvedEvaluatorScriptPath, $ResolvedCatalogPath)
        }

        $captured = [Hexalith.Builds.CentralCatalog.ProcessRunner]::Run(
            $executable, [string[]] $arguments, $EvaluationTimeoutSeconds * 1000)
        if ([int] $captured[0] -ne 0) {
            $stdoutTail = $captured[1].Substring([Math]::Max(0, $captured[1].Length - 16384))
            Stop-Validation "catalog evaluation exited with code $($captured[0]). stdout tail:`n$stdoutTail`nstderr tail:`n$($captured[2])"
        }

        return $captured[1]
    }
    catch {
        Stop-Validation "catalog evaluation could not complete. $($_.Exception.GetBaseException().Message)"
    }
    finally {
        if (-not [string]::IsNullOrWhiteSpace($probeDirectory)) {
            [IO.Directory]::Delete($probeDirectory, $true)
        }
    }
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
}

$resolvedCatalogPath = Resolve-ExistingFile -Path $CatalogPath -Description 'Central package catalog'
$resolvedEvaluatorScriptPath = ''
if (-not [string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
    $resolvedEvaluatorScriptPath = Resolve-ExistingFile -Path $EvaluatorScriptPath -Description 'Evaluator script'
}

try {
    [xml] $catalogXml = Read-StrictUtf8BomFile -Path $resolvedCatalogPath
}
catch {
    Stop-Validation "central catalog source is malformed XML. $($_.Exception.GetBaseException().Message)"
}

$sourcePackageVersions = @($catalogXml.SelectNodes("//*[local-name()='PackageVersion']"))
if ($sourcePackageVersions.Count -eq 0) {
    Stop-Validation 'central catalog source contains no PackageVersion entries.'
}

$sourceByIdentity = @{}
foreach ($sourcePackageVersion in $sourcePackageVersions) {
    $sourceIdentity = [string] $sourcePackageVersion.GetAttribute('Include')
    if ([string]::IsNullOrWhiteSpace($sourceIdentity)) {
        Stop-Validation 'A source PackageVersion entry must have a nonblank Include identity.'
    }

    $sourceIdentity = $sourceIdentity.Trim()
    if ($sourceByIdentity.ContainsKey($sourceIdentity)) {
        Stop-Validation "source contains duplicate package identity '$sourceIdentity' (case-insensitive)."
    }

    $sourceVersion = [string] $sourcePackageVersion.GetAttribute('Version')
    if ([string]::IsNullOrWhiteSpace($sourceVersion)) {
        $sourceVersionNode = $sourcePackageVersion.SelectSingleNode("*[local-name()='Version']")
        if ($null -ne $sourceVersionNode) {
            $sourceVersion = [string] $sourceVersionNode.InnerText
        }
    }

    $sourceByIdentity[$sourceIdentity] = $sourceVersion.Trim()
}

$evaluationText = Invoke-CatalogEvaluation `
    -ResolvedCatalogPath $resolvedCatalogPath `
    -ResolvedEvaluatorScriptPath $resolvedEvaluatorScriptPath

try {
    $evaluation = $evaluationText | ConvertFrom-Json -ErrorAction Stop
}
catch {
    Stop-Validation "catalog evaluation returned malformed JSON. $($_.Exception.GetBaseException().Message)"
}

if (
    $null -eq $evaluation -or
    $evaluation.PSObject.Properties.Name -notcontains 'Items' -or
    $null -eq $evaluation.Items -or
    $evaluation.Items.PSObject.Properties.Name -notcontains 'PackageVersion'
) {
    Stop-Validation 'catalog evaluation did not return an Items.PackageVersion collection.'
}

$packageVersions = @($evaluation.Items.PackageVersion)
if ($packageVersions.Count -eq 0) {
    Stop-Validation 'catalog evaluation returned no PackageVersion entries.'
}

$failures = [System.Collections.Generic.List[string]]::new()
$evaluatedByIdentity = @{}
foreach ($packageVersion in $packageVersions) {
    $identity = if (
        $null -ne $packageVersion -and
        $packageVersion.PSObject.Properties.Name -contains 'Identity'
    ) {
        [string] $packageVersion.Identity
    }
    else {
        ''
    }

    $version = if (
        $null -ne $packageVersion -and
        $packageVersion.PSObject.Properties.Name -contains 'Version'
    ) {
        [string] $packageVersion.Version
    }
    else {
        ''
    }

    $identity = $identity.Trim()
    $version = $version.Trim()
    if ([string]::IsNullOrWhiteSpace($identity)) {
        $failures.Add('A PackageVersion entry has a blank package identity.')
        continue
    }

    if ($evaluatedByIdentity.ContainsKey($identity)) {
        $failures.Add("Evaluated catalog contains duplicate package identity '$identity' (case-insensitive).")
        continue
    }

    $evaluatedByIdentity[$identity] = $version

    if ([string]::IsNullOrWhiteSpace($version)) {
        $failures.Add("Package '$identity' has a blank version.")
        continue
    }

    if ($version -match '^[vV]\d') {
        $failures.Add("Package '$identity' uses tag-prefixed version '$version'; use the NuGet version without the v prefix.")
        continue
    }

    if ($version -match '[\$%@]\(') {
        $failures.Add("Package '$identity' has unresolved MSBuild expression '$version'.")
        continue
    }

    if ($version -cnotmatch $nugetVersionPattern) {
        $failures.Add("Package '$identity' has malformed NuGet/SemVer version '$version'.")
    }
}

foreach ($sourceEntry in $sourceByIdentity.GetEnumerator()) {
    if (-not $evaluatedByIdentity.ContainsKey($sourceEntry.Key)) {
        $failures.Add("Source package '$($sourceEntry.Key)' is missing from the evaluated catalog.")
        continue
    }

    $sourceVersion = [string] $sourceEntry.Value
    $evaluatedVersion = [string] $evaluatedByIdentity[$sourceEntry.Key]
    if (
        -not [string]::IsNullOrWhiteSpace($sourceVersion) -and
        $sourceVersion -notmatch '[\$%@]\(' -and
        $sourceVersion -cne $evaluatedVersion
    ) {
        $failures.Add(
            "Package '$($sourceEntry.Key)' source version '$sourceVersion' evaluates to '$evaluatedVersion'."
        )
    }
}

foreach ($evaluatedEntry in $evaluatedByIdentity.GetEnumerator()) {
    if (-not $sourceByIdentity.ContainsKey($evaluatedEntry.Key)) {
        $failures.Add("Evaluated package '$($evaluatedEntry.Key)' has no authoritative source declaration.")
    }
}

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Central package version validation failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

if ($EmitEvaluation) {
    [Console]::Out.WriteLine($evaluationText)
    exit 0
}

[Console]::Out.WriteLine(
    "Central package version validation passed for $($packageVersions.Count) entries in '$resolvedCatalogPath'."
)
