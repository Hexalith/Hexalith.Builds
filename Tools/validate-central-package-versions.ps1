[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string] $CatalogPath = '',

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

function Invoke-CatalogEvaluation {
    param(
        [Parameter(Mandatory = $true)]
        [string] $ResolvedCatalogPath,

        [AllowEmptyString()]
        [string] $ResolvedEvaluatorScriptPath
    )

    try {
        if ([string]::IsNullOrWhiteSpace($ResolvedEvaluatorScriptPath)) {
            $output = @(
                & dotnet msbuild $ResolvedCatalogPath -nologo -getItem:PackageVersion 2>&1
            )
        }
        else {
            $output = @(
                & $pwshExecutable -NoLogo -NoProfile -File $ResolvedEvaluatorScriptPath $ResolvedCatalogPath 2>&1
            )
        }
    }
    catch {
        Stop-Validation "catalog evaluation could not start. $($_.Exception.GetBaseException().Message)"
    }

    if ($LASTEXITCODE -ne 0) {
        $diagnostics = [string]::Join("`n", @($output | ForEach-Object { [string] $_ })).Trim()
        if ([string]::IsNullOrWhiteSpace($diagnostics)) {
            $diagnostics = 'The evaluator produced no diagnostic output.'
        }

        Stop-Validation "catalog evaluation exited with code $LASTEXITCODE. $diagnostics"
    }

    return [string]::Join("`n", @($output | ForEach-Object { [string] $_ }))
}

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $PSScriptRoot '../Props/Directory.Packages.props'
}

$resolvedCatalogPath = Resolve-ExistingFile -Path $CatalogPath -Description 'Central package catalog'
$resolvedEvaluatorScriptPath = ''
if (-not [string]::IsNullOrWhiteSpace($EvaluatorScriptPath)) {
    $resolvedEvaluatorScriptPath = Resolve-ExistingFile -Path $EvaluatorScriptPath -Description 'Evaluator script'
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

if ($failures.Count -gt 0) {
    [Console]::Error.WriteLine("Central package version validation failed with $($failures.Count) error(s):")
    foreach ($failure in $failures) {
        [Console]::Error.WriteLine("- $failure")
    }

    exit 1
}

[Console]::Out.WriteLine(
    "Central package version validation passed for $($packageVersions.Count) entries in '$resolvedCatalogPath'."
)
