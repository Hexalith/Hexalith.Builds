[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $CatalogPath = '',

    [ValidateRange(1, 300)]
    [int] $EvaluationTimeoutSeconds = 30,

    [ValidateRange(1, 30)]
    [int] $StreamDrainTimeoutSeconds = 5,

    [Parameter(DontShow = $true)]
    [string] $EvaluatorScriptPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$targetVersion = '1.18.4'
$pwshExecutable = Join-Path $PSHOME $(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })
$requiredPackageIds = @(
    'Dapr.Client'
    'Dapr.AspNetCore'
    'Dapr.Actors.AspNetCore'
    'Dapr.Actors.Generators'
    'Dapr.Actors'
    'Dapr.Workflow'
)

function Stop-Validation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Message
    )

    [Console]::Error.WriteLine("Dapr package validation failed: $Message")
    exit 1
}

function Get-OutputTail {
    param(
        [AllowNull()]
        [string] $Text
    )

    if ([string]::IsNullOrWhiteSpace($Text)) {
        return ''
    }

    $normalized = $Text -replace "`e\[[0-9;?]*[ -/]*[@-~]", ''
    $normalized = $normalized.Trim()
    $maximumLength = 4096
    if ($normalized.Length -le $maximumLength) {
        return $normalized
    }

    return "<earlier output omitted>`n$($normalized.Substring($normalized.Length - $maximumLength))"
}

function Get-CompletedTaskText {
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]] $Task
    )

    if ($Task.Status -eq [System.Threading.Tasks.TaskStatus]::RanToCompletion) {
        return [string] $Task.Result
    }

    return ''
}

function Format-EvaluatorDiagnostics {
    param(
        [AllowNull()]
        [string] $StandardOutput,

        [AllowNull()]
        [string] $StandardError
    )

    $parts = [System.Collections.Generic.List[string]]::new()
    $stderrTail = Get-OutputTail -Text $StandardError
    if (-not [string]::IsNullOrWhiteSpace($stderrTail)) {
        $parts.Add("stderr tail:`n$stderrTail")
    }

    $stdoutTail = Get-OutputTail -Text $StandardOutput
    if (-not [string]::IsNullOrWhiteSpace($stdoutTail)) {
        $parts.Add("stdout tail:`n$stdoutTail")
    }

    if ($parts.Count -eq 0) {
        return 'The evaluator produced no diagnostic output.'
    }

    return [string]::Join("`n", $parts)
}

function Wait-ForStreamDrain {
    param(
        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]] $StandardOutputTask,

        [Parameter(Mandatory = $true)]
        [System.Threading.Tasks.Task[string]] $StandardErrorTask,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutMilliseconds
    )

    $combinedTask = [System.Threading.Tasks.Task]::WhenAll(
        [System.Threading.Tasks.Task[]] @($StandardOutputTask, $StandardErrorTask)
    )

    try {
        return $combinedTask.Wait($TimeoutMilliseconds)
    }
    catch {
        Stop-Validation "reading evaluator output failed. $($_.Exception.GetBaseException().Message)"
    }
}

function Stop-ProcessTreeBounded {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutMilliseconds
    )

    $stateCheckError = ''
    try {
        if ($Process.HasExited) {
            return [pscustomobject] @{
                ExitConfirmed = $true
                KillAttempted = $false
                KillError = ''
                WaitError = ''
            }
        }
    }
    catch {
        $stateCheckError = "checking evaluator state failed: $($_.Exception.GetBaseException().Message)"
    }

    $killError = ''
    try {
        $Process.Kill($true)
    }
    catch {
        $killError = $_.Exception.GetBaseException().Message
    }

    $exitConfirmed = $false
    $waitError = $stateCheckError
    try {
        $exitConfirmed = $Process.WaitForExit($TimeoutMilliseconds)
    }
    catch {
        $boundedWaitError = $_.Exception.GetBaseException().Message
        $waitError = if ([string]::IsNullOrWhiteSpace($waitError)) {
            $boundedWaitError
        }
        else {
            "$waitError; waiting for evaluator exit failed: $boundedWaitError"
        }
    }

    return [pscustomobject] @{
        ExitConfirmed = $exitConfirmed
        KillAttempted = $true
        KillError = $killError
        WaitError = $waitError
    }
}

