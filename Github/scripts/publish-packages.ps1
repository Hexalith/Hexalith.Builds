#!/usr/bin/env pwsh
# Publishes the NuGet packages produced under ./src/libraries.
# Invoked by semantic-release (@semantic-release/exec publishCmd) during a release.
# Pre-releases (version contains '-') go to GitHub Packages using GITHUB_TOKEN;
# stable versions go to NuGet.org using NUGET_API_KEY.
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version
)

$ErrorActionPreference = 'Stop'

function Publish-Packages {
    param(
        [string]$Extension,
        [string]$ApiKey,
        [string]$Source
    )

    $files = Get-ChildItem -Path './src/libraries' -Recurse -Filter "*.$Extension" -ErrorAction SilentlyContinue
    if (-not $files) {
        Write-Host "No *.$Extension packages found to publish."
        return
    }

    dotnet nuget push "./src/libraries/**/*.$Extension" --api-key $ApiKey --source $Source --skip-duplicate
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish *.$Extension packages (exit code $LASTEXITCODE)."
    }
}

if ($Version -like '*-*') {
    Write-Host "Publishing pre-release $Version to GitHub Packages"
    $apiKey = $env:GITHUB_TOKEN
    $source = 'https://nuget.pkg.github.com/Hexalith/index.json'
}
else {
    Write-Host "Publishing release $Version to NuGet.org"
    $apiKey = $env:NUGET_API_KEY
    $source = 'https://api.nuget.org/v3/index.json'
}

Publish-Packages -Extension 'nupkg' -ApiKey $apiKey -Source $source
Publish-Packages -Extension 'snupkg' -ApiKey $apiKey -Source $source
