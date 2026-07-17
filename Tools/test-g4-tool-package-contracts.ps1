#!/usr/bin/env pwsh

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string] $Version,

    [string] $PackageDirectory,

    [switch] $SkipSourceValidation,

    [switch] $RequireControls,

    [string] $FixtureRoot = 'test/fixtures'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
    throw "Version '$Version' is not a supported NuGet semantic version."
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-builds-g4-packages-$([System.Guid]::NewGuid().ToString('N'))"
}
elseif (-not [System.IO.Path]::IsPathRooted($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot $PackageDirectory
}

$packageDirectoryPath = [System.IO.Path]::GetFullPath($PackageDirectory)
if (-not [System.IO.Path]::IsPathRooted($FixtureRoot)) {
    $FixtureRoot = Join-Path $repositoryRoot $FixtureRoot
}

$fixtureRootPath = [System.IO.Path]::GetFullPath($FixtureRoot)
$ownsPackageDirectory = -not (Test-Path -LiteralPath $packageDirectoryPath)
$hadNuGetPackages = Test-Path -LiteralPath Env:NUGET_PACKAGES
$previousNuGetPackages = $env:NUGET_PACKAGES
$hadDotNetCliHome = Test-Path -LiteralPath Env:DOTNET_CLI_HOME
$previousDotNetCliHome = $env:DOTNET_CLI_HOME
$consumerEnvironmentConfigured = $false

function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [string] $WorkingDirectory = $repositoryRoot
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        & dotnet @Arguments
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }
}

function Invoke-ToolCommand {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Command,

        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,

        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory
    )

    Push-Location -LiteralPath $WorkingDirectory
    try {
        $output = @(& dotnet tool run $Command @Arguments 2>&1)
        return [pscustomobject]@{
            ExitCode = $LASTEXITCODE
            Output = $output -join [Environment]::NewLine
        }
    }
    finally {
        Pop-Location
    }
}

function Get-RequiredFixture {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string[]] $Extensions,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Description directory '$Directory' is required for packaged-tool qualification."
    }

    $fixtures = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Where-Object {
                $_.Extension -in $Extensions -and
                -not $_.Name.EndsWith('.expected.json', [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object -Property FullName
    )
    if ($fixtures.Count -ne 1) {
        throw "$Description directory '$Directory' must contain exactly one fixture; found $($fixtures.Count)."
    }

    return $fixtures[0]
}

function Get-FixtureExpectationPath {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $Fixture
    )

    $baseNameExpectation = Join-Path $Fixture.DirectoryName "$($Fixture.BaseName).expected.json"
    if (Test-Path -LiteralPath $baseNameExpectation -PathType Leaf) {
        return $baseNameExpectation
    }

    $fullNameExpectation = "$($Fixture.FullName).expected.json"
    if (Test-Path -LiteralPath $fullNameExpectation -PathType Leaf) {
        return $fullNameExpectation
    }

    return $baseNameExpectation
}

function Get-NegativeFixtures {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Directory,

        [Parameter(Mandatory = $true)]
        [string[]] $Extensions,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if (-not (Test-Path -LiteralPath $Directory -PathType Container)) {
        throw "$Description directory '$Directory' is required for packaged-tool qualification."
    }

    $fixtures = @(
        Get-ChildItem -LiteralPath $Directory -File -Recurse |
            Where-Object {
                $_.Extension -in $Extensions -and
                -not $_.Name.EndsWith('.expected.json', [System.StringComparison]::OrdinalIgnoreCase)
            } |
            Sort-Object -Property FullName
    )
    if ($fixtures.Count -eq 0) {
        throw "$Description directory '$Directory' must contain one or more blocking negative fixtures."
    }

    foreach ($fixture in $fixtures) {
        $expectedPath = Get-FixtureExpectationPath -Fixture $fixture
        if (-not (Test-Path -LiteralPath $expectedPath -PathType Leaf)) {
            throw "Negative fixture '$($fixture.FullName)' requires '$expectedPath' with exitCode and ruleId."
        }
    }

    return $fixtures
}

function Assert-PositiveResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [string] $Description
    )

    if ($Result.ExitCode -ne 0) {
        throw "$Description was expected to pass but exited $($Result.ExitCode)."
    }
}

function Assert-NegativeResult {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Result,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo] $Fixture
    )

    $expectedPath = Get-FixtureExpectationPath -Fixture $Fixture
    $expected = Get-Content -LiteralPath $expectedPath -Raw | ConvertFrom-Json
    if ($null -eq $expected.exitCode -or [string]::IsNullOrWhiteSpace($expected.ruleId)) {
        throw "Negative fixture expectation '$expectedPath' must contain nonzero exitCode and ruleId."
    }

    if ([int]$expected.exitCode -eq 0) {
        throw "Negative fixture expectation '$expectedPath' cannot declare a successful exit code."
    }

    if ($Result.ExitCode -ne [int]$expected.exitCode) {
        throw "Negative fixture '$($Fixture.FullName)' exited $($Result.ExitCode), expected $($expected.exitCode)."
    }

    if (-not $Result.Output.Contains([string]$expected.ruleId, [System.StringComparison]::Ordinal)) {
        throw "Negative fixture '$($Fixture.FullName)' did not report expected stable rule ID '$($expected.ruleId)'."
    }
}

$consumerRoot = $null

