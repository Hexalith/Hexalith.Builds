#!/usr/bin/env pwsh

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$publisherPath = Join-Path $PSScriptRoot 'publish-g4-tool-packages.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-g4-publisher-$([System.Guid]::NewGuid().ToString('N'))"
$packageIds = @('Hexalith.Builds.Evidence.Cli', 'Hexalith.Builds.Module.Cli')
$originalGitHubToken = $env:GITHUB_TOKEN
$originalNuGetApiKey = $env:NUGET_API_KEY
$global:HexalithG4PublisherTestInvocations = [System.Collections.Generic.List[string]]::new()

function global:dotnet {
    param(
        [Parameter(ValueFromRemainingArguments = $true)]
        [object[]] $ForwardedArguments
    )

    $global:HexalithG4PublisherTestInvocations.Add(($ForwardedArguments | ConvertTo-Json -Compress))
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

function New-PackageInventory {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion
    )

    $packageDirectory = Join-Path $testRoot $PublishedVersion
    $null = New-Item -ItemType Directory -Path $packageDirectory -Force
    $packages = [System.Collections.Generic.List[object]]::new()
    foreach ($packageId in $packageIds) {
        $nupkgName = "$packageId.$PublishedVersion.nupkg"
        $snupkgName = "$packageId.$PublishedVersion.snupkg"
        $nupkgPath = Join-Path $packageDirectory $nupkgName
        $snupkgPath = Join-Path $packageDirectory $snupkgName
        [System.IO.File]::WriteAllText($nupkgPath, "primary:$packageId", [System.Text.UTF8Encoding]::new($false))
        [System.IO.File]::WriteAllText($snupkgPath, "symbols:$packageId", [System.Text.UTF8Encoding]::new($false))

        $packages.Add([ordered]@{
            id = $packageId
            nupkg = [ordered]@{
                file = $nupkgName
                sha256 = (Get-FileHash -LiteralPath $nupkgPath -Algorithm SHA256).Hash
            }
            snupkg = [ordered]@{
                file = $snupkgName
                sha256 = (Get-FileHash -LiteralPath $snupkgPath -Algorithm SHA256).Hash
            }
        })
    }

    $inventory = [ordered]@{
        schema = 'hexalith.g4-tool-package-inventory.v1'
        version = $PublishedVersion
        configuration = 'Release'
        packages = $packages
    }
    $inventoryPath = Join-Path $packageDirectory 'g4-tool-package-inventory.json'
    [System.IO.File]::WriteAllText($inventoryPath, ($inventory | ConvertTo-Json -Depth 5), [System.Text.UTF8Encoding]::new($false))

    return $packageDirectory
}

function Invoke-PublisherCase {
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishedVersion,

        [Parameter(Mandatory = $true)]
        [string] $ExpectedSource
    )

    $packageDirectory = New-PackageInventory -PublishedVersion $PublishedVersion
    $expectedPrimaryNames = @($packageIds | ForEach-Object { "$($_).$PublishedVersion.nupkg" } | Sort-Object)
    $global:HexalithG4PublisherTestInvocations.Clear()
    & $publisherPath -Version $PublishedVersion -PackageDirectory $packageDirectory

    Assert-Equal -Actual $global:HexalithG4PublisherTestInvocations.Count -Expected 2 -Because 'The publisher must invoke dotnet once per primary package.'

    $publishedNames = [System.Collections.Generic.List[string]]::new()
    foreach ($serializedInvocation in $global:HexalithG4PublisherTestInvocations) {
        $arguments = @($serializedInvocation | ConvertFrom-Json)
        Assert-Equal -Actual $arguments[0] -Expected 'nuget' -Because 'The publisher must use the NuGet command group.'
        Assert-Equal -Actual $arguments[1] -Expected 'push' -Because 'The publisher must use the NuGet push command.'

        $artifactPath = [string]$arguments[2]
        if ($artifactPath.EndsWith('.snupkg', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "The symbol package '$artifactPath' must not be submitted directly; dotnet pushes the adjacent symbol package with its primary package."
        }
        if ($arguments -contains '--no-symbols') {
            throw 'The publisher must leave automatic adjacent symbol-package publication enabled.'
        }

        $publishedNames.Add([System.IO.Path]::GetFileName($artifactPath))
        $sourceIndex = [Array]::IndexOf($arguments, '--source')
        if ($sourceIndex -lt 0 -or $sourceIndex + 1 -ge $arguments.Count) {
            throw 'The publisher invocation is missing its package source.'
        }

        Assert-Equal -Actual $arguments[$sourceIndex + 1] -Expected $ExpectedSource -Because 'The publisher must route the version to the expected feed.'
    }

    Assert-Equal -Actual (($publishedNames | Sort-Object) -join '|') -Expected ($expectedPrimaryNames -join '|') -Because 'The publisher must submit exactly the two approved primary packages.'
}

try {
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
    Remove-Variable -Name HexalithG4PublisherTestInvocations -Scope Global -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}

[Console]::Out.WriteLine('G-4 tool package publisher checks passed.')