function Format-ProcessCleanupStatus {
    param(
        [Parameter(Mandatory = $true)]
        [pscustomobject] $CleanupResult,

        [Parameter(Mandatory = $true)]
        [int] $TimeoutMilliseconds
    )

    $details = [System.Collections.Generic.List[string]]::new()
    if ($CleanupResult.KillAttempted) {
        $details.Add('Process-tree termination was attempted.')
    }
    else {
        $details.Add('The evaluator had already exited before process-tree termination was needed.')
    }

    if (-not [string]::IsNullOrWhiteSpace($CleanupResult.KillError)) {
        $details.Add("The termination request failed: $($CleanupResult.KillError)")
    }

    if (-not [string]::IsNullOrWhiteSpace($CleanupResult.WaitError)) {
        $details.Add("The bounded exit check failed: $($CleanupResult.WaitError)")
    }

    if ($CleanupResult.ExitConfirmed) {
        $details.Add('Evaluator exit was confirmed.')
    }
    else {
        $details.Add("Evaluator exit was not confirmed within the $TimeoutMilliseconds-millisecond cleanup deadline.")
    }

    return [string]::Join(' ', $details)
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

function Invoke-PackageVersionEvaluation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedCatalogPath,

        [AllowEmptyString()]
        [string] $ResolvedEvaluatorScriptPath
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.CreateNoWindow = $true

    if ([string]::IsNullOrWhiteSpace($ResolvedEvaluatorScriptPath)) {
        $startInfo.FileName = 'dotnet'
        $startInfo.ArgumentList.Add('msbuild')
        $startInfo.ArgumentList.Add($ResolvedCatalogPath)
        $startInfo.ArgumentList.Add('-nologo')
        $startInfo.ArgumentList.Add('-getItem:PackageVersion')
    }
    else {
        $startInfo.FileName = $pwshExecutable
        $startInfo.ArgumentList.Add('-NoLogo')
        $startInfo.ArgumentList.Add('-NoProfile')
        $startInfo.ArgumentList.Add('-File')
        $startInfo.ArgumentList.Add($ResolvedEvaluatorScriptPath)
        $startInfo.ArgumentList.Add($ResolvedCatalogPath)
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $stdoutTask = $null
    $stderrTask = $null
    $processStarted = $false

    try {
        try {
            if (-not $process.Start()) {
                Stop-Validation 'the package evaluator did not start.'
            }

            $processStarted = $true
        }
        catch {
            Stop-Validation "the package evaluator could not start. $($_.Exception.GetBaseException().Message)"
        }

        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $evaluationTimeoutMilliseconds = $EvaluationTimeoutSeconds * 1000
        $drainTimeoutMilliseconds = $StreamDrainTimeoutSeconds * 1000

        if (-not $process.WaitForExit($evaluationTimeoutMilliseconds)) {
            $cleanupResult = Stop-ProcessTreeBounded `
                -Process $process `
                -TimeoutMilliseconds $drainTimeoutMilliseconds
            $drained = Wait-ForStreamDrain `
                -StandardOutputTask $stdoutTask `
                -StandardErrorTask $stderrTask `
                -TimeoutMilliseconds $drainTimeoutMilliseconds
            $stdout = Get-CompletedTaskText -Task $stdoutTask
            $stderr = Get-CompletedTaskText -Task $stderrTask
            $diagnostics = Format-EvaluatorDiagnostics -StandardOutput $stdout -StandardError $stderr
            $cleanupStatus = Format-ProcessCleanupStatus `
                -CleanupResult $cleanupResult `
                -TimeoutMilliseconds $drainTimeoutMilliseconds
            $drainStatus = if ($drained) { 'Redirected streams drained after termination.' } else { 'Redirected streams did not drain before the bounded cleanup deadline.' }
            Stop-Validation "the package evaluator timed out after $EvaluationTimeoutSeconds seconds. $cleanupStatus $drainStatus`n$diagnostics"
        }

        $exitCode = $process.ExitCode
        $drained = Wait-ForStreamDrain `
            -StandardOutputTask $stdoutTask `
            -StandardErrorTask $stderrTask `
            -TimeoutMilliseconds $drainTimeoutMilliseconds
        if (-not $drained) {
            $stdout = Get-CompletedTaskText -Task $stdoutTask
            $stderr = Get-CompletedTaskText -Task $stderrTask
            $diagnostics = Format-EvaluatorDiagnostics -StandardOutput $stdout -StandardError $stderr
            Stop-Validation "redirected evaluator output did not drain within $StreamDrainTimeoutSeconds seconds after process exit. No unbounded wait was performed.`n$diagnostics"
        }

        $stdout = Get-CompletedTaskText -Task $stdoutTask
        $stderr = Get-CompletedTaskText -Task $stderrTask
        if ($exitCode -ne 0) {
            $diagnostics = Format-EvaluatorDiagnostics -StandardOutput $stdout -StandardError $stderr
            Stop-Validation "the package evaluator exited with code $exitCode.`n$diagnostics"
        }

        return [pscustomobject] @{
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    finally {
        if ($processStarted) {
            $processStillRunning = $true
            try {
                $processStillRunning = -not $process.HasExited
            }
            catch {
                # State is unconfirmed, so bounded best-effort cleanup is still appropriate.
            }

            if ($processStillRunning) {
                $null = Stop-ProcessTreeBounded `
                    -Process $process `
                    -TimeoutMilliseconds ($StreamDrainTimeoutSeconds * 1000)
            }
        }

        $process.Dispose()
    }
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
}

$resolvedCatalogPath = Resolve-ExistingFile -Path $CatalogPath -Description 'Package catalog'
$resolvedEvaluatorScriptPath = ''
if (-not [string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
    $resolvedEvaluatorScriptPath = Resolve-ExistingFile -Path $EvaluatorScriptPath -Description 'Test evaluator script'
}

$evaluationResult = Invoke-PackageVersionEvaluation `
    -ResolvedCatalogPath $resolvedCatalogPath `
    -ResolvedEvaluatorScriptPath $resolvedEvaluatorScriptPath
$json = $evaluationResult.StandardOutput
$evaluationDiagnostics = Format-EvaluatorDiagnostics `
    -StandardOutput $evaluationResult.StandardOutput `
    -StandardError $evaluationResult.StandardError

if ([string]::IsNullOrWhiteSpace($json)) {
    Stop-Validation "the evaluator produced no JSON on stdout.`n$evaluationDiagnostics"
}

try {
    $evaluation = $json | ConvertFrom-Json -Depth 100 -ErrorAction Stop
}
catch {
    Stop-Validation "the evaluator produced malformed JSON on stdout. $($_.Exception.GetBaseException().Message)`n$evaluationDiagnostics"
}

if ($null -eq $evaluation) {
    Stop-Validation 'the evaluator JSON root must be an object.'
}

$itemsProperty = $evaluation.PSObject.Properties['Items']
if ($null -eq $itemsProperty -or $null -eq $itemsProperty.Value) {
    Stop-Validation "the evaluator JSON schema requires an 'Items' object."
}

$packageVersionsProperty = $itemsProperty.Value.PSObject.Properties['PackageVersion']
if ($null -eq $packageVersionsProperty -or -not ($packageVersionsProperty.Value -is [System.Array])) {
    Stop-Validation "the evaluator JSON schema requires 'Items.PackageVersion' to be an array."
}

$validatedItems = [System.Collections.Generic.List[object]]::new()
$itemIndex = 0
foreach ($item in $packageVersionsProperty.Value) {
    if ($null -eq $item) {
        Stop-Validation "the evaluator JSON schema requires PackageVersion item $itemIndex to be an object."
    }

    $identityProperty = $item.PSObject.Properties['Identity']
    if (
        $null -eq $identityProperty -or
        -not ($identityProperty.Value -is [string]) -or
        [string]::IsNullOrWhiteSpace([string] $identityProperty.Value)
    ) {
        Stop-Validation "the evaluator JSON schema requires PackageVersion item $itemIndex Identity to be a nonblank string."
    }

    $versionProperty = $item.PSObject.Properties['Version']
    if (
        $null -eq $versionProperty -or
        -not ($versionProperty.Value -is [string]) -or
        [string]::IsNullOrWhiteSpace([string] $versionProperty.Value)
    ) {
        Stop-Validation "the evaluator JSON schema requires PackageVersion item $itemIndex Version to be a nonblank string."
    }

    $validatedItems.Add([pscustomobject] @{
        Identity = [string] $identityProperty.Value
        Version = [string] $versionProperty.Value
    })
    $itemIndex++
}

$bareDaprItems = @(
    $validatedItems | Where-Object {
        [string]::Equals($_.Identity, 'Dapr', [StringComparison]::OrdinalIgnoreCase)
    }
)
if ($bareDaprItems.Count -gt 0) {
    Stop-Validation "the invalid bare package ID 'Dapr' is present in the evaluated catalog. Delete it instead of assigning it a version."
}

$daprItems = @(
    $validatedItems | Where-Object {
        $_.Identity.StartsWith('Dapr.', [StringComparison]::OrdinalIgnoreCase)
    }
)
if ($daprItems.Count -eq 0) {
    Stop-Validation 'no Dapr.* PackageVersion items were evaluated.'
}

$seenIdentities = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
$duplicateIdentities = [System.Collections.Generic.List[string]]::new()
foreach ($item in $daprItems) {
    if ($seenIdentities.ContainsKey($item.Identity)) {
        $duplicateIdentities.Add("$($seenIdentities[$item.Identity]) / $($item.Identity)")
    }
    else {
        $seenIdentities.Add($item.Identity, $item.Identity)
    }
}

if ($duplicateIdentities.Count -gt 0) {
    Stop-Validation "duplicate Dapr.* PackageVersion identities are not allowed because NuGet IDs are case-insensitive: $([string]::Join(', ', $duplicateIdentities))."
}

$evaluatedIdentities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($item in $daprItems) {
    $null = $evaluatedIdentities.Add($item.Identity)
}

$missingPackageIds = @(
    $requiredPackageIds | Where-Object { -not $evaluatedIdentities.Contains($_) }
)
if ($missingPackageIds.Count -gt 0) {
    Stop-Validation "required shared Dapr SDK package IDs are missing: $([string]::Join(', ', $missingPackageIds))."
}

$mismatchedItems = @(
    $daprItems | Where-Object {
        -not [string]::Equals($_.Version, $targetVersion, [StringComparison]::Ordinal)
    }
)
if ($mismatchedItems.Count -gt 0) {
    $mismatches = $mismatchedItems | ForEach-Object { "$($_.Identity)=$($_.Version)" }
    Stop-Validation "every evaluated Dapr.* package must use $targetVersion; mismatches: $([string]::Join(', ', $mismatches))."
}

Write-Output "Dapr package validation passed: $($daprItems.Count) unique Dapr.* package IDs use $targetVersion and all required shared SDK IDs are present."