try {
    if (-not $SkipSourceValidation) {
        $solutionPath = Join-Path $repositoryRoot 'Hexalith.Builds.slnx'
        Invoke-DotNet -Arguments @('restore', $solutionPath)
        Invoke-DotNet -Arguments @('build', $solutionPath, '--configuration', 'Release', '--no-restore', '-p:GeneratePackageOnBuild=false')

        $testProjects = @(
            Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'test') -File -Filter '*.csproj' -Recurse |
                Sort-Object -Property FullName
        )
        if ($testProjects.Count -eq 0) {
            throw 'No G-4 test projects were found under test/.'
        }

        foreach ($testProject in $testProjects) {
            Invoke-DotNet -Arguments @('test', $testProject.FullName, '--configuration', 'Release', '--no-restore')
        }
    }

    $buildScript = Join-Path $PSScriptRoot 'build-g4-tool-packages.ps1'
    & $buildScript -Version $Version -OutputDirectory $packageDirectoryPath
    if ($LASTEXITCODE -ne 0) {
        throw "G-4 package build script failed with exit code $LASTEXITCODE."
    }

    $consumerRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-builds-g4-consumer-$([System.Guid]::NewGuid().ToString('N'))"
    $toolManifestDirectory = Join-Path $consumerRoot '.config'
    $toolManifestPath = Join-Path $toolManifestDirectory 'dotnet-tools.json'
    $nuGetConfigPath = Join-Path $consumerRoot 'NuGet.Config'
    $null = New-Item -ItemType Directory -Path $toolManifestDirectory -Force

    $toolManifest = [ordered]@{
        version = 1
        isRoot = $true
        tools = [ordered]@{
            'hexalith.builds.module.cli' = [ordered]@{
                version = $Version
                commands = @('hexalith-module')
            }
            'hexalith.builds.evidence.cli' = [ordered]@{
                version = $Version
                commands = @('hexalith-evidence')
            }
        }
    }
    $toolManifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $toolManifestPath -Encoding utf8

    $env:NUGET_PACKAGES = Join-Path $consumerRoot '.nuget/packages'
    $env:DOTNET_CLI_HOME = Join-Path $consumerRoot '.dotnet'
    $consumerEnvironmentConfigured = $true

    $escapedPackageDirectory = [System.Security.SecurityElement]::Escape($packageDirectoryPath)
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="g4-local" value="$escapedPackageDirectory" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $nuGetConfigPath -Encoding utf8

    Invoke-DotNet -Arguments @('tool', 'restore', '--configfile', $nuGetConfigPath, '--no-http-cache') -WorkingDirectory $consumerRoot

    Assert-PositiveResult -Description 'hexalith-module help' -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('--help') -WorkingDirectory $consumerRoot)
    Assert-PositiveResult -Description 'hexalith-module run help' -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('run', '--help') -WorkingDirectory $consumerRoot)
    Assert-PositiveResult -Description 'hexalith-module down help' -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('down', '--help') -WorkingDirectory $consumerRoot)
    Assert-PositiveResult -Description 'hexalith-module test help' -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('test', '--help') -WorkingDirectory $consumerRoot)
    Assert-PositiveResult -Description 'hexalith-evidence help' -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('--help') -WorkingDirectory $consumerRoot)
    Assert-PositiveResult -Description 'hexalith-evidence validate help' -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', '--help') -WorkingDirectory $consumerRoot)

    if ($RequireControls) {
        $modulePositive = Get-RequiredFixture -Directory (Join-Path $fixtureRootPath 'module/positive') -Extensions @('.json') -Description 'Positive module manifest'
        $evidencePositive = Get-RequiredFixture -Directory (Join-Path $fixtureRootPath 'evidence/positive') -Extensions @('.yaml', '.yml') -Description 'Positive readiness evidence'
        $moduleNegatives = Get-NegativeFixtures -Directory (Join-Path $fixtureRootPath 'module/negative') -Extensions @('.json') -Description 'Module manifest negative control'
        $evidenceNegatives = Get-NegativeFixtures -Directory (Join-Path $fixtureRootPath 'evidence/negative') -Extensions @('.yaml', '.yml') -Description 'Readiness evidence negative control'

        Assert-PositiveResult -Description 'Positive module manifest' -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('down', '--manifest', $modulePositive.FullName, '--output', 'json') -WorkingDirectory $consumerRoot)
        Assert-PositiveResult -Description 'Positive readiness evidence' -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', $evidencePositive.FullName, '--output', 'json') -WorkingDirectory $consumerRoot)

        foreach ($fixture in $moduleNegatives) {
            Assert-NegativeResult -Fixture $fixture -Result (Invoke-ToolCommand -Command 'hexalith-module' -Arguments @('down', '--manifest', $fixture.FullName, '--output', 'json') -WorkingDirectory $consumerRoot)
        }

        foreach ($fixture in $evidenceNegatives) {
            Assert-NegativeResult -Fixture $fixture -Result (Invoke-ToolCommand -Command 'hexalith-evidence' -Arguments @('validate', $fixture.FullName, '--output', 'json') -WorkingDirectory $consumerRoot)
        }
    }

    Write-Host "Packed G-4 tool contract qualification passed for version '$Version'."
}
finally {
    if ($consumerEnvironmentConfigured) {
        if ($hadNuGetPackages) {
            $env:NUGET_PACKAGES = $previousNuGetPackages
        }
        else {
            Remove-Item -LiteralPath Env:NUGET_PACKAGES
        }

        if ($hadDotNetCliHome) {
            $env:DOTNET_CLI_HOME = $previousDotNetCliHome
        }
        else {
            Remove-Item -LiteralPath Env:DOTNET_CLI_HOME
        }
    }

    if ($null -ne $consumerRoot -and (Test-Path -LiteralPath $consumerRoot)) {
        Remove-Item -LiteralPath $consumerRoot -Recurse -Force
    }

    if ($ownsPackageDirectory -and (Test-Path -LiteralPath $packageDirectoryPath)) {
        Remove-Item -LiteralPath $packageDirectoryPath -Recurse -Force
    }
}
