#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/g4-tool-packages/$Version"
}
elseif (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot $OutputDirectory
}

$packageDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

function Get-ExpectedPackageNames {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Tools,

        [Parameter(Mandatory = $true)]
        [string] $PackageVersion
    )

    $names = foreach ($tool in $Tools) {
        "$($tool.Id).$PackageVersion.nupkg"
        "$($tool.Id).$PackageVersion.snupkg"
    }

    return @($names | Sort-Object)
}

$tools = @(
    [pscustomobject]@{
        Id = 'Hexalith.Builds.Module.Cli'
        Project = Join-Path $repositoryRoot 'src/libraries/Hexalith.Builds.Module.Cli/Hexalith.Builds.Module.Cli.csproj'
    },
    [pscustomobject]@{
        Id = 'Hexalith.Builds.Evidence.Cli'
        Project = Join-Path $repositoryRoot 'src/libraries/Hexalith.Builds.Evidence.Cli/Hexalith.Builds.Evidence.Cli.csproj'
    }
)

foreach ($tool in $tools) {
    if (-not (Test-Path -LiteralPath $tool.Project -PathType Leaf)) {
        throw "Approved G-4 tool project '$($tool.Project)' does not exist."
    }
}

if (Test-Path -LiteralPath $packageDirectory) {
    $existingItems = @(Get-ChildItem -LiteralPath $packageDirectory -Force)
    if ($existingItems.Count -gt 0) {
        throw "Package output directory '$packageDirectory' must be empty to prevent stale artifacts from qualifying a release."
    }
}
else {
    $null = New-Item -ItemType Directory -Path $packageDirectory -Force
}

$solutionPath = Join-Path $repositoryRoot 'Hexalith.Builds.slnx'
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "Solution '$solutionPath' does not exist."
}

Invoke-DotNet -Arguments @('restore', $solutionPath)

foreach ($tool in $tools) {
    Write-Host "Packing $($tool.Id) at $Version in Release configuration."
    Invoke-DotNet -Arguments @(
        'pack',
        $tool.Project,
        '--configuration',
        'Release',
        '--no-restore',
        '--output',
        $packageDirectory,
        '-p:GeneratePackageOnBuild=false',
        '-p:IDEBuild=false',
        "-p:Version=$Version",
        "-p:PackageVersion=$Version"
    )
}

$expectedNames = Get-ExpectedPackageNames -Tools $tools -PackageVersion $Version
$actualPackages = @(
    Get-ChildItem -LiteralPath $packageDirectory -File |
        Where-Object { $_.Extension -in @('.nupkg', '.snupkg') } |
        Sort-Object -Property Name
)
$actualNames = @($actualPackages | ForEach-Object Name)
$difference = @(Compare-Object -ReferenceObject $expectedNames -DifferenceObject $actualNames)

if ($difference.Count -gt 0 -or $actualPackages.Count -ne 4) {
    $actualDisplay = if ($actualNames.Count -eq 0) { '<none>' } else { $actualNames -join ', ' }
    throw "Expected exactly the two approved G-4 tool packages with .nupkg and .snupkg artifacts at version '$Version'. Actual artifacts: $actualDisplay."
}

$inventoryPackages = foreach ($tool in $tools) {
    $nupkg = Join-Path $packageDirectory "$($tool.Id).$Version.nupkg"
    $snupkg = Join-Path $packageDirectory "$($tool.Id).$Version.snupkg"

    [ordered]@{
        id = $tool.Id
        version = $Version
        nupkg = [ordered]@{
            file = [System.IO.Path]::GetFileName($nupkg)
            sha256 = (Get-FileHash -LiteralPath $nupkg -Algorithm SHA256).Hash
            sizeBytes = (Get-Item -LiteralPath $nupkg).Length
        }
        snupkg = [ordered]@{
            file = [System.IO.Path]::GetFileName($snupkg)
            sha256 = (Get-FileHash -LiteralPath $snupkg -Algorithm SHA256).Hash
            sizeBytes = (Get-Item -LiteralPath $snupkg).Length
        }
    }
}

$inventory = [ordered]@{
    schema = 'hexalith.g4-tool-package-inventory.v1'
    version = $Version
    configuration = 'Release'
    packages = @($inventoryPackages)
}
$inventoryPath = Join-Path $packageDirectory 'g4-tool-package-inventory.json'
$inventory | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $inventoryPath -Encoding utf8

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_OUTPUT)) {
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "g4_tool_package_directory=$packageDirectory"
    Add-Content -LiteralPath $env:GITHUB_OUTPUT -Value "g4_tool_package_inventory=$inventoryPath"
}

Write-Host "G-4 tool package inventory recorded at '$inventoryPath'."
