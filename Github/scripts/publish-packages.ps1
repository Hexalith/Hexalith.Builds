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
        [string]$ConfigFile,
        [string]$Source
    )

    $files = Get-ChildItem -Path './src/libraries' -Recurse -Filter "*.$Extension" -ErrorAction SilentlyContinue
    if (-not $files) {
        Write-Host "No *.$Extension packages found to publish."
        return
    }

    # No --skip-duplicate: a version that already exists on the feed means the
    # release is republishing content under a version someone may already have
    # restored. Fail closed and let the operator resolve it, matching the modern
    # chain's version-collision preflight.
    # The API key comes from the restricted-permission config file rather than
    # argv, so it is not visible in the process table to anything sharing the runner.
    dotnet nuget push "./src/libraries/**/*.$Extension" --source $Source --configfile $ConfigFile
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to publish *.$Extension packages (exit code $LASTEXITCODE)."
    }
}

function New-PublishConfigFile {
    param(
        [string]$ApiKey,
        [string]$Source
    )

    if ([string]::IsNullOrWhiteSpace($ApiKey)) {
        throw 'No API key is available for the target feed. Set NUGET_API_KEY (stable) or GITHUB_TOKEN (prerelease).'
    }

    $configFile = Join-Path ([System.IO.Path]::GetTempPath()) "hexalith-publish-$([System.Guid]::NewGuid().ToString('N')).config"

    # Create the file empty and lock it down before the key is written, so the
    # secret is never briefly readable by other users on the runner.
    $null = New-Item -ItemType File -Path $configFile -Force
    if (-not $IsWindows) {
        & chmod 600 $configFile
        if ($LASTEXITCODE -ne 0) {
            throw "Unable to restrict permissions on the publish config file (exit code $LASTEXITCODE)."
        }
    }

    $escapedSource = [System.Security.SecurityElement]::Escape($Source)
    $escapedApiKey = [System.Security.SecurityElement]::Escape($ApiKey)
    $content = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <apikeys>
    <add key="$escapedSource" value="$escapedApiKey" />
  </apikeys>
</configuration>
"@
    Set-Content -LiteralPath $configFile -Value $content -Encoding utf8 -NoNewline

    return $configFile
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

$configFile = New-PublishConfigFile -ApiKey $apiKey -Source $source
try {
    Publish-Packages -Extension 'nupkg' -ConfigFile $configFile -Source $source
    Publish-Packages -Extension 'snupkg' -ConfigFile $configFile -Source $source
}
finally {
    Remove-Item -LiteralPath $configFile -Force -ErrorAction SilentlyContinue
}
