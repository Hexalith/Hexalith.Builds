#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

$channel = if ($Version.Contains('-', [StringComparison]::Ordinal)) { 'GitHub Packages prerelease' } else { 'NuGet.org stable release' }
$credential = if ($Version.Contains('-', [StringComparison]::Ordinal)) { $env:GITHUB_TOKEN } else { $env:NUGET_API_KEY }
if ([string]::IsNullOrWhiteSpace($credential)) {
    throw "A package API key is required to publish the $channel before creating a release tag."
}

$packageDirectory = "artifacts/g4-tool-packages/$Version"
& (Join-Path $PSScriptRoot 'test-g4-tool-package-contracts.ps1') -Version $Version `
    -PackageDirectory $packageDirectory -RetainPackageDirectory -RequireControls
if (-not $?) {
    throw "Pre-tag G-4 package qualification failed for '$Version'."
}

$inventoryPath = Join-Path (Join-Path $PSScriptRoot '..') "$packageDirectory/g4-tool-package-inventory.json"
$null = Assert-ReleaseEligibleInventory -InventoryPath $inventoryPath -Version $Version

# Prepare the carrier before tagging. The publisher consumes these same bytes;
# a later publication failure can still retain them through Actions artifacts.
& python3 (Join-Path $PSScriptRoot 'archive-g4-qualification-evidence.py') `
    --package-directory (Split-Path -Parent $inventoryPath) --kind release
if ($LASTEXITCODE -ne 0) {
    throw "Pre-tag G-4 qualification evidence retention failed for '$Version'."
}
