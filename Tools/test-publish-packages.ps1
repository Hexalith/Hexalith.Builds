#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publisherPath = Join-Path $repositoryRoot 'Github/scripts/publish-packages.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-package-publisher-$([System.Guid]::NewGuid().ToString('N'))"
$packageIds = @('Hexalith.Example.Core', 'Hexalith.Example.Generators')
$originalGitHubToken = $env:GITHUB_TOKEN
$originalNuGetApiKey = $env:NUGET_API_KEY
$global:HexalithPackagePublisherTestInvocations = [System.Collections.Generic.List[string]]::new()

function global:dotnet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]] $ForwardedArguments
    )

    $global:HexalithPackagePublisherTestInvocations.Add(($ForwardedArguments | ConvertTo-Json -Compress))
    $global:LASTEXITCODE = 0
}

function Assert-Equal {
    param(
        [Parameter(Mandatory = $true)]
        [object] $Actual,

        [Parameter(Mandatory = $true)]
        [object] $Expected,

        [Parameter(Mandatory = $true)]
        [string] $Because
    )

    if ($Actual -cne $Expected) {
        throw "$Because Expected '$Expected', actual '$Actual'."
    }
}

function New-PackageTree {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion
    )

    $sourceRoot = Join-Path $testRoot 'src'
    if (Test-Path -LiteralPath $sourceRoot) {
        Remove-Item -LiteralPath $sourceRoot -Recurse -Force
    }

    foreach ($packageId in $packageIds) {
        $packageDirectory = Join-Path $sourceRoot "libraries/$packageId/bin/Release"
        $null = New-Item -ItemType Directory -Path $packageDirectory -Force
        [System.IO.File]::WriteAllText(
            (Join-Path $packageDirectory "$packageId.$PublishedVersion.nupkg"),
            "primary:$packageId",
            [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText(
            (Join-Path $packageDirectory "$packageId.$PublishedVersion.snupkg"),
            "symbols:$packageId",
            [System.Text.UTF8Encoding]::new($false))
    }
}

function Invoke-PublisherCase {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource
    )

    New-PackageTree -PublishedVersion $PublishedVersion
    $expectedPrimaryNames = @($packageIds | ForEach-Object { "$($_).$PublishedVersion.nupkg" } | Sort-Object)
    $global:HexalithPackagePublisherTestInvocations.Clear()

    Push-Location -LiteralPath $testRoot
    try {
        & $publisherPath -Version $PublishedVersion
    }
    finally {
        Pop-Location
    }

    Assert-Equal -Actual $global:HexalithPackagePublisherTestInvocations.Count -Expected 2 -Because 'The publisher must invoke dotnet once per primary package.'

    $publishedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($serializedInvocation in $global:HexalithPackagePublisherTestInvocations) {
        $arguments = @($serializedInvocation | ConvertFrom-Json)
        Assert-Equal -Actual $arguments[0] -Expected 'nuget' -Because 'The publisher must use the NuGet command group.'
        Assert-Equal -Actual $arguments[1] -Expected 'push' -Because 'The publisher must use the NuGet push command.'

        $artifactPath = [string] $arguments[2]
        if ($artifactPath.EndsWith('.snupkg', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The symbol package '$artifactPath' must not be submitted directly; dotnet pushes it with the adjacent primary package."
        }
        if ($arguments -contains '--no-symbols') {
            throw 'The publisher must leave automatic adjacent symbol-package publication enabled.'
        }
        if ($arguments -contains '--api-key') {
            throw 'The publisher must keep the API key out of process arguments.'
        }

        $publishedNames.Add([System.IO.Path]::GetFileName($artifactPath))
        $sourceIndex = [Array]::IndexOf($arguments, '--source')
        if ($sourceIndex -lt 0 -or $sourceIndex + 1 -ge $arguments.Count) {
            throw 'The publisher invocation is missing its package source.'
        }

        Assert-Equal -Actual $arguments[$sourceIndex + 1] -Expected $ExpectedSource -Because 'The publisher must route the version to the expected feed.'

        $configIndex = [Array]::IndexOf($arguments, '--configfile')
        if ($configIndex -lt 0 -or $configIndex + 1 -ge $arguments.Count) {
            throw 'The publisher invocation is missing its restricted-permission NuGet config file.'
        }
    }

    Assert-Equal -Actual (($publishedNames | Sort-Object) -join '|') -Expected ($expectedPrimaryNames -join '|') -Because 'The publisher must submit exactly the primary packages.'
}

try {
    $null = New-Item -ItemType Directory -Path $testRoot -Force

    $env:NUGET_API_KEY = 'stable-test-token'
    $env:GITHUB_TOKEN = $null
    Invoke-PublisherCase -PublishedVersion '9.8.7' -ExpectedSource 'https://api.nuget.org/v3/index.json'

    $env:NUGET_API_KEY = $null
    $env:GITHUB_TOKEN = 'prerelease-test-token'
    Invoke-PublisherCase -PublishedVersion '9.8.7-preview.1' -ExpectedSource 'https://nuget.pkg.github.com/Hexalith/index.json'
}
finally {
    $env:GITHUB_TOKEN = $originalGitHubToken
    $env:NUGET_API_KEY = $originalNuGetApiKey
    Remove-Item Function:\dotnet -Force -ErrorAction SilentlyContinue
    Remove-Variable -Name HexalithPackagePublisherTestInvocations -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

[Console]::Out.WriteLine('Legacy package publisher checks passed.')
