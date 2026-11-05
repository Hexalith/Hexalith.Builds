#!/usr/bin/env pwsh
# Internal final semantic-release prepare action, after notes and before tagging.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Version)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}
$packageDirectory = Join-Path (Join-Path $PSScriptRoot '..') "artifacts/g4-tool-packages/$Version"
$inventory = Assert-ReleaseEligibleInventory -InventoryPath (Join-Path $packageDirectory 'g4-tool-package-inventory.json') -Version $Version
& python3 (Join-Path $PSScriptRoot 'archive-g4-qualification-evidence.py') --package-directory $packageDirectory --kind release --verify
if ($LASTEXITCODE -ne 0) { throw 'Final pre-tag qualification evidence carrier failed validation.' }
Assert-CurrentReleaseSource -Qualification $inventory.qualification
