#!/usr/bin/env pwsh
#Requires -Version 7.6

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $PackagePath,
    [Parameter(Mandatory = $true)][string] $Version,
    [Parameter(Mandatory = $true)][string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'G4PackageQualification.functions.ps1')

$PackagePath = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) { throw 'Packaged seed evidence output must be new.' }
$scratch = Join-Path ([IO.Path]::GetTempPath()) "hexalith-g4-seed-probe-$([Guid]::NewGuid().ToString('N'))"
$null = [IO.Directory]::CreateDirectory($scratch)
$process = $null
try {
    $project = Join-Path $PSScriptRoot 'PackagedSeedProbe/PackagedSeedProbe.csproj'
    & dotnet build $project --configuration Debug --output $scratch -p:GeneratePackageOnBuild=false
    if ($LASTEXITCODE -ne 0) { throw "Packaged seed probe build failed with exit code $LASTEXITCODE." }
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $scratch
    $start.UseShellExecute = $false
    foreach ($argument in @((Join-Path $scratch 'PackagedSeedProbe.dll'), $PackagePath, $Version, $OutputPath)) {
        $start.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process.WaitForExit(120000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'Packaged seed probe timed out; its process tree was killed.'
    }
    if ($process.ExitCode -ne 0) { throw "Packaged seed subscription probe failed with exit code $($process.ExitCode)." }
    $null = Assert-PackagedSeedSubscriptionEvidence -FilePath $OutputPath -PackagePath $PackagePath -Version $Version
    Write-Host 'Packaged seed configured/declarative HTTP and receiver metadata checks passed.'
}
finally {
    if ($null -ne $process) { $process.Dispose() }
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
