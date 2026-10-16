# Exercise the real pre-tag gate against the actual dirty diagnostic inventory.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../../../Tools/G4PackageQualification.functions.ps1')
$inventoryPath = Join-Path $PSScriptRoot '../../../../artifacts/story117-seed-20261016-corpus-repaired-verified/g4-tool-package-inventory.json'
$inventory = Get-Content -LiteralPath $inventoryPath -Raw | ConvertFrom-Json
if ($inventory.qualification.releaseEligible -ne $false -or $inventory.qualification.sourceTree.clean -ne $false) {
    throw 'Dirty preparation lost its release-ineligibility marker.'
}
$refused = $false
try {
    $null = Assert-ReleaseEligibleInventory -InventoryPath $inventoryPath -Version '4.31.2'
}
catch {
    if (-not $_.Exception.Message.StartsWith("Pre-tag G-4 package qualification is not release eligible for '4.31.2':", [StringComparison]::Ordinal)) { throw }
    $refused = $true
    [Console]::Out.WriteLine($_.Exception.Message)
}
if (-not $refused) { throw 'The dirty diagnostic passed the pre-tag release gate.' }
