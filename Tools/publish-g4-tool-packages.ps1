#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $PackageDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot "artifacts/g4-tool-packages/$Version"
}
elseif (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot $PackageDirectory
}

$packageDirectoryPath = [System.IO.Path]::GetFullPath($PackageDirectory)
$inventoryPath = Join-Path $packageDirectoryPath 'g4-tool-package-inventory.json'

function Get-RedactedDotNetArguments {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    $redactNextArgument = $false
    $redactedArguments = foreach ($argument in $Arguments) {
        if ($redactNextArgument) {
            $redactNextArgument = $false
            '<redacted>'
            continue
        }

        if ($argument -eq '--api-key') {
            $redactNextArgument = $true
        }

        $argument
    }

    return @($redactedArguments)
}

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        $redactedArguments = Get-RedactedDotNetArguments -Arguments $Arguments
        throw "dotnet $($redactedArguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

if (-not (Test-Path -LiteralPath $inventoryPath -PathType Leaf)) {
    throw "Package inventory '$inventoryPath' is required before publication. Run build-g4-tool-packages.ps1 first."
}

$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if ($inventory.schema -cne 'hexalith.g4-tool-package-inventory.v1' -or $inventory.version -cne $Version -or $inventory.configuration -cne 'Release') {
    throw "Package inventory '$inventoryPath' does not describe the requested Release version '$Version'."
}

$approvedIds = @('Hexalith.Builds.Evidence.Cli', 'Hexalith.Builds.Module.Cli')
$inventoryPackages = @($inventory.packages | Sort-Object -Property id)
$inventoryIds = @($inventoryPackages | ForEach-Object { [string]$_.id } | Sort-Object)
if ($inventoryPackages.Count -ne 2 -or (($inventoryIds -join '|') -cne ($approvedIds -join '|'))) {
    throw 'Package inventory must contain exactly the two approved G-4 tool package IDs.'
}

$expectedArtifactNames = [System.Collections.Generic.List[string]]::new()
foreach ($package in $inventoryPackages) {
    foreach ($artifactName in @($package.nupkg.file, $package.snupkg.file)) {
        $expectedArtifactNames.Add([string]$artifactName)
    }

    foreach ($artifact in @($package.nupkg, $package.snupkg)) {
        $artifactPath = Join-Path $packageDirectoryPath $artifact.file
        if (-not (Test-Path -LiteralPath $artifactPath -PathType Leaf)) {
            throw "Inventory artifact '$artifactPath' is missing."
        }

        $actualHash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash
        if ($actualHash -cne $artifact.sha256) {
            throw "SHA-256 mismatch for '$artifactPath'; publication is blocked."
        }
    }
}

$actualArtifactNames = @(
    Get-ChildItem -LiteralPath $packageDirectoryPath -File |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
        ForEach-Object Name |
        Sort-Object
)
$artifactDifference = @(Compare-Object -ReferenceObject @($expectedArtifactNames | Sort-Object) -DifferenceObject $actualArtifactNames)
if ($artifactDifference.Count -gt 0 -or $actualArtifactNames.Count -ne 4) {
    throw 'The package directory contains unexpected, missing, or stale NuGet artifacts.'
}

if ($Version.Contains('-', [System.StringComparison]::Ordinal)) {
    $source = 'https://nuget.pkg.github.com/Hexalith/index.json'
    $apiKey = $env:GITHUB_TOKEN
    $channel = 'GitHub Packages prerelease'
}
else {
    $source = 'https://api.nuget.org/v3/index.json'
    $apiKey = $env:NUGET_API_KEY
    $channel = 'NuGet.org stable release'
}

if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw "A package API key is required to publish the $channel."
}

Write-Host "Publishing the verified G-4 tool package inventory to $channel."
foreach ($artifactName in @($expectedArtifactNames | Sort-Object)) {
    $artifactPath = Join-Path $packageDirectoryPath $artifactName
    Invoke-DotNet -Arguments @('nuget', 'push', $artifactPath, '--api-key', $apiKey, '--source', $source)
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_STEP_SUMMARY)) {
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '## G-4 tool package inventory'
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ''
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("Published version: ``{0}`` ({1})" -f $Version, $channel)
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ''
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '| Package | Artifact | SHA-256 |'
    Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value '| --- | --- | --- |'
    foreach ($package in $inventoryPackages) {
        foreach ($artifact in @($package.nupkg, $package.snupkg)) {
            Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value ("| {0} | {1} | ``{2}`` |" -f $package.id, $artifact.file, $artifact.sha256)
        }
    }
}
